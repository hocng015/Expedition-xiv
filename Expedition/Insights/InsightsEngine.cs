using System.Net.Http;
using System.Text.Json;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace Expedition.Insights;

/// <summary>
/// Background data orchestrator for the Insights tab.
/// Fetches pre-computed market data from the Expedition bot's API server,
/// enriches items with Lumina names/icons, and exposes an InsightsSnapshot
/// for the UI thread to read lock-free.
///
/// Key design:
/// - Bot server handles all Universalis fetching, caching, and ranking
/// - Plugin just does: one HTTP GET → parse JSON → enrich with Lumina → swap snapshot
/// - No more rate limiting, batching, or retry complexity on the plugin side
/// </summary>
public sealed class InsightsEngine : IDisposable
{
    private const string BotApiBase = "https://expedition-bot-production.up.railway.app";
    private const int RequestTimeoutMs = 30000; // 30s — server may need time on first fetch

    private readonly HttpClient httpClient;
    private readonly ExcelSheet<Item> itemSheet;

    // Atomic snapshot for lock-free UI reading
    private InsightsSnapshot currentSnapshot;
    public InsightsSnapshot CurrentSnapshot => Volatile.Read(ref currentSnapshot);

    // Background refresh state
    private CancellationTokenSource? refreshCts;
    private DateTime lastRefreshTime = DateTime.MinValue;
    private volatile bool isRefreshing;
    public bool IsRefreshing => isRefreshing;

    // Selected data center (set from UI)
    private string selectedDataCenter;
    public string SelectedDataCenter
    {
        get => selectedDataCenter;
        set => selectedDataCenter = value;
    }

    public InsightsEngine()
    {
        httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(RequestTimeoutMs),
        };
        httpClient.DefaultRequestHeaders.Add("User-Agent", "Expedition-FFXIV-Plugin");

        itemSheet = DalamudApi.DataManager.GetExcelSheet<Item>()!;

        selectedDataCenter = Expedition.Config.InsightsDefaultDataCenter;
        if (string.IsNullOrEmpty(selectedDataCenter))
            selectedDataCenter = "Aether";

        currentSnapshot = new InsightsSnapshot
        {
            IsLoading = true,
            FetchedAt = DateTime.MinValue,
            DataCenterName = selectedDataCenter,
        };

        // Begin fetching immediately in the background
        TriggerRefresh();
    }

    /// <summary>
    /// Called from Framework.Update. Checks if a refresh is due and triggers one.
    /// </summary>
    public void Update()
    {
        if (isRefreshing) return;

        var refreshIntervalMinutes = Expedition.Config.InsightsRefreshIntervalMinutes;
        if (refreshIntervalMinutes <= 0) return;

        if ((DateTime.UtcNow - lastRefreshTime).TotalMinutes >= refreshIntervalMinutes)
            TriggerRefresh();
    }

    /// <summary>
    /// Manually triggers a data refresh. Safe to call from the UI thread.
    /// </summary>
    public void TriggerRefresh()
    {
        if (isRefreshing) return;
        isRefreshing = true;

        refreshCts?.Cancel();
        refreshCts = new CancellationTokenSource();
        var ct = refreshCts.Token;

        Task.Run(async () =>
        {
            try
            {
                var snapshot = await FetchSnapshotFromBotAsync(ct);
                Interlocked.Exchange(ref currentSnapshot, snapshot);
                lastRefreshTime = DateTime.UtcNow;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                DalamudApi.Log.Error(ex, "[Insights] Refresh failed");
                var errorSnapshot = new InsightsSnapshot
                {
                    FetchedAt = DateTime.UtcNow,
                    DataCenterName = selectedDataCenter,
                    ErrorMessage = string.Concat("Refresh failed: ", ex.Message),
                };
                Interlocked.Exchange(ref currentSnapshot, errorSnapshot);
            }
            finally
            {
                isRefreshing = false;
            }
        }, ct);
    }

    /// <summary>
    /// Fetches the pre-computed snapshot from the bot's API server
    /// and transforms it into an InsightsSnapshot with Lumina-enriched names/icons.
    /// </summary>
    private async Task<InsightsSnapshot> FetchSnapshotFromBotAsync(CancellationToken ct)
    {
        var dc = selectedDataCenter;
        var url = string.Concat(BotApiBase, "/api/insights?dc=", dc);

        DalamudApi.Log.Information($"[Insights] Fetching snapshot from bot API: {url}");

        string json;
        try
        {
            var response = await httpClient.GetAsync(url, ct);

            if ((int)response.StatusCode == 503)
            {
                DalamudApi.Log.Information("[Insights] Bot server is still building data, will retry shortly");
                return new InsightsSnapshot
                {
                    IsLoading = true,
                    FetchedAt = DateTime.MinValue,
                    DataCenterName = dc,
                    ErrorMessage = "Server is preparing data, please wait...",
                };
            }

            response.EnsureSuccessStatusCode();
            json = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is TaskCanceledException or HttpRequestException)
        {
            DalamudApi.Log.Warning(ex, "[Insights] Bot API request failed");
            throw;
        }

        DalamudApi.Log.Information($"[Insights] Received {json.Length} bytes from bot API");
        return ParseBotResponse(json, dc);
    }

    /// <summary>
    /// Parses the bot API's JSON response into an InsightsSnapshot.
    /// Uses System.Text.Json with JsonDocument (project standard).
    /// </summary>
    private InsightsSnapshot ParseBotResponse(string json, string dcName)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Check for error
        if (root.TryGetProperty("error", out var errorEl))
        {
            return new InsightsSnapshot
            {
                FetchedAt = DateTime.UtcNow,
                DataCenterName = dcName,
                ErrorMessage = errorEl.GetString() ?? "Unknown error",
            };
        }

        // Parse ranked lists
        var hottestItems = ParseItemList(root, "hottest");
        var highestGilVolume = ParseItemList(root, "highest_gil");
        var mostExpensive = ParseItemList(root, "most_expensive");

        // Parse category summaries
        var categorySummaries = new List<CategorySummary>();
        if (root.TryGetProperty("categories", out var catsEl) &&
            catsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var catEl in catsEl.EnumerateArray())
            {
                MarketItemData? topItem = null;
                if (catEl.TryGetProperty("topItem", out var topEl) &&
                    topEl.ValueKind == JsonValueKind.Object)
                {
                    topItem = ParseSingleItem(topEl);
                }

                categorySummaries.Add(new CategorySummary
                {
                    CategoryName = GetString(catEl, "name"),
                    ItemCount = GetInt(catEl, "count"),
                    TotalDailyVelocity = GetFloat(catEl, "totalVelocity"),
                    AveragePrice = GetFloat(catEl, "avgPrice"),
                    EstimatedDailyGilVolume = GetFloat(catEl, "gilVolume"),
                    TopItem = topItem,
                });
            }
        }

        // Parse items by category
        var itemsByCategory = new Dictionary<string, List<MarketItemData>>();
        if (root.TryGetProperty("items_by_category", out var ibcEl) &&
            ibcEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in ibcEl.EnumerateObject())
            {
                var items = new List<MarketItemData>();
                if (prop.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var itemEl in prop.Value.EnumerateArray())
                    {
                        var item = ParseSingleItem(itemEl);
                        if (item != null)
                        {
                            item.CategoryName = prop.Name;
                            items.Add(item);
                        }
                    }
                }
                itemsByCategory[prop.Name] = items;
            }
        }

        // Parse activity feed
        var recentActivity = new List<RecentlyUpdatedItem>();
        if (root.TryGetProperty("recent_activity", out var actEl) &&
            actEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in actEl.EnumerateArray())
            {
                var itemId = (uint)GetInt(entry, "id");
                if (itemId == 0) continue;

                var uploadMs = entry.TryGetProperty("uploadTime", out var utEl) && utEl.TryGetInt64(out var ms)
                    ? ms
                    : 0L;

                var item = new RecentlyUpdatedItem
                {
                    ItemId = itemId,
                    WorldName = GetString(entry, "world"),
                    LastUploadTime = uploadMs > 0
                        ? DateTimeOffset.FromUnixTimeMilliseconds(uploadMs).UtcDateTime
                        : DateTime.MinValue,
                };

                // Enrich with Lumina name/icon
                var lumiItem = itemSheet.GetRowOrDefault(itemId);
                if (lumiItem != null)
                {
                    item.ItemName = lumiItem.Value.Name.ExtractText();
                    item.IconId = (uint)lumiItem.Value.Icon;
                }

                recentActivity.Add(item);
            }
        }

        var totalItems = root.TryGetProperty("total_items", out var tiEl) && tiEl.TryGetInt32(out var ti)
            ? ti
            : hottestItems.Count;

        DalamudApi.Log.Information(
            $"[Insights] Parsed snapshot: {totalItems} items, {categorySummaries.Count} categories, {recentActivity.Count} activity entries");

        // Parse Saddlebag Exchange analytics if present
        SaddlebagData? saddlebag = null;
        if (root.TryGetProperty("saddlebag", out var sbEl) &&
            sbEl.ValueKind == JsonValueKind.Object)
        {
            saddlebag = ParseSaddlebagData(sbEl);
        }

        return new InsightsSnapshot
        {
            FetchedAt = DateTime.UtcNow,
            DataCenterName = dcName,
            HottestItems = hottestItems,
            HighestGilVolume = highestGilVolume,
            MostExpensive = mostExpensive,
            CategorySummaries = categorySummaries,
            ItemsByCategory = itemsByCategory,
            RecentActivity = recentActivity,
            Saddlebag = saddlebag,
        };
    }

    // ──────────────────────────────────────────────
    // JSON Parsing Helpers
    // ──────────────────────────────────────────────

    private List<MarketItemData> ParseItemList(JsonElement root, string propertyName)
    {
        var items = new List<MarketItemData>();
        if (!root.TryGetProperty(propertyName, out var arrEl) ||
            arrEl.ValueKind != JsonValueKind.Array)
            return items;

        foreach (var itemEl in arrEl.EnumerateArray())
        {
            var item = ParseSingleItem(itemEl);
            if (item != null) items.Add(item);
        }

        return items;
    }

    private MarketItemData? ParseSingleItem(JsonElement el)
    {
        var itemId = (uint)GetInt(el, "id");
        if (itemId == 0) return null;

        var data = new MarketItemData
        {
            ItemId = itemId,
            RegularSaleVelocity = GetFloat(el, "vel"),
            NqSaleVelocity = GetFloat(el, "nqVel"),
            HqSaleVelocity = GetFloat(el, "hqVel"),
            CurrentAveragePrice = GetFloat(el, "avgPrice"),
            CurrentAveragePriceNQ = GetFloat(el, "avgPriceNQ"),
            CurrentAveragePriceHQ = GetFloat(el, "avgPriceHQ"),
            MinPrice = GetFloat(el, "minPrice"),
            MaxPrice = GetFloat(el, "maxPrice"),
            UnitsForSale = GetInt(el, "supply"),
            UnitsSold = GetInt(el, "sold"),
            ListingsCount = GetInt(el, "listings"),
            CategoryName = GetString(el, "category"),
        };

        // Enrich with item name + icon from Lumina
        EnrichItemInfo(data);
        return data;
    }

    private void EnrichItemInfo(MarketItemData data)
    {
        var item = itemSheet.GetRowOrDefault(data.ItemId);
        if (item == null) return;
        data.ItemName = item.Value.Name.ExtractText();
        data.IconId = (uint)item.Value.Icon;
    }

    private static float GetFloat(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.TryGetSingle(out var f) ? f : 0;

    private static int GetInt(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.TryGetInt32(out var i) ? i : 0;

    private static string GetString(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    // ──────────────────────────────────────────────
    // Saddlebag Exchange Parsing
    // ──────────────────────────────────────────────

    private SaddlebagData ParseSaddlebagData(JsonElement sbEl)
    {
        var data = new SaddlebagData
        {
            MarketShare = ParseMarketShareList(sbEl),
            CraftProfit = ParseCraftProfitList(sbEl),
            ScripExchange = ParseScripExchangeList(sbEl),
            WeeklyTrends = ParseWeeklyTrendsList(sbEl),
        };

        DalamudApi.Log.Information(
            $"[Insights] Saddlebag parsed: {data.MarketShare.Count} market share, " +
            $"{data.CraftProfit.Count} craft profit, {data.ScripExchange.Count} scrip, " +
            $"{data.WeeklyTrends.Count} weekly trends");

        return data;
    }

    private List<SaddlebagMarketShareItem> ParseMarketShareList(JsonElement sbEl)
    {
        var items = new List<SaddlebagMarketShareItem>();
        if (!sbEl.TryGetProperty("market_share", out var arrEl) ||
            arrEl.ValueKind != JsonValueKind.Array)
            return items;

        foreach (var el in arrEl.EnumerateArray())
        {
            var itemId = (uint)GetInt(el, "itemID");
            var item = new SaddlebagMarketShareItem
            {
                ItemName = GetString(el, "name"),
                ItemId = itemId,
                State = GetString(el, "state"),
                AveragePrice = GetFloat(el, "avgPrice"),
                MedianPrice = GetFloat(el, "medianPrice"),
                MinPrice = GetFloat(el, "minPrice"),
                MarketValue = GetFloat(el, "marketValue"),
                QuantitySold = GetInt(el, "quantitySold"),
                PercentChange = GetFloat(el, "percentChange"),
                HomeMinPrice = GetFloat(el, "homeMinPrice"),
                HomeMedianPrice = GetFloat(el, "homeMedian"),
            };
            EnrichItemIcon(item.ItemId, out var iconId);
            item.IconId = iconId;
            items.Add(item);
        }

        return items;
    }

    private List<SaddlebagCraftProfitItem> ParseCraftProfitList(JsonElement sbEl)
    {
        var items = new List<SaddlebagCraftProfitItem>();
        if (!sbEl.TryGetProperty("craft_profit", out var arrEl) ||
            arrEl.ValueKind != JsonValueKind.Array)
            return items;

        foreach (var el in arrEl.EnumerateArray())
        {
            var itemId = (uint)GetInt(el, "itemID");
            var item = new SaddlebagCraftProfitItem
            {
                ItemName = GetString(el, "name"),
                ItemId = itemId,
                Revenue = GetFloat(el, "revenue"),
                CraftingCost = GetFloat(el, "craftingCost"),
                Profit = GetFloat(el, "profit"),
                ProfitPercent = GetFloat(el, "profitPct"),
                AverageSold = GetFloat(el, "avgSold"),
                MedianPrice = GetFloat(el, "medianPrice"),
                MinPrice = GetFloat(el, "minPrice"),
                HomeMinPrice = GetFloat(el, "homeMinPrice"),
                SalesAmount = GetInt(el, "salesAmount"),
                Job = GetString(el, "job"),
            };
            EnrichItemIcon(item.ItemId, out var iconId);
            item.IconId = iconId;
            items.Add(item);
        }

        return items;
    }

    private List<SaddlebagScripItem> ParseScripExchangeList(JsonElement sbEl)
    {
        var items = new List<SaddlebagScripItem>();
        if (!sbEl.TryGetProperty("scrip_exchange", out var arrEl) ||
            arrEl.ValueKind != JsonValueKind.Array)
            return items;

        foreach (var el in arrEl.EnumerateArray())
        {
            var itemId = (uint)GetInt(el, "itemID");
            var item = new SaddlebagScripItem
            {
                ItemName = GetString(el, "name"),
                ItemId = itemId,
                ScripCost = GetInt(el, "scripCost"),
                MarketPrice = GetFloat(el, "marketPrice"),
                MinPrice = GetFloat(el, "minPrice"),
                HomeMinPrice = GetFloat(el, "homeMinPrice"),
                GilPerScrip = GetFloat(el, "gilPerScrip"),
                QuantitySold = GetInt(el, "quantitySold"),
            };
            EnrichItemIcon(item.ItemId, out var iconId);
            item.IconId = iconId;
            items.Add(item);
        }

        return items;
    }

    private List<SaddlebagWeeklyTrendItem> ParseWeeklyTrendsList(JsonElement sbEl)
    {
        var items = new List<SaddlebagWeeklyTrendItem>();
        if (!sbEl.TryGetProperty("weekly_trends", out var arrEl) ||
            arrEl.ValueKind != JsonValueKind.Array)
            return items;

        foreach (var el in arrEl.EnumerateArray())
        {
            var itemId = (uint)GetInt(el, "itemID");
            var item = new SaddlebagWeeklyTrendItem
            {
                ItemName = GetString(el, "name"),
                ItemId = itemId,
                CurrentAverage = GetFloat(el, "currentAvg"),
                PreviousAverage = GetFloat(el, "previousAvg"),
                PriceDelta = GetFloat(el, "priceDelta"),
                PercentChange = GetFloat(el, "percentChange"),
                SalesAmount = GetInt(el, "salesAmount"),
            };
            EnrichItemIcon(item.ItemId, out var iconId);
            item.IconId = iconId;
            items.Add(item);
        }

        return items;
    }

    private void EnrichItemIcon(uint itemId, out uint iconId)
    {
        iconId = 0;
        if (itemId == 0) return;
        var item = itemSheet.GetRowOrDefault(itemId);
        if (item == null) return;
        iconId = (uint)item.Value.Icon;
    }

    public void Dispose()
    {
        refreshCts?.Cancel();
        refreshCts?.Dispose();
        httpClient.Dispose();
    }
}
