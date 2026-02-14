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

    private static readonly string[] DcNames = { "Aether", "Primal", "Crystal", "Dynamis" };
    private static readonly string[] SortModeNames = { "Sale Velocity", "Gil Volume", "Average Price" };

    // Icon sizes
    private static readonly Vector2 IconSm = new(24, 24);
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
        ImGui.SameLine(30);
        ImGui.TextColored(Theme.TextMuted, "Item");
        if (showCategory)
        {
            ImGui.SameLine(280);
            ImGui.TextColored(Theme.TextMuted, "Category");
        }
        ImGui.SameLine(showCategory ? 400 : 280);
        ImGui.TextColored(Theme.TextMuted, "Velocity");
        ImGui.SameLine(showCategory ? 490 : 370);
        ImGui.TextColored(Theme.TextMuted, "Avg Price");
        ImGui.SameLine(showCategory ? 590 : 470);
        ImGui.TextColored(Theme.TextMuted, "Daily Gil Vol");
        ImGui.Separator();

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var isExpanded = expandedItemIndex == i;

            ImGui.PushID(i);

            // Rank number
            ImGui.TextColored(Theme.TextMuted, (i + 1).ToString());
            ImGui.SameLine(30);

            // Icon + Name
            MainWindow.DrawGameIcon(item.IconId, IconSm);
            ImGui.SameLine(0, Theme.PadSmall);

            var cursorY = ImGui.GetCursorPosY();
            ImGui.SetCursorPosY(cursorY + (IconSm.Y - ImGui.GetTextLineHeight()) / 2);

            var nameText = string.IsNullOrEmpty(item.ItemName)
                ? string.Concat("Item #", item.ItemId.ToString())
                : item.ItemName;

            // Clickable name for expansion
            if (ImGui.Selectable(string.Concat(nameText, "##item", i.ToString()),
                isExpanded, ImGuiSelectableFlags.None, new Vector2(showCategory ? 200 : 200, 0)))
            {
                expandedItemIndex = isExpanded ? -1 : i;
            }

            // Category badge
            if (showCategory && !string.IsNullOrEmpty(item.CategoryName))
            {
                ImGui.SameLine(280);
                ImGui.SetCursorPosY(cursorY + (IconSm.Y - ImGui.GetTextLineHeight()) / 2);
                ImGui.TextColored(Theme.TextSecondary, item.CategoryName);
            }

            // Velocity
            var velX = showCategory ? 400f : 280f;
            ImGui.SameLine(velX);
            ImGui.SetCursorPosY(cursorY + (IconSm.Y - ImGui.GetTextLineHeight()) / 2);
            ImGui.TextColored(Theme.Accent, string.Concat(FormatNumber(item.RegularSaleVelocity), "/day"));

            // Average price
            var priceX = showCategory ? 490f : 370f;
            ImGui.SameLine(priceX);
            ImGui.SetCursorPosY(cursorY + (IconSm.Y - ImGui.GetTextLineHeight()) / 2);
            ImGui.TextColored(Theme.Gold, FormatGil(item.CurrentAveragePrice));

            // Daily gil volume
            var gilX = showCategory ? 590f : 470f;
            ImGui.SameLine(gilX);
            ImGui.SetCursorPosY(cursorY + (IconSm.Y - ImGui.GetTextLineHeight()) / 2);
            ImGui.TextColored(Theme.Success, FormatGil(item.EstimatedDailyGilVolume));

            // Reset cursor
            ImGui.SetCursorPosY(cursorY + IconSm.Y + 2);

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
