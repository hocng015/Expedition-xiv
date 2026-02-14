using System.Numerics;
using Dalamud.Bindings.ImGui;
using Expedition.Insights;

namespace Expedition.UI;

/// <summary>
/// Draws the Insights tab content. Follows the static class pattern
/// established by SettingsTab — called from MainWindow.
///
/// All data is read from InsightsEngine.CurrentSnapshot (immutable, lock-free).
/// No network calls or heavy allocations happen during Draw().
/// </summary>
public static class InsightsTab
{
    // UI state
    private static int selectedDcIndex;
    private static int selectedSortMode;     // 0=Velocity, 1=Gil Volume, 2=Price
    private static int selectedCategoryIndex;
    private static int expandedItemIndex = -1;
    private static int expandedEconomyIndex = -1;
    private static int expandedCraftIndex = -1;
    private static int expandedScripIndex = -1;
    private static int expandedTrendIndex = -1;

    private static readonly string[] DcNames = { "Aether", "Primal", "Crystal", "Dynamis" };
    private static readonly string[] SortModeNames = { "Sale Velocity", "Gil Volume", "Average Price" };

    // Icon sizes
    private static readonly Vector2 IconSm = new(24, 24);
    private static readonly Vector2 IconRow = new(32, 32);  // Table row icons — larger for readability
    private static readonly Vector2 IconMd = new(36, 36);

    public static void Draw(InsightsEngine engine)
    {
        DrawControlBar(engine);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var snapshot = engine.CurrentSnapshot;

        // Show loading / error state
        if (snapshot.IsLoading && snapshot.FetchedAt == DateTime.MinValue)
        {
            ImGui.Spacing();
            ImGui.TextColored(Theme.Accent, "Loading marketplace data...");
            ImGui.Spacing();
            ImGui.TextColored(Theme.TextMuted, "This may take a few seconds on first load.");
            return;
        }

        if (snapshot.ErrorMessage != null && snapshot.HottestItems.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(Theme.Error, snapshot.ErrorMessage);
            ImGui.Spacing();
            if (Theme.PrimaryButton("Retry", new Vector2(100, 30)))
                engine.TriggerRefresh();
            return;
        }

        DrawSubTabs(engine);
    }

    // ──────────────────────────────────────────────
    // Control Bar
    // ──────────────────────────────────────────────

    private static void DrawControlBar(InsightsEngine engine)
    {
        // DC selector combo
        ImGui.TextColored(Theme.TextSecondary, "Data Center:");
        ImGui.SameLine(0, Theme.PadSmall);
        ImGui.SetNextItemWidth(120);
        if (ImGui.Combo("##DcSelect", ref selectedDcIndex, DcNames, DcNames.Length))
        {
            engine.SelectedDataCenter = DcNames[selectedDcIndex];
            engine.TriggerRefresh();
        }

        ImGui.SameLine(0, Theme.PadLarge);

        // Last updated time
        var snapshot = engine.CurrentSnapshot;
        if (snapshot.FetchedAt > DateTime.MinValue)
        {
            var ago = DateTime.UtcNow - snapshot.FetchedAt;
            string agoText;
            if (ago.TotalSeconds < 60)
                agoText = "just now";
            else if (ago.TotalMinutes < 60)
                agoText = string.Concat(((int)ago.TotalMinutes).ToString(), "m ago");
            else
                agoText = string.Concat(((int)ago.TotalHours).ToString(), "h ago");

            ImGui.TextColored(Theme.TextMuted, "Updated:");
            ImGui.SameLine(0, Theme.PadSmall);
            ImGui.TextColored(Theme.TextSecondary, agoText);
        }

        ImGui.SameLine(0, Theme.PadLarge);

        // Refresh button
        if (engine.IsRefreshing) ImGui.BeginDisabled();
        if (Theme.SecondaryButton("Refresh", new Vector2(80, 0)))
            engine.TriggerRefresh();
        if (engine.IsRefreshing) ImGui.EndDisabled();

        // Loading indicator
        if (engine.IsRefreshing)
        {
            ImGui.SameLine(0, Theme.Pad);
            ImGui.TextColored(Theme.Accent, "Loading...");
        }

        // Error message (inline, not blocking)
        if (snapshot.ErrorMessage != null && snapshot.HottestItems.Count > 0)
        {
            ImGui.SameLine(0, Theme.Pad);
            ImGui.TextColored(Theme.Warning, snapshot.ErrorMessage);
        }
    }

    // ──────────────────────────────────────────────
    // Sub-tabs
    // ──────────────────────────────────────────────

    private static void DrawSubTabs(InsightsEngine engine)
    {
        if (ImGui.BeginTabBar("InsightsSubTabs"))
        {
            if (ImGui.BeginTabItem("Overview"))
            {
                DrawOverview(engine);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Hot Items"))
            {
                DrawHotItems(engine);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Categories"))
            {
                DrawCategories(engine);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Activity Feed"))
            {
                DrawActivityFeed(engine);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Economy"))
            {
                DrawEconomy(engine);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Craft Profit"))
            {
                DrawCraftProfit(engine);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Scrip Exchange"))
            {
                DrawScripExchange(engine);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Weekly Trends"))
            {
                DrawWeeklyTrends(engine);
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    // ──────────────────────────────────────────────
    // Overview Sub-tab
    // ──────────────────────────────────────────────

    private static void DrawOverview(InsightsEngine engine)
    {
        var snapshot = engine.CurrentSnapshot;

        if (snapshot.CategorySummaries.Count == 0)
        {
            ImGui.TextColored(Theme.TextMuted, "No category data available.");
            return;
        }

        ImGui.BeginChild("OverviewScroll", Vector2.Zero, false);

        // ── Header banner ──
        ImGui.Spacing();
        DrawOverviewHeader(snapshot);
        ImGui.Spacing();
        ImGui.Spacing();

        // ── Top movers section: top 5 items by velocity ──
        if (snapshot.HottestItems.Count > 0)
        {
            Theme.SectionHeader("Top Movers", Theme.Accent);
            ImGui.Spacing();
            DrawTopMoversRow(snapshot);
            ImGui.Spacing();
            ImGui.Spacing();
        }

        // ── Category breakdown cards ──
        Theme.SectionHeader("Category Breakdown", Theme.Gold);
        ImGui.Spacing();

        for (var i = 0; i < snapshot.CategorySummaries.Count; i++)
        {
            ImGui.PushID(i);
            DrawCategoryCard(snapshot.CategorySummaries[i]);
            ImGui.PopID();
            ImGui.Spacing();
        }

        ImGui.EndChild();
    }

    private static void DrawOverviewHeader(InsightsSnapshot snapshot)
    {
        var drawList = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail().X;
        var headerH = 52f;

        // Dark gradient-style banner background
        drawList.AddRectFilled(
            pos,
            new Vector2(pos.X + avail, pos.Y + headerH),
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.10f, 0.12f, 0.18f, 1.00f)),
            6f);

        // Gold accent line at top
        drawList.AddRectFilled(
            pos,
            new Vector2(pos.X + avail, pos.Y + 2),
            ImGui.ColorConvertFloat4ToU32(Theme.Gold),
            6f);

        // Title text
        var titleY = pos.Y + 8f;
        var title = string.Concat("Marketplace Report  —  ", snapshot.DataCenterName);
        drawList.AddText(new Vector2(pos.X + Theme.PadLarge, titleY),
            ImGui.ColorConvertFloat4ToU32(Theme.Gold), title);

        // Summary stats inline
        var totalVelocity = 0f;
        var totalGilVol = 0f;
        var totalItems = 0;
        for (var i = 0; i < snapshot.CategorySummaries.Count; i++)
        {
            totalVelocity += snapshot.CategorySummaries[i].TotalDailyVelocity;
            totalGilVol += snapshot.CategorySummaries[i].EstimatedDailyGilVolume;
            totalItems += snapshot.CategorySummaries[i].ItemCount;
        }

        var statsY = pos.Y + 28f;
        var statsText = string.Concat(
            totalItems.ToString(), " items tracked  |  ~",
            FormatNumber(totalVelocity), " units/day  |  ~",
            FormatGil(totalGilVol), " gil/day estimated volume");
        drawList.AddText(new Vector2(pos.X + Theme.PadLarge, statsY),
            ImGui.ColorConvertFloat4ToU32(Theme.TextSecondary), statsText);

        ImGui.Dummy(new Vector2(avail, headerH));
    }

    private static void DrawTopMoversRow(InsightsSnapshot snapshot)
    {
        var count = Math.Min(5, snapshot.HottestItems.Count);
        var avail = ImGui.GetContentRegionAvail().X;
        var itemWidth = (avail - (count - 1) * Theme.Pad) / count;
        if (itemWidth < 100) itemWidth = 100;

        for (var i = 0; i < count; i++)
        {
            var item = snapshot.HottestItems[i];

            if (i > 0) ImGui.SameLine(0, Theme.Pad);

            ImGui.PushID(1000 + i);
            ImGui.BeginGroup();

            var pos = ImGui.GetCursorScreenPos();
            var drawList = ImGui.GetWindowDrawList();
            var cardH = 80f;

            // Card background with subtle left accent
            drawList.AddRectFilled(
                pos,
                new Vector2(pos.X + itemWidth, pos.Y + cardH),
                ImGui.ColorConvertFloat4ToU32(Theme.CardBg),
                4f);
            drawList.AddRectFilled(
                pos,
                new Vector2(pos.X + 3, pos.Y + cardH),
                ImGui.ColorConvertFloat4ToU32(Theme.Accent),
                4f);

            // Rank badge top-right
            var rankText = string.Concat("#", (i + 1).ToString());
            var rankSize = ImGui.CalcTextSize(rankText);
            drawList.AddText(
                new Vector2(pos.X + itemWidth - rankSize.X - Theme.PadSmall, pos.Y + Theme.PadSmall),
                ImGui.ColorConvertFloat4ToU32(Theme.TextMuted), rankText);

            // Icon + name
            ImGui.SetCursorScreenPos(new Vector2(pos.X + Theme.Pad, pos.Y + Theme.PadSmall));
            MainWindow.DrawGameIcon(item.IconId, IconMd);
            ImGui.SetCursorScreenPos(new Vector2(pos.X + Theme.Pad + IconMd.X + Theme.PadSmall, pos.Y + Theme.PadSmall));
            var nameText = string.IsNullOrEmpty(item.ItemName) ? string.Concat("Item #", item.ItemId.ToString()) : item.ItemName;
            // Truncate long names
            if (nameText.Length > 14) nameText = string.Concat(nameText.AsSpan(0, 12), "..");
            ImGui.TextColored(Theme.TextPrimary, nameText);

            // Velocity stat below name
            ImGui.SetCursorScreenPos(new Vector2(pos.X + Theme.Pad + IconMd.X + Theme.PadSmall, pos.Y + Theme.PadSmall + ImGui.GetTextLineHeightWithSpacing()));
            ImGui.TextColored(Theme.Accent, string.Concat(FormatNumber(item.RegularSaleVelocity), "/day"));

            // Gil volume at bottom of card
            ImGui.SetCursorScreenPos(new Vector2(pos.X + Theme.Pad, pos.Y + cardH - ImGui.GetTextLineHeight() - Theme.PadSmall));
            ImGui.TextColored(Theme.Success, string.Concat(FormatGil(item.EstimatedDailyGilVolume), " gil/day"));

            ImGui.SetCursorScreenPos(new Vector2(pos.X, pos.Y + cardH));
            ImGui.EndGroup();
            ImGui.PopID();
        }
        ImGui.Spacing();
    }

    private static void DrawCategoryCard(CategorySummary cat)
    {
        var avail = ImGui.GetContentRegionAvail().X;
        var drawList = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var cardH = 76f;

        // Card background
        drawList.AddRectFilled(
            pos,
            new Vector2(pos.X + avail, pos.Y + cardH),
            ImGui.ColorConvertFloat4ToU32(Theme.CardBg),
            4f);

        // Left accent bar in gold
        drawList.AddRectFilled(
            pos,
            new Vector2(pos.X + 3, pos.Y + cardH),
            ImGui.ColorConvertFloat4ToU32(Theme.Gold),
            4f);

        // ── Left section: category info ──
        var leftX = pos.X + Theme.PadLarge;
        var topY = pos.Y + Theme.Pad;

        // Category name (large text)
        drawList.AddText(new Vector2(leftX, topY),
            ImGui.ColorConvertFloat4ToU32(Theme.Gold), cat.CategoryName);

        // Item count below
        var subY = topY + ImGui.GetTextLineHeightWithSpacing();
        drawList.AddText(new Vector2(leftX, subY),
            ImGui.ColorConvertFloat4ToU32(Theme.TextMuted),
            string.Concat(cat.ItemCount.ToString(), " items tracked"));

        // ── Middle section: key stats ──
        var midX = pos.X + avail * 0.30f;
        drawList.AddText(new Vector2(midX, topY),
            ImGui.ColorConvertFloat4ToU32(Theme.TextSecondary), "Daily Volume");
        drawList.AddText(new Vector2(midX, subY),
            ImGui.ColorConvertFloat4ToU32(Theme.Accent),
            string.Concat("~", FormatNumber(cat.TotalDailyVelocity), " units"));

        var gilX = pos.X + avail * 0.52f;
        drawList.AddText(new Vector2(gilX, topY),
            ImGui.ColorConvertFloat4ToU32(Theme.TextSecondary), "Gil Volume");
        drawList.AddText(new Vector2(gilX, subY),
            ImGui.ColorConvertFloat4ToU32(Theme.Success),
            string.Concat("~", FormatGil(cat.EstimatedDailyGilVolume)));

        // ── Right section: top item with icon ──
        if (cat.TopItem != null)
        {
            var rightLabelX = pos.X + avail * 0.72f;
            drawList.AddText(new Vector2(rightLabelX, topY),
                ImGui.ColorConvertFloat4ToU32(Theme.TextSecondary), "Top Seller");

            // Item icon
            var iconX = rightLabelX;
            var iconY = subY - 2f;
            ImGui.SetCursorScreenPos(new Vector2(iconX, iconY));
            MainWindow.DrawGameIcon(cat.TopItem.IconId, IconSm);

            // Item name next to icon
            var nameX = iconX + IconSm.X + Theme.PadSmall;
            var topName = cat.TopItem.ItemName;
            if (topName.Length > 18) topName = string.Concat(topName.AsSpan(0, 16), "..");
            drawList.AddText(new Vector2(nameX, iconY + (IconSm.Y - ImGui.GetTextLineHeight()) / 2),
                ImGui.ColorConvertFloat4ToU32(Theme.TextPrimary), topName);
        }

        // Advance cursor past the card
        ImGui.SetCursorScreenPos(new Vector2(pos.X, pos.Y + cardH));
    }

    // ──────────────────────────────────────────────
    // Hot Items Sub-tab
    // ──────────────────────────────────────────────

    private static void DrawHotItems(InsightsEngine engine)
    {
        var snapshot = engine.CurrentSnapshot;
        ImGui.Spacing();

        // Sort mode selector
        ImGui.TextColored(Theme.TextSecondary, "Sort by:");
        ImGui.SameLine(0, Theme.PadSmall);
        ImGui.SetNextItemWidth(150);
        ImGui.Combo("##SortMode", ref selectedSortMode, SortModeNames, SortModeNames.Length);

        ImGui.Spacing();

        // Pick the right ranked list based on sort mode
        var items = selectedSortMode switch
        {
            1 => snapshot.HighestGilVolume,
            2 => snapshot.MostExpensive,
            _ => snapshot.HottestItems,
        };

        if (items.Count == 0)
        {
            ImGui.TextColored(Theme.TextMuted, "No market data available.");
            return;
        }

        DrawItemTable(items, showCategory: true);
    }

    // ──────────────────────────────────────────────
    // Categories Sub-tab
    // ──────────────────────────────────────────────

    private static void DrawCategories(InsightsEngine engine)
    {
        var snapshot = engine.CurrentSnapshot;
        ImGui.Spacing();

        // Category selector
        var categoryNames = new string[snapshot.CategorySummaries.Count];
        for (var i = 0; i < snapshot.CategorySummaries.Count; i++)
            categoryNames[i] = snapshot.CategorySummaries[i].CategoryName;

        if (categoryNames.Length == 0)
        {
            ImGui.TextColored(Theme.TextMuted, "No category data available.");
            return;
        }

        if (selectedCategoryIndex >= categoryNames.Length)
            selectedCategoryIndex = 0;

        ImGui.TextColored(Theme.TextSecondary, "Category:");
        ImGui.SameLine(0, Theme.PadSmall);
        ImGui.SetNextItemWidth(200);
        ImGui.Combo("##CatSelect", ref selectedCategoryIndex, categoryNames, categoryNames.Length);

        ImGui.Spacing();

        var catName = categoryNames[selectedCategoryIndex];
        if (snapshot.ItemsByCategory.TryGetValue(catName, out var catItems) && catItems.Count > 0)
        {
            // Show category summary
            var catSummary = snapshot.CategorySummaries[selectedCategoryIndex];
            ImGui.TextColored(Theme.TextMuted, string.Concat(
                catSummary.ItemCount.ToString(), " items  |  ~",
                FormatNumber(catSummary.TotalDailyVelocity), " units/day  |  ~",
                FormatGil(catSummary.EstimatedDailyGilVolume), " gil/day"));
            ImGui.Spacing();

            // Sort category items by velocity
            var sorted = new List<MarketItemData>(catItems);
            sorted.Sort((a, b) => b.RegularSaleVelocity.CompareTo(a.RegularSaleVelocity));

            DrawItemTable(sorted, showCategory: false);
        }
        else
        {
            ImGui.TextColored(Theme.TextMuted, "No items in this category.");
        }
    }

    // ──────────────────────────────────────────────
    // Activity Feed Sub-tab
    // ──────────────────────────────────────────────

    private static void DrawActivityFeed(InsightsEngine engine)
    {
        var snapshot = engine.CurrentSnapshot;
        ImGui.Spacing();
        Theme.SectionHeader(string.Concat("Recent Market Activity — ", snapshot.DataCenterName), Theme.Accent);
        ImGui.Spacing();

        if (snapshot.RecentActivity.Count == 0)
        {
            ImGui.TextColored(Theme.TextMuted, "No recent activity data.");
            return;
        }

        ImGui.BeginChild("ActivityScroll", Vector2.Zero, false);

        for (var i = 0; i < snapshot.RecentActivity.Count; i++)
        {
            var item = snapshot.RecentActivity[i];

            MainWindow.DrawGameIcon(item.IconId, IconSm);
            ImGui.SameLine(0, Theme.PadSmall);

            // Vertically center text with icon
            var cursorY = ImGui.GetCursorPosY();
            ImGui.SetCursorPosY(cursorY + (IconSm.Y - ImGui.GetTextLineHeight()) / 2);

            // Item name
            var nameText = string.IsNullOrEmpty(item.ItemName)
                ? string.Concat("Item #", item.ItemId.ToString())
                : item.ItemName;
            ImGui.TextColored(Theme.Gold, nameText);

            ImGui.SameLine(0, Theme.PadLarge);

            // Time ago
            if (item.LastUploadTime > DateTime.MinValue)
            {
                var ago = DateTime.UtcNow - item.LastUploadTime;
                string timeText;
                if (ago.TotalSeconds < 60)
                    timeText = string.Concat(((int)ago.TotalSeconds).ToString(), "s ago");
                else if (ago.TotalMinutes < 60)
                    timeText = string.Concat(((int)ago.TotalMinutes).ToString(), "m ago");
                else
                    timeText = string.Concat(((int)ago.TotalHours).ToString(), "h ago");

                ImGui.TextColored(Theme.TextMuted, timeText);
            }

            ImGui.SameLine(0, Theme.Pad);

            // World name
            if (!string.IsNullOrEmpty(item.WorldName))
                ImGui.TextColored(Theme.TextSecondary, item.WorldName);

            // Reset cursor Y for next row
            ImGui.SetCursorPosY(cursorY + IconSm.Y + Theme.PadSmall);
        }

        ImGui.EndChild();
    }

    // ──────────────────────────────────────────────
    // Shared Item Table
    // ──────────────────────────────────────────────

    private static void DrawItemTable(List<MarketItemData> items, bool showCategory)
    {
        ImGui.BeginChild("ItemTableScroll", Vector2.Zero, false);

        // Column header
        ImGui.TextColored(Theme.TextMuted, "#");
        ImGui.SameLine(40);
        ImGui.TextColored(Theme.TextMuted, "Item");
        if (showCategory)
        {
            ImGui.SameLine(540);
            ImGui.TextColored(Theme.TextMuted, "Category");
        }
        ImGui.SameLine(showCategory ? 680 : 540);
        ImGui.TextColored(Theme.TextMuted, "Velocity");
        ImGui.SameLine(showCategory ? 800 : 660);
        ImGui.TextColored(Theme.TextMuted, "Avg Price");
        ImGui.SameLine(showCategory ? 920 : 780);
        ImGui.TextColored(Theme.TextMuted, "Daily Gil Vol");
        ImGui.Separator();

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var isExpanded = expandedItemIndex == i;

            ImGui.PushID(i);

            // Rank number
            ImGui.TextColored(Theme.TextMuted, (i + 1).ToString());
            ImGui.SameLine(40);

            // Icon + Name
            MainWindow.DrawGameIcon(item.IconId, IconRow);
            ImGui.SameLine(0, Theme.PadSmall);

            var cursorY = ImGui.GetCursorPosY();
            ImGui.SetCursorPosY(cursorY + (IconRow.Y - ImGui.GetTextLineHeight()) / 2);

            var nameText = string.IsNullOrEmpty(item.ItemName)
                ? string.Concat("Item #", item.ItemId.ToString())
                : item.ItemName;

            // Clickable name for expansion
            if (ImGui.Selectable(string.Concat(nameText, "##item", i.ToString()),
                isExpanded, ImGuiSelectableFlags.None, new Vector2(showCategory ? 450 : 460, 0)))
            {
                expandedItemIndex = isExpanded ? -1 : i;
            }

            // Category badge
            if (showCategory && !string.IsNullOrEmpty(item.CategoryName))
            {
                ImGui.SameLine(540);
                ImGui.SetCursorPosY(cursorY + (IconRow.Y - ImGui.GetTextLineHeight()) / 2);
                ImGui.TextColored(Theme.TextSecondary, item.CategoryName);
            }

            // Velocity
            var velX = showCategory ? 680f : 540f;
            ImGui.SameLine(velX);
            ImGui.SetCursorPosY(cursorY + (IconRow.Y - ImGui.GetTextLineHeight()) / 2);
            DrawBoxedValue(string.Concat(FormatNumber(item.RegularSaleVelocity), "/day"), Theme.Accent);

            // Average price
            var priceX = showCategory ? 800f : 660f;
            ImGui.SameLine(priceX);
            ImGui.SetCursorPosY(cursorY + (IconRow.Y - ImGui.GetTextLineHeight()) / 2);
            DrawBoxedValue(FormatGil(item.CurrentAveragePrice), Theme.Gold);

            // Daily gil volume
            var gilX = showCategory ? 920f : 780f;
            ImGui.SameLine(gilX);
            ImGui.SetCursorPosY(cursorY + (IconRow.Y - ImGui.GetTextLineHeight()) / 2);
            DrawBoxedValue(FormatGil(item.EstimatedDailyGilVolume), Theme.Success);

            // Reset cursor
            ImGui.SetCursorPosY(cursorY + IconRow.Y + 4);

            // Expanded detail: item stats
            if (isExpanded)
            {
                ImGui.Indent(40);
                ImGui.Spacing();

                ImGui.TextColored(Theme.TextMuted, "Details:");
                ImGui.Spacing();

                Theme.KeyValue("Min Price: ", FormatGil(item.MinPrice), Theme.Success);
                Theme.KeyValue("Max Price: ", FormatGil(item.MaxPrice), Theme.Error);
                Theme.KeyValue("Listings: ", item.ListingsCount.ToString(), Theme.TextPrimary);
                Theme.KeyValue("Units For Sale: ", FormatNumber(item.UnitsForSale), Theme.TextPrimary);
                Theme.KeyValue("Units Sold (recent): ", FormatNumber(item.UnitsSold), Theme.Accent);
                if (item.HqSaleVelocity > 0)
                    Theme.KeyValue("HQ Velocity: ", string.Concat(FormatNumber(item.HqSaleVelocity), "/day"), Theme.Collectable);
                if (item.NqSaleVelocity > 0)
                    Theme.KeyValue("NQ Velocity: ", string.Concat(FormatNumber(item.NqSaleVelocity), "/day"), Theme.TextSecondary);

                ImGui.Spacing();
                ImGui.Unindent(40);
                ImGui.Separator();
            }

            ImGui.PopID();
        }

        ImGui.EndChild();
    }

    // ──────────────────────────────────────────────
    // Economy Sub-tab (Saddlebag Market Share)
    // ──────────────────────────────────────────────

    private static void DrawEconomy(InsightsEngine engine)
    {
        var snapshot = engine.CurrentSnapshot;
        ImGui.Spacing();

        if (snapshot.Saddlebag == null || snapshot.Saddlebag.MarketShare.Count == 0)
        {
            ImGui.TextColored(Theme.TextMuted, "No market share data available.");
            ImGui.TextColored(Theme.TextMuted, "Data from Saddlebag Exchange will appear after the next refresh cycle.");
            return;
        }

        Theme.SectionHeader("Market Share — Top Items by Market Value", Theme.Gold);
        ImGui.Spacing();

        ImGui.BeginChild("EconomyScroll", Vector2.Zero, false);

        // Column headers
        ImGui.TextColored(Theme.TextMuted, "#");
        ImGui.SameLine(40);
        ImGui.TextColored(Theme.TextMuted, "Item");
        ImGui.SameLine(540);
        ImGui.TextColored(Theme.TextMuted, "State");
        ImGui.SameLine(660);
        ImGui.TextColored(Theme.TextMuted, "Avg Price");
        ImGui.SameLine(780);
        ImGui.TextColored(Theme.TextMuted, "Qty Sold");
        ImGui.SameLine(890);
        ImGui.TextColored(Theme.TextMuted, "Market Value");
        ImGui.SameLine(1020);
        ImGui.TextColored(Theme.TextMuted, "% Change");
        ImGui.Separator();

        var items = snapshot.Saddlebag.MarketShare;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var isExpanded = expandedEconomyIndex == i;

            ImGui.PushID(2000 + i);

            // Rank
            ImGui.TextColored(Theme.TextMuted, (i + 1).ToString());
            ImGui.SameLine(40);

            // Icon + Name
            MainWindow.DrawGameIcon(item.IconId, IconRow);
            ImGui.SameLine(0, Theme.PadSmall);

            var cursorY = ImGui.GetCursorPosY();
            ImGui.SetCursorPosY(cursorY + (IconRow.Y - ImGui.GetTextLineHeight()) / 2);

            var nameText = string.IsNullOrEmpty(item.ItemName)
                ? string.Concat("Item #", item.ItemId.ToString())
                : item.ItemName;

            if (ImGui.Selectable(string.Concat(nameText, "##eco", i.ToString()),
                isExpanded, ImGuiSelectableFlags.None, new Vector2(450, 0)))
            {
                expandedEconomyIndex = isExpanded ? -1 : i;
            }

            // State badge with color
            ImGui.SameLine(540);
            ImGui.SetCursorPosY(cursorY + (IconRow.Y - ImGui.GetTextLineHeight()) / 2);
            var stateColor = GetStateColor(item.State);
            DrawBoxedValue(item.State, stateColor);

            // Average price
            ImGui.SameLine(660);
            ImGui.SetCursorPosY(cursorY + (IconRow.Y - ImGui.GetTextLineHeight()) / 2);
            DrawBoxedValue(FormatGil(item.AveragePrice), Theme.Gold);

            // Quantity sold
            ImGui.SameLine(780);
            ImGui.SetCursorPosY(cursorY + (IconRow.Y - ImGui.GetTextLineHeight()) / 2);
            DrawBoxedValue(FormatNumber(item.QuantitySold), Theme.TextPrimary);

            // Market value
            ImGui.SameLine(890);
            ImGui.SetCursorPosY(cursorY + (IconRow.Y - ImGui.GetTextLineHeight()) / 2);
            DrawBoxedValue(FormatGil(item.MarketValue), Theme.Success);

            // Percent change
            ImGui.SameLine(1020);
            ImGui.SetCursorPosY(cursorY + (IconRow.Y - ImGui.GetTextLineHeight()) / 2);
            var pctColor = item.PercentChange >= 0 ? Theme.Success : Theme.Error;
            var pctText = string.Concat(item.PercentChange >= 0 ? "+" : "", item.PercentChange.ToString("F1"), "%");
            DrawBoxedValue(pctText, pctColor);

            ImGui.SetCursorPosY(cursorY + IconRow.Y + 4);

            // Expanded detail
            if (isExpanded)
            {
                ImGui.Indent(40);
                ImGui.Spacing();
                Theme.KeyValue("Median Price: ", FormatGil(item.MedianPrice), Theme.TextPrimary);
                Theme.KeyValue("Min Price: ", FormatGil(item.MinPrice), Theme.Success);
                Theme.KeyValue("Home Min Price: ", FormatGil(item.HomeMinPrice), Theme.Accent);
                Theme.KeyValue("Home Median: ", FormatGil(item.HomeMedianPrice), Theme.Accent);
                ImGui.Spacing();
                ImGui.Unindent(40);
                ImGui.Separator();
            }

            ImGui.PopID();
        }

        ImGui.EndChild();
    }

    private static Vector4 GetStateColor(string state)
    {
        return state.ToLowerInvariant() switch
        {
            "crashing" => Theme.Error,
            "decreasing" => new Vector4(1.0f, 0.5f, 0.3f, 1.0f),  // orange
            "stable" => Theme.TextSecondary,
            "increasing" => Theme.Success,
            "spiking" => new Vector4(0.0f, 1.0f, 0.5f, 1.0f),     // bright green
            "out of stock" => Theme.TextMuted,
            _ => Theme.TextPrimary,
        };
    }

    // ──────────────────────────────────────────────
    // Craft Profit Sub-tab
    // ──────────────────────────────────────────────

    private static void DrawCraftProfit(InsightsEngine engine)
    {
        var snapshot = engine.CurrentSnapshot;
        ImGui.Spacing();

        if (snapshot.Saddlebag == null || snapshot.Saddlebag.CraftProfit.Count == 0)
        {
            ImGui.TextColored(Theme.TextMuted, "No craft profit data available.");
            ImGui.TextColored(Theme.TextMuted, "Data from Saddlebag Exchange will appear after the next refresh cycle.");
            return;
        }

        Theme.SectionHeader("Craft Profit Analysis — Most Profitable Crafts", Theme.Accent);
        ImGui.Spacing();

        ImGui.BeginChild("CraftProfitScroll", Vector2.Zero, false);

        // Column headers
        ImGui.TextColored(Theme.TextMuted, "#");
        ImGui.SameLine(40);
        ImGui.TextColored(Theme.TextMuted, "Item");
        ImGui.SameLine(540);
        ImGui.TextColored(Theme.TextMuted, "Job");
        ImGui.SameLine(600);
        ImGui.TextColored(Theme.TextMuted, "Revenue");
        ImGui.SameLine(720);
        ImGui.TextColored(Theme.TextMuted, "Cost");
        ImGui.SameLine(830);
        ImGui.TextColored(Theme.TextMuted, "Profit");
        ImGui.SameLine(950);
        ImGui.TextColored(Theme.TextMuted, "Margin");
        ImGui.Separator();

        var items = snapshot.Saddlebag.CraftProfit;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var isExpanded = expandedCraftIndex == i;

            ImGui.PushID(3000 + i);

            // Rank
            ImGui.TextColored(Theme.TextMuted, (i + 1).ToString());
            ImGui.SameLine(40);

            // Icon + Name
            MainWindow.DrawGameIcon(item.IconId, IconRow);
            ImGui.SameLine(0, Theme.PadSmall);

            var cursorY = ImGui.GetCursorPosY();
            ImGui.SetCursorPosY(cursorY + (IconRow.Y - ImGui.GetTextLineHeight()) / 2);

            var nameText = string.IsNullOrEmpty(item.ItemName)
                ? string.Concat("Item #", item.ItemId.ToString())
                : item.ItemName;

            if (ImGui.Selectable(string.Concat(nameText, "##craft", i.ToString()),
                isExpanded, ImGuiSelectableFlags.None, new Vector2(450, 0)))
            {
                expandedCraftIndex = isExpanded ? -1 : i;
            }

            // Job
            ImGui.SameLine(540);
            ImGui.SetCursorPosY(cursorY + (IconRow.Y - ImGui.GetTextLineHeight()) / 2);
            ImGui.TextColored(Theme.TextSecondary, item.Job);

            // Revenue
            ImGui.SameLine(600);
            ImGui.SetCursorPosY(cursorY + (IconRow.Y - ImGui.GetTextLineHeight()) / 2);
            DrawBoxedValue(FormatGil(item.Revenue), Theme.Gold);

            // Cost
            ImGui.SameLine(720);
            ImGui.SetCursorPosY(cursorY + (IconRow.Y - ImGui.GetTextLineHeight()) / 2);
            DrawBoxedValue(FormatGil(item.CraftingCost), Theme.Error);

            // Profit
            ImGui.SameLine(830);
            ImGui.SetCursorPosY(cursorY + (IconRow.Y - ImGui.GetTextLineHeight()) / 2);
            var profitColor = item.Profit >= 0 ? Theme.Success : Theme.Error;
            DrawBoxedValue(FormatGil(item.Profit), profitColor);

            // Margin %
            ImGui.SameLine(950);
            ImGui.SetCursorPosY(cursorY + (IconRow.Y - ImGui.GetTextLineHeight()) / 2);
            var marginColor = item.ProfitPercent >= 50 ? Theme.Success
                : item.ProfitPercent >= 20 ? Theme.Accent
                : Theme.Warning;
            DrawBoxedValue(string.Concat(item.ProfitPercent.ToString("F0"), "%"), marginColor);

            ImGui.SetCursorPosY(cursorY + IconRow.Y + 4);

            // Expanded detail
            if (isExpanded)
            {
                ImGui.Indent(40);
                ImGui.Spacing();
                Theme.KeyValue("Median Price: ", FormatGil(item.MedianPrice), Theme.TextPrimary);
                Theme.KeyValue("Min Price: ", FormatGil(item.MinPrice), Theme.Success);
                Theme.KeyValue("Home Min Price: ", FormatGil(item.HomeMinPrice), Theme.Accent);
                Theme.KeyValue("Avg Sold/Day: ", FormatNumber(item.AverageSold), Theme.TextPrimary);
                Theme.KeyValue("Total Sales (7d): ", item.SalesAmount.ToString(), Theme.TextPrimary);
                ImGui.Spacing();
                ImGui.Unindent(40);
                ImGui.Separator();
            }

            ImGui.PopID();
        }

        ImGui.EndChild();
    }

    // ──────────────────────────────────────────────
    // Scrip Exchange Sub-tab
    // ──────────────────────────────────────────────

    private static void DrawScripExchange(InsightsEngine engine)
    {
        var snapshot = engine.CurrentSnapshot;
        ImGui.Spacing();

        if (snapshot.Saddlebag == null || snapshot.Saddlebag.ScripExchange.Count == 0)
        {
            ImGui.TextColored(Theme.TextMuted, "No scrip exchange data available.");
            ImGui.TextColored(Theme.TextMuted, "Data from Saddlebag Exchange will appear after the next refresh cycle.");
            return;
        }

        Theme.SectionHeader("Scrip Exchange — Best Gil per Scrip", Theme.Collectable);
        ImGui.Spacing();

        ImGui.BeginChild("ScripScroll", Vector2.Zero, false);

        // Column headers
        ImGui.TextColored(Theme.TextMuted, "#");
        ImGui.SameLine(40);
        ImGui.TextColored(Theme.TextMuted, "Item");
        ImGui.SameLine(540);
        ImGui.TextColored(Theme.TextMuted, "Scrip Cost");
        ImGui.SameLine(660);
        ImGui.TextColored(Theme.TextMuted, "Market Price");
        ImGui.SameLine(800);
        ImGui.TextColored(Theme.TextMuted, "Gil/Scrip");
        ImGui.SameLine(920);
        ImGui.TextColored(Theme.TextMuted, "Qty Sold");
        ImGui.Separator();

        var items = snapshot.Saddlebag.ScripExchange;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var isExpanded = expandedScripIndex == i;

            ImGui.PushID(4000 + i);

            // Rank
            ImGui.TextColored(Theme.TextMuted, (i + 1).ToString());
            ImGui.SameLine(40);

            // Icon + Name
            MainWindow.DrawGameIcon(item.IconId, IconRow);
            ImGui.SameLine(0, Theme.PadSmall);

            var cursorY = ImGui.GetCursorPosY();
            ImGui.SetCursorPosY(cursorY + (IconRow.Y - ImGui.GetTextLineHeight()) / 2);

            var nameText = string.IsNullOrEmpty(item.ItemName)
                ? string.Concat("Item #", item.ItemId.ToString())
                : item.ItemName;

            if (ImGui.Selectable(string.Concat(nameText, "##scrip", i.ToString()),
                isExpanded, ImGuiSelectableFlags.None, new Vector2(450, 0)))
            {
                expandedScripIndex = isExpanded ? -1 : i;
            }

            // Scrip cost
            ImGui.SameLine(540);
            ImGui.SetCursorPosY(cursorY + (IconRow.Y - ImGui.GetTextLineHeight()) / 2);
            DrawBoxedValue(item.ScripCost.ToString(), Theme.Collectable);

            // Market price
            ImGui.SameLine(660);
            ImGui.SetCursorPosY(cursorY + (IconRow.Y - ImGui.GetTextLineHeight()) / 2);
            DrawBoxedValue(FormatGil(item.MarketPrice), Theme.Gold);

            // Gil per scrip
            ImGui.SameLine(800);
            ImGui.SetCursorPosY(cursorY + (IconRow.Y - ImGui.GetTextLineHeight()) / 2);
            var gilPerColor = item.GilPerScrip >= 200 ? Theme.Success
                : item.GilPerScrip >= 100 ? Theme.Accent
                : Theme.Warning;
            DrawBoxedValue(FormatGil(item.GilPerScrip), gilPerColor);

            // Quantity sold
            ImGui.SameLine(920);
            ImGui.SetCursorPosY(cursorY + (IconRow.Y - ImGui.GetTextLineHeight()) / 2);
            DrawBoxedValue(FormatNumber(item.QuantitySold), Theme.TextPrimary);

            ImGui.SetCursorPosY(cursorY + IconRow.Y + 4);

            // Expanded detail
            if (isExpanded)
            {
                ImGui.Indent(40);
                ImGui.Spacing();
                Theme.KeyValue("Min Price: ", FormatGil(item.MinPrice), Theme.Success);
                Theme.KeyValue("Home Min Price: ", FormatGil(item.HomeMinPrice), Theme.Accent);
                ImGui.Spacing();
                ImGui.Unindent(40);
                ImGui.Separator();
            }

            ImGui.PopID();
        }

        ImGui.EndChild();
    }

    // ──────────────────────────────────────────────
    // Weekly Trends Sub-tab
    // ──────────────────────────────────────────────

    private static void DrawWeeklyTrends(InsightsEngine engine)
    {
        var snapshot = engine.CurrentSnapshot;
        ImGui.Spacing();

        if (snapshot.Saddlebag == null || snapshot.Saddlebag.WeeklyTrends.Count == 0)
        {
            ImGui.TextColored(Theme.TextMuted, "No weekly trend data available.");
            ImGui.TextColored(Theme.TextMuted, "Data from Saddlebag Exchange will appear after the next refresh cycle.");
            return;
        }

        Theme.SectionHeader("Weekly Price Trends — 7 Day Price Movement", Theme.Warning);
        ImGui.Spacing();

        ImGui.BeginChild("TrendsScroll", Vector2.Zero, false);

        // Column headers
        ImGui.TextColored(Theme.TextMuted, "#");
        ImGui.SameLine(40);
        ImGui.TextColored(Theme.TextMuted, "Item");
        ImGui.SameLine(540);
        ImGui.TextColored(Theme.TextMuted, "Current Avg");
        ImGui.SameLine(680);
        ImGui.TextColored(Theme.TextMuted, "Previous Avg");
        ImGui.SameLine(820);
        ImGui.TextColored(Theme.TextMuted, "Delta");
        ImGui.SameLine(960);
        ImGui.TextColored(Theme.TextMuted, "% Change");
        ImGui.Separator();

        var items = snapshot.Saddlebag.WeeklyTrends;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var isExpanded = expandedTrendIndex == i;

            ImGui.PushID(5000 + i);

            // Rank
            ImGui.TextColored(Theme.TextMuted, (i + 1).ToString());
            ImGui.SameLine(40);

            // Icon + Name
            MainWindow.DrawGameIcon(item.IconId, IconRow);
            ImGui.SameLine(0, Theme.PadSmall);

            var cursorY = ImGui.GetCursorPosY();
            ImGui.SetCursorPosY(cursorY + (IconRow.Y - ImGui.GetTextLineHeight()) / 2);

            var nameText = string.IsNullOrEmpty(item.ItemName)
                ? string.Concat("Item #", item.ItemId.ToString())
                : item.ItemName;

            if (ImGui.Selectable(string.Concat(nameText, "##trend", i.ToString()),
                isExpanded, ImGuiSelectableFlags.None, new Vector2(450, 0)))
            {
                expandedTrendIndex = isExpanded ? -1 : i;
            }

            // Current average
            ImGui.SameLine(540);
            ImGui.SetCursorPosY(cursorY + (IconRow.Y - ImGui.GetTextLineHeight()) / 2);
            DrawBoxedValue(FormatGil(item.CurrentAverage), Theme.Gold);

            // Previous average
            ImGui.SameLine(680);
            ImGui.SetCursorPosY(cursorY + (IconRow.Y - ImGui.GetTextLineHeight()) / 2);
            DrawBoxedValue(FormatGil(item.PreviousAverage), Theme.TextSecondary);

            // Delta
            ImGui.SameLine(820);
            ImGui.SetCursorPosY(cursorY + (IconRow.Y - ImGui.GetTextLineHeight()) / 2);
            var deltaColor = item.PriceDelta >= 0 ? Theme.Success : Theme.Error;
            var deltaPrefix = item.PriceDelta >= 0 ? "+" : "";
            DrawBoxedValue(string.Concat(deltaPrefix, FormatGil(item.PriceDelta)), deltaColor);

            // Percent change
            ImGui.SameLine(960);
            ImGui.SetCursorPosY(cursorY + (IconRow.Y - ImGui.GetTextLineHeight()) / 2);
            var pctColor = item.PercentChange >= 0 ? Theme.Success : Theme.Error;
            var pctPrefix = item.PercentChange >= 0 ? "+" : "";
            DrawBoxedValue(string.Concat(pctPrefix, item.PercentChange.ToString("F1"), "%"), pctColor);

            ImGui.SetCursorPosY(cursorY + IconRow.Y + 4);

            // Expanded detail
            if (isExpanded)
            {
                ImGui.Indent(40);
                ImGui.Spacing();
                Theme.KeyValue("Sales Amount (7d): ", item.SalesAmount.ToString(), Theme.TextPrimary);
                ImGui.Spacing();
                ImGui.Unindent(40);
                ImGui.Separator();
            }

            ImGui.PopID();
        }

        ImGui.EndChild();
    }

    // ──────────────────────────────────────────────
    // Boxed Value Renderer
    // ──────────────────────────────────────────────

    /// <summary>
    /// Draws text inside a colored rounded-rect box (border + semi-transparent fill).
    /// Used for numeric cell values in tables to match the FFXIV Market Board style.
    /// </summary>
    private static void DrawBoxedValue(string text, Vector4 color)
    {
        var drawList = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var textSize = ImGui.CalcTextSize(text);
        var padding = new Vector2(4, 1);
        var boxMin = pos;
        var boxMax = new Vector2(pos.X + textSize.X + padding.X * 2, pos.Y + textSize.Y + padding.Y * 2);
        var bgColor = new Vector4(color.X, color.Y, color.Z, 0.12f);
        var borderColor = new Vector4(color.X, color.Y, color.Z, 0.55f);

        drawList.AddRectFilled(boxMin, boxMax, ImGui.ColorConvertFloat4ToU32(bgColor), 3f);
        drawList.AddRect(boxMin, boxMax, ImGui.ColorConvertFloat4ToU32(borderColor), 3f);
        drawList.AddText(new Vector2(pos.X + padding.X, pos.Y + padding.Y),
            ImGui.ColorConvertFloat4ToU32(color), text);
        ImGui.Dummy(new Vector2(textSize.X + padding.X * 2, textSize.Y + padding.Y * 2));
    }

    // ──────────────────────────────────────────────
    // Formatting Helpers
    // ──────────────────────────────────────────────

    /// <summary>
    /// Formats a number with K/M suffixes for readability.
    /// </summary>
    private static string FormatNumber(float value)
    {
        if (value >= 1_000_000)
            return string.Concat((value / 1_000_000f).ToString("F1"), "M");
        if (value >= 1_000)
            return string.Concat((value / 1_000f).ToString("F1"), "K");
        return value.ToString("F0");
    }

    /// <summary>
    /// Formats a gil amount with commas for readability.
    /// </summary>
    private static string FormatGil(float value)
    {
        if (value >= 1_000_000_000)
            return string.Concat((value / 1_000_000_000f).ToString("F1"), "B");
        if (value >= 1_000_000)
            return string.Concat((value / 1_000_000f).ToString("F1"), "M");
        if (value >= 1_000)
            return string.Concat((value / 1_000f).ToString("F1"), "K");
        return ((int)value).ToString("N0");
    }
}
