using System.Net.Http;
using System.Text.Json;

namespace Expedition.Insights;

/// <summary>
/// HTTP client for the Universalis API. Handles batching, response parsing,
/// and error handling for market board data queries.
///
/// Follows the existing HttpClient pattern from MobDropLookupService:
/// - Single HttpClient instance with User-Agent header
/// - Configurable timeout
/// - Response size limits
/// - System.Text.Json with JsonDocument.Parse()
/// </summary>
public sealed class UniversalisService : IDisposable
{
    private const string BaseUrl = "https://universalis.app/api/v2";
    private const int RequestTimeoutMs = 15000;
    private const int MaxItemsPerBatch = 10; // Small batches — DC-level queries are heavy + avoid rate limits
    private const int InterBatchDelayMs = 500; // Pace requests generously to avoid 429s
    private const int MaxRetries = 2; // Retry failed batches up to 2 times
    private const int RetryDelayMs = 2000; // Wait 2s before retry
    private const int MaxResponseSizeBytes = 1024 * 1024; // 1MB

    public static readonly string[] NaDataCenters = { "Aether", "Primal", "Crystal", "Dynamis" };

    private readonly HttpClient httpClient;

    public UniversalisService()
    {
        httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(RequestTimeoutMs),
        };
        httpClient.DefaultRequestHeaders.Add("User-Agent", "Expedition-FFXIV-Plugin");
    }

    // ──────────────────────────────────────────────
    // Market Data (batched)
    // ──────────────────────────────────────────────

    /// <summary>
    /// Fetches market data for items on a single data center.
    /// Automatically batches into groups of MaxItemsPerBatch items per request.
    /// Uses noListings=1 to skip the full listings array — we only need aggregate stats.
    /// Failed batches are retried with exponential backoff, then individual fallback.
    /// </summary>
    public async Task<List<MarketItemData>> GetMarketDataAsync(
        string dcName, IReadOnlyList<uint> itemIds, CancellationToken ct = default)
    {
        if (itemIds.Count == 0) return new();

        var results = new List<MarketItemData>();
        var returnedIds = new HashSet<uint>();

        for (var i = 0; i < itemIds.Count; i += MaxItemsPerBatch)
        {
            ct.ThrowIfCancellationRequested();

            // Pace requests generously
            if (i > 0)
                await Task.Delay(InterBatchDelayMs, ct);

            // Build comma-separated ID string for this batch
            var end = Math.Min(i + MaxItemsPerBatch, itemIds.Count);
            var sb = new System.Text.StringBuilder((end - i) * 8);
            for (var j = i; j < end; j++)
            {
                if (j > i) sb.Append(',');
                sb.Append(itemIds[j]);
            }

            var url = string.Concat(BaseUrl, "/", dcName, "/", sb.ToString(), "?noListings=1");
            var success = false;

            // Retry loop for transient failures (rate limits, server errors)
            for (var attempt = 0; attempt <= MaxRetries; attempt++)
            {
                if (attempt > 0)
                {
                    var delay = RetryDelayMs * attempt; // Linear backoff: 2s, 4s
                    DalamudApi.Log.Information(
                        $"[Insights] Retry {attempt}/{MaxRetries} for batch {i / MaxItemsPerBatch} after {delay}ms");
                    await Task.Delay(delay, ct);
                }

                var countBefore = results.Count;
                var (json, statusCode) = await FetchJsonWithStatusAsync(url, ct);
                if (json != null)
                {
                    ParseMarketDataResponse(json, results);
                    // Track which IDs were returned
                    for (var k = countBefore; k < results.Count; k++)
                        returnedIds.Add(results[k].ItemId);
                    success = true;
                    break;
                }

                DalamudApi.Log.Warning(
                    $"[Insights] Batch {i / MaxItemsPerBatch} attempt {attempt}: HTTP {statusCode}. IDs: {sb}");

                // Don't retry 404s — the items genuinely don't exist on Universalis
                if (statusCode == 404) break;
            }

            // If the batch still failed after retries, try individual item queries as fallback
            if (!success)
            {
                DalamudApi.Log.Information(
                    $"[Insights] Batch {i / MaxItemsPerBatch} failed after retries, falling back to individual queries");
                for (var j = i; j < end; j++)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(InterBatchDelayMs, ct);

                    var singleUrl = string.Concat(BaseUrl, "/", dcName, "/", itemIds[j].ToString(), "?noListings=1");
                    var (singleJson, singleStatus) = await FetchJsonWithStatusAsync(singleUrl, ct);
                    if (singleJson != null)
                    {
                        var countBefore = results.Count;
                        ParseMarketDataResponse(singleJson, results);
                        for (var k = countBefore; k < results.Count; k++)
                            returnedIds.Add(results[k].ItemId);
                    }
                }
            }
        }

        return results;
    }

    // ──────────────────────────────────────────────
    // Most Recently Updated
    // ──────────────────────────────────────────────

    /// <summary>
    /// Fetches the most recently updated items for a data center.
    /// </summary>
    public async Task<List<RecentlyUpdatedItem>> GetRecentlyUpdatedAsync(
        string dcName, int entries = 50, CancellationToken ct = default)
    {
        var url = string.Concat(BaseUrl, "/extra/stats/most-recently-updated?dcName=", dcName, "&entries=", entries.ToString());
        var json = await FetchJsonAsync(url, ct);
        if (json == null) return new();

        return ParseRecentlyUpdated(json);
    }

    // ──────────────────────────────────────────────
    // History
    // ──────────────────────────────────────────────

    /// <summary>
    /// Fetches recent purchase history for items on a data center.
    /// </summary>
    public async Task<List<RecentSale>> GetHistoryAsync(
        string dcName, IReadOnlyList<uint> itemIds,
        int entriesWithin = 86400, CancellationToken ct = default)
    {
        if (itemIds.Count == 0) return new();

        var results = new List<RecentSale>();

        for (var i = 0; i < itemIds.Count; i += MaxItemsPerBatch)
        {
            ct.ThrowIfCancellationRequested();

            if (i > 0)
                await Task.Delay(InterBatchDelayMs, ct);

            var end = Math.Min(i + MaxItemsPerBatch, itemIds.Count);
            var sb = new System.Text.StringBuilder((end - i) * 8);
            for (var j = i; j < end; j++)
            {
                if (j > i) sb.Append(',');
                sb.Append(itemIds[j]);
            }

            var url = string.Concat(BaseUrl, "/history/", dcName, "/", sb.ToString(),
                "?entriesWithin=", entriesWithin.ToString());
            var json = await FetchJsonAsync(url, ct);
            if (json != null)
                ParseHistoryResponse(json, results);
        }

        return results;
    }

    // ──────────────────────────────────────────────
    // Shared HTTP Fetch
    // ──────────────────────────────────────────────

    /// <summary>
    /// Fetches JSON from a URL, returning the body and HTTP status code.
    /// Returns (null, statusCode) on failure so callers can decide whether to retry.
    /// </summary>
    private async Task<(string? Json, int StatusCode)> FetchJsonWithStatusAsync(string url, CancellationToken ct)
    {
        try
        {
            var response = await httpClient.GetAsync(url, ct);
            var statusCode = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode) return (null, statusCode);

            // Guard against oversized responses
            if (response.Content.Headers.ContentLength > MaxResponseSizeBytes)
                return (null, statusCode);

            var json = await response.Content.ReadAsStringAsync(ct);
            return json.Length > MaxResponseSizeBytes ? (null, statusCode) : (json, statusCode);
        }
        catch (TaskCanceledException)
        {
            DalamudApi.Log.Warning($"[Insights] Request timed out: {url}");
            return (null, 408); // Timeout
        }
        catch (HttpRequestException ex)
        {
            DalamudApi.Log.Warning(ex, $"[Insights] Request failed: {url}");
            return (null, 0); // Network error
        }
    }

    /// <summary>
    /// Simple fetch wrapper for endpoints that don't need retry logic.
    /// </summary>
    private async Task<string?> FetchJsonAsync(string url, CancellationToken ct)
    {
        var (json, _) = await FetchJsonWithStatusAsync(url, ct);
        return json;
    }

    // ──────────────────────────────────────────────
    // JSON Parsing
    // ──────────────────────────────────────────────

    /// <summary>
    /// Parses market data from Universalis.
    /// Handles both single-item (direct object) and multi-item (items dict) responses.
    /// </summary>
    private static void ParseMarketDataResponse(string json, List<MarketItemData> results)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Multi-item response has "items" object keyed by item ID string
            if (root.TryGetProperty("items", out var itemsObj) &&
                itemsObj.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in itemsObj.EnumerateObject())
                {
                    var data = ParseSingleMarketItem(prop.Value);
                    if (data != null) results.Add(data);
                }
            }
            else
            {
                // Single-item response
                var data = ParseSingleMarketItem(root);
                if (data != null) results.Add(data);
            }
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Warning(ex, "[Insights] Failed to parse market data response");
        }
    }

    private static MarketItemData? ParseSingleMarketItem(JsonElement el)
    {
        if (!el.TryGetProperty("itemID", out var idEl)) return null;

        // Extract per-world upload times (available even with noListings=1)
        var worldPrices = new List<WorldPriceSnapshot>();
        if (el.TryGetProperty("worldUploadTimes", out var worldTimes) &&
            worldTimes.ValueKind == JsonValueKind.Object)
        {
            foreach (var wt in worldTimes.EnumerateObject())
            {
                if (int.TryParse(wt.Name, out var wId) && wt.Value.TryGetInt64(out var ts))
                {
                    worldPrices.Add(new WorldPriceSnapshot
                    {
                        WorldId = wId,
                        LastUploadTime = ts,
                    });
                }
            }
        }

        return new MarketItemData
        {
            ItemId = idEl.GetUInt32(),
            CurrentAveragePrice = GetFloat(el, "currentAveragePrice"),
            CurrentAveragePriceNQ = GetFloat(el, "currentAveragePriceNQ"),
            CurrentAveragePriceHQ = GetFloat(el, "currentAveragePriceHQ"),
            MinPrice = GetFloat(el, "minPrice"),
            MaxPrice = GetFloat(el, "maxPrice"),
            RegularSaleVelocity = GetFloat(el, "regularSaleVelocity"),
            NqSaleVelocity = GetFloat(el, "nqSaleVelocity"),
            HqSaleVelocity = GetFloat(el, "hqSaleVelocity"),
            UnitsForSale = GetInt(el, "unitsForSale"),
            UnitsSold = GetInt(el, "unitsSold"),
            ListingsCount = GetInt(el, "listingsCount"),
            WorldPrices = worldPrices,
        };
    }

    private static List<RecentlyUpdatedItem> ParseRecentlyUpdated(string json)
    {
        var results = new List<RecentlyUpdatedItem>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
                return results;

            foreach (var item in items.EnumerateArray())
            {
                var itemId = (uint)GetInt(item, "itemID");
                if (itemId == 0) continue;

                var uploadMs = item.TryGetProperty("lastUploadTime", out var ut) && ut.TryGetInt64(out var ms)
                    ? ms
                    : 0L;

                results.Add(new RecentlyUpdatedItem
                {
                    ItemId = itemId,
                    WorldName = GetString(item, "worldName"),
                    LastUploadTime = uploadMs > 0
                        ? DateTimeOffset.FromUnixTimeMilliseconds(uploadMs).UtcDateTime
                        : DateTime.MinValue,
                });
            }
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Warning(ex, "[Insights] Failed to parse recently-updated response");
        }

        return results;
    }

    private static void ParseHistoryResponse(string json, List<RecentSale> results)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Multi-item history has "items" object
            if (root.TryGetProperty("items", out var itemsObj) &&
                itemsObj.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in itemsObj.EnumerateObject())
                    ParseSingleItemHistory(prop.Value, results);
            }
            else
            {
                ParseSingleItemHistory(root, results);
            }
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Warning(ex, "[Insights] Failed to parse history response");
        }
    }

    private static void ParseSingleItemHistory(JsonElement el, List<RecentSale> results)
    {
        var itemId = (uint)GetInt(el, "itemID");
        if (itemId == 0) return;

        if (!el.TryGetProperty("entries", out var entries) ||
            entries.ValueKind != JsonValueKind.Array)
            return;

        foreach (var entry in entries.EnumerateArray())
        {
            var ts = entry.TryGetProperty("timestamp", out var tsEl) && tsEl.TryGetInt64(out var secs)
                ? secs
                : 0L;

            results.Add(new RecentSale
            {
                ItemId = itemId,
                PricePerUnit = GetInt(entry, "pricePerUnit"),
                Quantity = GetInt(entry, "quantity"),
                IsHq = entry.TryGetProperty("hq", out var hqEl) && hqEl.GetBoolean(),
                WorldName = GetString(entry, "worldName"),
                Timestamp = ts > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime
                    : DateTime.MinValue,
            });
        }
    }

    // ──────────────────────────────────────────────
    // JSON Helpers
    // ──────────────────────────────────────────────

    private static float GetFloat(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.TryGetSingle(out var f) ? f : 0;

    private static int GetInt(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.TryGetInt32(out var i) ? i : 0;

    private static string GetString(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    public void Dispose() => httpClient.Dispose();
}
