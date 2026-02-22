using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;

using Expedition.Diadem;
using Expedition.PlayerState;

namespace Expedition.UI;

/// <summary>
/// Draws the Diadem tab content. Provides item browser, GBR integration,
/// session tracking, and XP dashboard for Diadem gathering.
/// </summary>
public static class DiademTab
{
    // UI state
    private static int selectedClassIndex;  // 0=All, 1=Miner, 2=Botanist
    private static readonly string[] ClassFilterNames = { "All", "Miner", "Botanist" };
    private static readonly HashSet<uint> selectedItemIds = new();
    private static bool gbrRunning;

    // Layout constants
    private static readonly Vector2 ItemIcon = new(32, 32);
    private static readonly Vector2 ClassIcon = new(20, 20);
    private const float RowHeight = 36f;
    private const float FilterPanelWidth = 180f;

    public static void Draw(Expedition plugin)
    {
        DrawHeader(plugin);
        ImGui.Spacing();
        DrawSubTabs(plugin);
    }

    // ──────────────────────────────────────────────
    // Header Banner
    // ──────────────────────────────────────────────

    private static void DrawHeader(Expedition plugin)
    {
        var session = plugin.DiademSession;
        var drawList = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail().X;
        const float headerH = 48f;

        // Banner background
        drawList.AddRectFilled(
            pos,
            new Vector2(pos.X + avail, pos.Y + headerH),
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.08f, 0.10f, 0.16f, 1.00f)),
            4f);

        // Accent line at top
        drawList.AddRectFilled(
            pos,
            new Vector2(pos.X + avail, pos.Y + 2),
            ImGui.ColorConvertFloat4ToU32(Theme.Accent),
            4f);

        // Title
        drawList.AddText(new Vector2(pos.X + Theme.PadLarge, pos.Y + 6f),
            ImGui.ColorConvertFloat4ToU32(Theme.Accent), "Diadem Gathering  —  Ishgard Restoration");

        // Player levels + session info on second line
        var minLevel = JobSwitchManager.GetPlayerJobLevel(JobSwitchManager.MIN);
        var btnLevel = JobSwitchManager.GetPlayerJobLevel(JobSwitchManager.BTN);

        var statsText = "";
        if (minLevel >= 0) statsText += $"MIN Lv{minLevel}";
        if (btnLevel >= 0) statsText += $"  |  BTN Lv{btnLevel}";
        if (selectedItemIds.Count > 0) statsText += $"  |  {selectedItemIds.Count} items selected";
        if (session.IsActive) statsText += $"  |  Session: {session.Elapsed:hh\\:mm\\:ss}";
        if (plugin.DiademNavigator.IsNavigating)
            statsText += plugin.DiademNavigator.UsingWindmire ? "  |  Nav: Windmire" : "  |  Nav: Flying";

        drawList.AddText(new Vector2(pos.X + Theme.PadLarge, pos.Y + 26f),
            ImGui.ColorConvertFloat4ToU32(Theme.TextSecondary), statsText);

        // Session button on the right
        ImGui.SetCursorScreenPos(new Vector2(pos.X + avail - 140, pos.Y + 10f));
        if (session.IsActive)
        {
            if (Theme.DangerButton("Stop Session", new Vector2(120, 28)))
            {
                session.Stop();
                StopGbr(plugin);
            }
        }
        else
        {
            var canStart = selectedItemIds.Count > 0;
            if (!canStart) ImGui.BeginDisabled();
            if (Theme.PrimaryButton("Start Session", new Vector2(120, 28)))
            {
                session.Start(selectedItemIds.ToList());
                InjectAndStartGbr(plugin);
            }
            if (!canStart) ImGui.EndDisabled();
        }

        ImGui.SetCursorScreenPos(new Vector2(pos.X, pos.Y + headerH));
        ImGui.Dummy(new Vector2(avail, 0)); // Advance cursor past banner
    }

    // ──────────────────────────────────────────────
    // Sub-tabs
    // ──────────────────────────────────────────────

    private static void DrawSubTabs(Expedition plugin)
    {
        if (ImGui.BeginTabBar("DiademSubTabs"))
        {
            if (ImGui.BeginTabItem("Items"))
            {
                DrawItemBrowser(plugin);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Recommendations"))
            {
                DrawRecommendations(plugin);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Navigation"))
            {
                DrawNavigation(plugin);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("GBR Controls"))
            {
                DrawGbrControls(plugin);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Session"))
            {
                DrawSessionDashboard(plugin);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("XP"))
            {
                DrawXpDashboard(plugin);
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    // ──────────────────────────────────────────────
    // Items Sub-tab — two-panel layout
    // ──────────────────────────────────────────────

    private static void DrawItemBrowser(Expedition plugin)
    {
        ImGui.Spacing();

        var minLevel = JobSwitchManager.GetPlayerJobLevel(JobSwitchManager.MIN);
        var btnLevel = JobSwitchManager.GetPlayerJobLevel(JobSwitchManager.BTN);

        // ── Left: Filter Panel ──
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.SectionBg);
        ImGui.BeginChild("DiademFilterPanel", new Vector2(FilterPanelWidth, -1), true);
        ImGui.PopStyleColor();

        Theme.SectionHeader("Filter", Theme.Accent);
        ImGui.Spacing();

        ImGui.TextColored(Theme.TextSecondary, "Class");
        ImGui.SetNextItemWidth(-1);
        ImGui.Combo("##DiademClassFilter", ref selectedClassIndex, ClassFilterNames, ClassFilterNames.Length);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        Theme.SectionHeader("Selection", Theme.Gold);
        ImGui.Spacing();
        ImGui.TextColored(Theme.TextSecondary, $"{selectedItemIds.Count} items selected");

        if (selectedItemIds.Count > 0)
        {
            ImGui.Spacing();
            if (Theme.SecondaryButton("Clear All", new Vector2(-1, 24)))
                selectedItemIds.Clear();

            ImGui.Spacing();
            if (Theme.PrimaryButton("Select All Visible", new Vector2(-1, 24)))
            {
                foreach (DiademNodeTier tier in Enum.GetValues<DiademNodeTier>())
                {
                    foreach (var item in GetFilteredItems(tier))
                    {
                        var playerLevel = item.GatherClass == DiademGatherClass.Miner ? minLevel : btnLevel;
                        if (playerLevel < 0 || playerLevel >= item.RecommendedMinLevel)
                            selectedItemIds.Add(item.ItemId);
                    }
                }
            }
        }
        else
        {
            ImGui.Spacing();
            if (Theme.PrimaryButton("Select All Visible", new Vector2(-1, 24)))
            {
                foreach (DiademNodeTier tier in Enum.GetValues<DiademNodeTier>())
                {
                    foreach (var item in GetFilteredItems(tier))
                    {
                        var playerLevel = item.GatherClass == DiademGatherClass.Miner ? minLevel : btnLevel;
                        if (playerLevel < 0 || playerLevel >= item.RecommendedMinLevel)
                            selectedItemIds.Add(item.ItemId);
                    }
                }
            }
        }

        ImGui.EndChild();

        ImGui.SameLine(0, Theme.Pad);

        // ── Right: Item List ──
        ImGui.BeginChild("DiademItemList", Vector2.Zero, false);

        foreach (DiademNodeTier tier in Enum.GetValues<DiademNodeTier>())
        {
            var tierItems = GetFilteredItems(tier).ToList();
            if (tierItems.Count == 0) continue;

            // Tier header with count
            var tierName = DiademItemDatabase.GetTierDisplayName(tier);
            var levelRange = DiademItemDatabase.GetTierLevelRange(tier);
            var tierColor = GetTierColor(tier);

            ImGui.Spacing();
            Theme.LabeledSeparator($"{tierName}  —  {levelRange}");
            ImGui.Spacing();

            foreach (var item in tierItems)
            {
                var playerLevel = item.GatherClass == DiademGatherClass.Miner ? minLevel : btnLevel;
                var tooLow = playerLevel >= 0 && playerLevel < item.RecommendedMinLevel;

                if (tooLow) ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.4f);

                ImGui.PushID((int)item.ItemId);
                DrawItemRow(item);
                ImGui.PopID();

                if (tooLow) ImGui.PopStyleVar();
            }
        }

        ImGui.EndChild();
    }

    /// <summary>
    /// Draws a single item row matching the Browse tab pattern:
    /// [checkbox] [32x32 icon] [Name]                    [class icon] [node type]
    /// </summary>
    private static void DrawItemRow(DiademItem item)
    {
        var startY = ImGui.GetCursorPosY();
        var contentWidth = ImGui.GetContentRegionAvail().X;

        // Checkbox
        var isSelected = selectedItemIds.Contains(item.ItemId);
        ImGui.SetCursorPosY(startY + (RowHeight - ImGui.GetFrameHeight()) / 2);
        if (ImGui.Checkbox("##chk", ref isSelected))
        {
            if (isSelected) selectedItemIds.Add(item.ItemId);
            else selectedItemIds.Remove(item.ItemId);
        }

        ImGui.SameLine(0, Theme.PadSmall);

        // Item icon (32x32)
        ImGui.SetCursorPosY(startY + (RowHeight - ItemIcon.Y) / 2);
        if (item.IconId > 0)
            MainWindow.DrawGameIcon(item.IconId, ItemIcon);
        else
            ImGui.Dummy(ItemIcon);

        ImGui.SameLine(0, Theme.Pad);

        // Item name — vertically centered
        ImGui.SetCursorPosY(startY + (RowHeight - ImGui.GetTextLineHeight()) / 2);
        ImGui.Text(item.Name);

        // Right-aligned metadata
        var rightX = contentWidth - 100;

        ImGui.SameLine(rightX);
        ImGui.SetCursorPosY(startY + (RowHeight - ClassIcon.Y) / 2);

        // Class icon (MIN=62116, BTN=62117)
        var classIconId = item.GatherClass == DiademGatherClass.Miner ? 62116u : 62117u;
        MainWindow.DrawGameIcon(classIconId, ClassIcon);

        ImGui.SameLine(0, Theme.PadSmall);
        ImGui.SetCursorPosY(startY + (RowHeight - ImGui.GetTextLineHeight()) / 2);
        ImGui.TextColored(Theme.TextMuted, item.NodeType);

        // Weather badge on same line if applicable
        if (item.WeatherCondition != null)
        {
            ImGui.SameLine(0, Theme.Pad);
            ImGui.SetCursorPosY(startY + (RowHeight - ImGui.GetTextLineHeight()) / 2);
            ImGui.TextColored(Theme.TimedNode, item.WeatherCondition);
        }

        // Tooltip
        ImGui.SetCursorPosY(startY);
        ImGui.Dummy(new Vector2(contentWidth, RowHeight));
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextColored(Theme.TextPrimary, item.Name);

            var classTxt = item.GatherClass == DiademGatherClass.Miner ? "Miner" : "Botanist";
            ImGui.TextColored(Theme.TextSecondary, $"{classTxt}  —  {item.NodeType}");
            ImGui.TextColored(Theme.TextSecondary,
                $"Recommended: Lv{item.RecommendedMinLevel}-{item.RecommendedMaxLevel}");

            if (item.IsExpert)
                ImGui.TextColored(Theme.Expert, "Expert Node");
            if (item.WeatherCondition != null)
                ImGui.TextColored(Theme.TimedNode, $"Requires: {item.WeatherCondition} weather");

            ImGui.EndTooltip();
        }
    }

    // ──────────────────────────────────────────────
    // Recommendations Sub-tab
    // ──────────────────────────────────────────────

    private static void DrawRecommendations(Expedition plugin)
    {
        ImGui.Spacing();

        var minLevel = JobSwitchManager.GetPlayerJobLevel(JobSwitchManager.MIN);
        var btnLevel = JobSwitchManager.GetPlayerJobLevel(JobSwitchManager.BTN);

        ImGui.BeginChild("DiademRecs", Vector2.Zero, false);

        // Gear check banner — always visible, provides stat thresholds
        DrawGearCheckSection(minLevel, btnLevel);
        ImGui.Spacing();
        ImGui.Spacing();

        DrawClassRecommendationCard("Miner", DiademGatherClass.Miner, minLevel, Theme.Accent);
        ImGui.Spacing();
        ImGui.Spacing();
        DrawClassRecommendationCard("Botanist", DiademGatherClass.Botanist, btnLevel, Theme.Success);

        ImGui.EndChild();
    }

    /// <summary>
    /// Draws a gear check section showing minimum Gathering stat requirements per tier,
    /// with actionable advice on how to improve gear.
    /// </summary>
    private static void DrawGearCheckSection(int minLevel, int btnLevel)
    {
        // Determine the player's likely recommended tier based on highest gatherer level
        var highestLevel = Math.Max(minLevel, btnLevel);
        var recTier = highestLevel >= 0 ? DiademItemDatabase.GetRecommendedTier(highestLevel) : DiademNodeTier.Tier1;

        // Draw gear check card
        if (Theme.BeginCard("GearCheck", 0))
        {
            ImGui.Spacing();
            Theme.SectionHeader("Gear Check", Theme.Warning);
            Theme.HelpMarker(
                "Your Gathering stat determines your success rate on nodes. " +
                "If you see low percentages (under 50%) in the gathering window, " +
                "you need better gathering gear for that tier.");
            ImGui.Spacing();
            ImGui.Spacing();

            // Tip banner for the player's current tier
            var tip = DiademItemDatabase.GetGearTip(recTier);
            var tierName = DiademItemDatabase.GetTierDisplayName(recTier);
            var minGathering = DiademItemDatabase.GetMinGatheringForTier(recTier);

            if (minGathering > 0)
            {
                // Draw amber info box
                var drawList = ImGui.GetWindowDrawList();
                var pos = ImGui.GetCursorScreenPos();
                var avail = ImGui.GetContentRegionAvail().X;
                const float tipH = 56f;

                drawList.AddRectFilled(
                    pos,
                    new Vector2(pos.X + avail, pos.Y + tipH),
                    ImGui.ColorConvertFloat4ToU32(new Vector4(0.20f, 0.16f, 0.06f, 0.80f)),
                    4f);
                drawList.AddRectFilled(
                    pos,
                    new Vector2(pos.X + 3, pos.Y + tipH),
                    ImGui.ColorConvertFloat4ToU32(Theme.Warning),
                    4f);

                drawList.AddText(new Vector2(pos.X + 14, pos.Y + 6),
                    ImGui.ColorConvertFloat4ToU32(Theme.Warning),
                    $"Your recommended tier: {tierName}  —  need {minGathering}+ Gathering stat");
                drawList.AddText(new Vector2(pos.X + 14, pos.Y + 26),
                    ImGui.ColorConvertFloat4ToU32(Theme.TextSecondary),
                    tip.Length > 90 ? tip[..90] + "..." : tip);

                ImGui.Dummy(new Vector2(avail, tipH));
                ImGui.Spacing();
            }

            // Stat requirements table
            ImGui.TextColored(Theme.TextMuted, "  Tier");
            ImGui.SameLine(200);
            ImGui.TextColored(Theme.TextMuted, "Min Gathering");
            ImGui.SameLine(340);
            ImGui.TextColored(Theme.TextMuted, "Gear Needed");
            ImGui.Separator();

            DrawGearRow("Tier 1 (Lv10)", 0, "Any", recTier == DiademNodeTier.Tier1);
            DrawGearRow("Tier 2 (Lv60)", 600, "Lv60+ Gathering Gear", recTier == DiademNodeTier.Tier2);
            DrawGearRow("Tier 3 (Lv80)", 2000, "Lv75-80 Gathering Gear", recTier == DiademNodeTier.Tier3);
            DrawGearRow("Expert (Lv80)", 2200, "Lv80 BiS + Materia", recTier == DiademNodeTier.Expert);

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            // Quick gear tips
            ImGui.TextColored(Theme.Accent, "Where to get gathering gear:");
            ImGui.Spacing();
            ImGui.TextColored(Theme.TextSecondary, "  \u2022  Vendor: Ishgard, Eulmore, or Crystarium gear vendors");
            ImGui.TextColored(Theme.TextSecondary, "  \u2022  Market Board: Search for your level range gathering gear");
            ImGui.TextColored(Theme.TextSecondary, "  \u2022  Crafting: HQ crafted gear gives higher stats");
            ImGui.TextColored(Theme.TextSecondary, "  \u2022  Scrip Exchange: Yellow/White gatherer scrip gear");
            ImGui.Spacing();
            ImGui.TextColored(Theme.TextMuted, "Tip: Buy NPC vendor gear for quick upgrades — it's cheap and sufficient for Diadem.");
            ImGui.Spacing();
        }
        Theme.EndCard();
    }

    private static void DrawGearRow(string tierLabel, int minGathering, string gearNeeded, bool isCurrentTier)
    {
        var color = isCurrentTier ? Theme.Gold : Theme.TextSecondary;
        var prefix = isCurrentTier ? "\u25b6 " : "  ";

        ImGui.TextColored(color, $"{prefix}{tierLabel}");
        ImGui.SameLine(200);
        ImGui.TextColored(color, minGathering > 0 ? $"{minGathering:N0}" : "—");
        ImGui.SameLine(340);
        ImGui.TextColored(color, gearNeeded);
    }

    private static void DrawClassRecommendationCard(string className, DiademGatherClass cls, int level, Vector4 color)
    {
        // Card container
        if (Theme.BeginCard($"Rec_{className}", 0))
        {
            ImGui.Spacing();

            if (level < 0)
            {
                Theme.SectionHeader($"{className}", Theme.TextMuted);
                ImGui.Spacing();
                ImGui.TextColored(Theme.TextMuted, "Level data unavailable. Make sure you're logged in.");
                ImGui.Spacing();
                Theme.EndCard();
                return;
            }

            // Header with level badge
            Theme.SectionHeader(className, color);
            ImGui.SameLine(0, Theme.Pad);
            Theme.Badge($"Lv{level}", color);
            ImGui.Spacing();
            ImGui.Spacing();

            var recommended = DiademItemDatabase.GetRecommendedItems(cls, level);
            if (recommended.Count == 0)
            {
                if (level >= 80)
                {
                    ImGui.TextColored(Theme.TextSecondary,
                        "At Lv80+, target Expert and Umbral nodes for the best XP and collectable rewards.");

                    // Show expert items
                    var expertItems = DiademItemDatabase.GetItemsForClass(cls)
                        .Where(i => i.NodeTier == DiademNodeTier.Expert)
                        .Take(5)
                        .ToList();

                    if (expertItems.Count > 0)
                    {
                        ImGui.Spacing();
                        ImGui.TextColored(Theme.TextMuted, "Top expert items:");
                        ImGui.Spacing();
                        foreach (var item in expertItems)
                            DrawRecommendedItemRow(item);
                    }
                }
                else
                {
                    ImGui.TextColored(Theme.TextMuted, "No items match your current level range.");
                }
            }
            else
            {
                ImGui.TextColored(Theme.TextSecondary, "Best items for your level:");
                ImGui.Spacing();

                foreach (var item in recommended)
                    DrawRecommendedItemRow(item);
            }

            // Next tier hint
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            if (level < 60)
                Theme.KeyValue("Next tier:", "Tier 2 nodes at Lv60", Theme.TextMuted);
            else if (level < 80)
                Theme.KeyValue("Next tier:", "Tier 3 + Expert nodes at Lv80", Theme.TextMuted);
            else
                Theme.KeyValue("Status:", "All tiers unlocked", Theme.Success);

            ImGui.Spacing();
        }
        Theme.EndCard();
    }

    private static void DrawRecommendedItemRow(DiademItem item)
    {
        var startY = ImGui.GetCursorPosY();

        // Icon
        ImGui.SetCursorPosY(startY + (RowHeight - ItemIcon.Y) / 2);
        if (item.IconId > 0)
            MainWindow.DrawGameIcon(item.IconId, ItemIcon);
        else
            ImGui.Dummy(ItemIcon);

        ImGui.SameLine(0, Theme.Pad);

        // Name
        ImGui.SetCursorPosY(startY + (RowHeight - ImGui.GetTextLineHeight()) / 2);
        ImGui.TextColored(Theme.TextPrimary, item.Name);

        // Select/Selected button on right
        ImGui.SameLine(ImGui.GetContentRegionAvail().X - 60);
        ImGui.SetCursorPosY(startY + (RowHeight - 22) / 2);

        if (!selectedItemIds.Contains(item.ItemId))
        {
            if (ImGui.SmallButton($"+ Select##rec_{item.ItemId}"))
                selectedItemIds.Add(item.ItemId);
        }
        else
        {
            ImGui.TextColored(Theme.Success, "Selected");
        }

        ImGui.SetCursorPosY(startY + RowHeight);
    }

    // ──────────────────────────────────────────────
    // Navigation Sub-tab — Windmire fast-travel
    // ──────────────────────────────────────────────

    private static void DrawNavigation(Expedition plugin)
    {
        ImGui.Spacing();
        ImGui.BeginChild("NavScroll", Vector2.Zero, false);

        var nav = plugin.DiademNavigator;
        var inDiadem = DiademWindmires.IsInDiadem();

        // Status card
        if (Theme.BeginCard("NavStatus", 0))
        {
            ImGui.Spacing();
            Theme.SectionHeader("Windmire Navigation", Theme.Accent);
            Theme.HelpMarker(
                "Windmires are the tornado launch pads that catapult you between Diadem islands. " +
                "This navigator automatically uses them when they provide a faster route.");
            ImGui.Spacing();
            ImGui.Spacing();

            if (!inDiadem)
            {
                ImGui.TextColored(Theme.TextMuted, "Not in the Diadem. Enter the Diadem to use Windmire navigation.");
            }
            else if (!plugin.Ipc.Vnavmesh.IsAvailable)
            {
                ImGui.TextColored(Theme.Error, "vnavmesh is not available. Install vnavmesh or ensure GBR is loaded.");
            }
            else
            {
                // Nav status
                var stateColor = nav.IsNavigating ? Theme.PhaseActive : Theme.TextMuted;
                var stateLabel = nav.IsNavigating
                    ? (nav.UsingWindmire ? "Windmire Route Active" : "Direct Flight Active")
                    : "Ready";
                Theme.StatusDot(stateColor, stateLabel);

                if (!string.IsNullOrEmpty(nav.StatusMessage))
                {
                    ImGui.Spacing();
                    ImGui.TextColored(Theme.TextSecondary, $"  {nav.StatusMessage}");
                }

                ImGui.Spacing();
                ImGui.Spacing();

                // Quick actions
                if (nav.IsNavigating)
                {
                    if (Theme.DangerButton("Stop Navigation", new Vector2(180, 32)))
                        nav.Stop();
                }
                else
                {
                    if (Theme.PrimaryButton("Fly to Nearest Windmire", new Vector2(220, 32)))
                        nav.FlyToNearestWindmire();
                }

                // Nearest Windmire info
                var playerPos = DalamudApi.ObjectTable.LocalPlayer?.Position ?? System.Numerics.Vector3.Zero;
                if (playerPos != System.Numerics.Vector3.Zero)
                {
                    var dist = DiademWindmires.DistanceToNearestWindmire(playerPos);
                    ImGui.Spacing();
                    Theme.KeyValue("Nearest Windmire:", $"{dist:N0}y away");
                }
            }

            ImGui.Spacing();
        }
        Theme.EndCard();

        ImGui.Spacing();
        ImGui.Spacing();

        // Windmire directory — list all connections
        if (Theme.BeginCard("WindmireList", 0))
        {
            ImGui.Spacing();
            Theme.SectionHeader("Windmire Directory", Theme.Gold);
            ImGui.Spacing();
            ImGui.TextColored(Theme.TextSecondary,
                $"{DiademWindmires.All.Length} Windmires (9 bidirectional pairs connecting island platforms)");
            ImGui.Spacing();
            ImGui.Spacing();

            // Table header
            ImGui.TextColored(Theme.TextMuted, "  #");
            ImGui.SameLine(40);
            ImGui.TextColored(Theme.TextMuted, "From");
            ImGui.SameLine(200);
            ImGui.TextColored(Theme.TextMuted, "To");
            ImGui.SameLine(360);
            ImGui.TextColored(Theme.TextMuted, "Action");
            ImGui.Separator();

            for (var i = 0; i < DiademWindmires.All.Length; i++)
            {
                var wm = DiademWindmires.All[i];

                ImGui.PushID(i);

                ImGui.TextColored(Theme.TextMuted, $"  {i + 1}");
                ImGui.SameLine(40);
                ImGui.TextColored(Theme.TextSecondary, $"({wm.From.X:F0}, {wm.From.Z:F0})");
                ImGui.SameLine(200);
                ImGui.TextColored(Theme.TextSecondary, $"({wm.To.X:F0}, {wm.To.Z:F0})");

                if (inDiadem && plugin.Ipc.Vnavmesh.IsAvailable && !nav.IsNavigating)
                {
                    ImGui.SameLine(360);
                    if (ImGui.SmallButton("Fly Here"))
                        nav.NavigateTo(wm.From);
                }

                ImGui.PopID();
            }

            ImGui.Spacing();
        }
        Theme.EndCard();

        ImGui.EndChild();
    }

    // ──────────────────────────────────────────────
    // GBR Controls Sub-tab
    // ──────────────────────────────────────────────

    private static void DrawGbrControls(Expedition plugin)
    {
        ImGui.Spacing();

        ImGui.BeginChild("GbrControlsScroll", Vector2.Zero, false);

        // Warning card
        DrawWarningBanner();
        ImGui.Spacing();
        ImGui.Spacing();

        // GBR status card
        if (Theme.BeginCard("GbrStatus", 60))
        {
            ImGui.Spacing();
            var gbrEnabled = plugin.Ipc.GatherBuddy.GetAutoGatherEnabled();
            gbrRunning = gbrEnabled;

            Theme.StatusDot(
                gbrRunning ? Theme.Success : Theme.TextMuted,
                gbrRunning ? "GBR Auto-Gather: Running" : "GBR Auto-Gather: Stopped");

            var gbrStatus = plugin.Ipc.GatherBuddy.GetStatusText();
            if (!string.IsNullOrEmpty(gbrStatus))
                Theme.KeyValue("  Status:", gbrStatus);

            ImGui.Spacing();
        }
        Theme.EndCard();

        ImGui.Spacing();
        ImGui.Spacing();

        // Selected items
        Theme.SectionHeader("Selected Items", Theme.Gold);
        ImGui.Spacing();

        if (selectedItemIds.Count == 0)
        {
            ImGui.TextColored(Theme.TextMuted, "No items selected. Go to the Items tab to select items.");
        }
        else
        {
            foreach (var itemId in selectedItemIds)
            {
                var item = DiademItemDatabase.AllItems.FirstOrDefault(i => i.ItemId == itemId);
                if (item == null) continue;

                if (item.IconId > 0)
                {
                    MainWindow.DrawGameIcon(item.IconId, new Vector2(24, 24));
                    ImGui.SameLine(0, Theme.PadSmall);
                }

                ImGui.Text(item.Name);
            }
        }

        ImGui.Spacing();
        ImGui.Spacing();

        // Skill & automation settings
        DrawSkillSettings(plugin);

        ImGui.EndChild();
    }

    private static void DrawSkillSettings(Expedition plugin)
    {
        if (Theme.BeginCard("SkillSettings", 0))
        {
            ImGui.Spacing();
            Theme.SectionHeader("Gathering Skills & Automation", Theme.Accent);
            Theme.HelpMarker(
                "These settings configure GBR's skill rotation when auto-gathering in the Diadem.\n" +
                "When enabled, Expedition automatically applies these on session start.");
            ImGui.Spacing();
            ImGui.Spacing();

            // Auto-apply toggle
            var autoApply = Expedition.Config.DiademAutoApplySkillPreset;
            if (ImGui.Checkbox("Auto-apply skill preset on session start", ref autoApply))
            {
                Expedition.Config.DiademAutoApplySkillPreset = autoApply;
                Expedition.Config.Save();
            }

            if (!autoApply) ImGui.BeginDisabled();

            ImGui.Spacing();
            ImGui.Indent(Theme.PadLarge);

            // Rotation solver
            ImGui.TextColored(Theme.TextSecondary, "Rotation Solver");
            ImGui.TextColored(Theme.TextMuted,
                "  Automatically picks the optimal gathering skill sequence per node.");
            ImGui.Spacing();

            // Gathering skills summary
            ImGui.TextColored(Theme.TextSecondary, "Skills Enabled");
            ImGui.TextColored(Theme.TextMuted,
                "  Bountiful Yield, King's Yield II, Solid Age, The Giving Land, Twelve's Bounty");
            ImGui.Spacing();

            // Cordials toggle
            var cordials = Expedition.Config.DiademEnableCordials;
            if (ImGui.Checkbox("Enable cordial usage (GP recovery)", ref cordials))
            {
                Expedition.Config.DiademEnableCordials = cordials;
                Expedition.Config.Save();
            }
            ImGui.TextColored(Theme.TextMuted,
                "  GBR will use cordials between nodes to recover GP faster.");
            ImGui.Spacing();

            // Aether Cannon toggle
            var cannon = Expedition.Config.DiademEnableAetherCannon;
            if (ImGui.Checkbox("Enable Aether Cannon (kill Diadem mobs)", ref cannon))
            {
                Expedition.Config.DiademEnableAetherCannon = cannon;
                Expedition.Config.Save();
            }
            ImGui.TextColored(Theme.TextMuted,
                "  Automatically fires aethercannon at Diadem enemies for bonus points.");
            ImGui.Spacing();

            // Windmires toggle
            var windmires = Expedition.Config.DiademUseWindmires;
            if (ImGui.Checkbox("Enable Windmire fast-travel", ref windmires))
            {
                Expedition.Config.DiademUseWindmires = windmires;
                Expedition.Config.Save();
            }
            ImGui.TextColored(Theme.TextMuted,
                "  GBR will use tornado launch pads to travel between islands.");

            ImGui.Unindent(Theme.PadLarge);

            if (!autoApply) ImGui.EndDisabled();

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            // Manual apply button
            if (Theme.SecondaryButton("Apply Skill Preset Now", new Vector2(220, 28)))
            {
                var ok = plugin.Ipc.GatherBuddyLists.ApplyDiademSkillPreset(
                    enableCordials: Expedition.Config.DiademEnableCordials,
                    enableAetherCannon: Expedition.Config.DiademEnableAetherCannon);
                DalamudApi.Log.Information(ok
                    ? "[Diadem] Manually applied skill preset."
                    : "[Diadem] Failed to apply skill preset.");
            }
            ImGui.SameLine(0, Theme.Pad);
            ImGui.TextColored(Theme.TextMuted, "Apply without restarting auto-gather");

            ImGui.Spacing();
        }
        Theme.EndCard();
    }

    private static void DrawWarningBanner()
    {
        var drawList = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail().X;
        const float bannerH = 50f;

        // Amber-tinted background
        drawList.AddRectFilled(
            pos,
            new Vector2(pos.X + avail, pos.Y + bannerH),
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.20f, 0.16f, 0.06f, 0.80f)),
            4f);

        // Amber accent bar
        drawList.AddRectFilled(
            pos,
            new Vector2(pos.X + 3, pos.Y + bannerH),
            ImGui.ColorConvertFloat4ToU32(Theme.Warning),
            4f);

        // Warning text
        drawList.AddText(new Vector2(pos.X + 14, pos.Y + 6),
            ImGui.ColorConvertFloat4ToU32(Theme.Warning),
            "Diadem is an instanced zone — GBR may not fully work here.");

        drawList.AddText(new Vector2(pos.X + 14, pos.Y + 26),
            ImGui.ColorConvertFloat4ToU32(Theme.TextSecondary),
            "If auto-gather doesn't function, use Items/XP tabs for manual tracking.");

        ImGui.Dummy(new Vector2(avail, bannerH));
    }

    // ──────────────────────────────────────────────
    // Session Sub-tab
    // ──────────────────────────────────────────────

    private static void DrawSessionDashboard(Expedition plugin)
    {
        var session = plugin.DiademSession;
        session.Update();

        ImGui.Spacing();

        if (!session.IsActive && session.Elapsed == TimeSpan.Zero)
        {
            ImGui.Spacing();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Theme.PadLarge);
            ImGui.TextColored(Theme.TextMuted, "No active session.");
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Theme.PadLarge);
            ImGui.TextColored(Theme.TextMuted, "Select items in the Items tab, then click Start Session.");
            return;
        }

        ImGui.BeginChild("SessionScroll", Vector2.Zero, false);

        // Metrics cards — three side by side
        var cardWidth = (ImGui.GetContentRegionAvail().X - Theme.Pad * 2) / 3;

        DrawMetricCard("Duration", $"{session.Elapsed:hh\\:mm\\:ss}", Theme.Accent, cardWidth);
        ImGui.SameLine(0, Theme.Pad);
        DrawMetricCard("Items Gathered", session.TotalItemsGathered.ToString("N0"), Theme.Success, cardWidth);
        ImGui.SameLine(0, Theme.Pad);
        DrawMetricCard("Items / Hour", session.ItemsPerHour.ToString("N1"), Theme.Gold, cardWidth);

        ImGui.Spacing();
        ImGui.Spacing();

        // Per-item breakdown
        Theme.SectionHeader("Item Breakdown", Theme.Accent);
        ImGui.Spacing();

        if (selectedItemIds.Count > 0)
        {
            // Table-style header
            ImGui.TextColored(Theme.TextMuted, "  Item");
            ImGui.SameLine(ImGui.GetContentRegionAvail().X - 60);
            ImGui.TextColored(Theme.TextMuted, "Gathered");
            ImGui.Separator();

            foreach (var itemId in selectedItemIds)
            {
                var item = DiademItemDatabase.AllItems.FirstOrDefault(i => i.ItemId == itemId);
                if (item == null) continue;

                var delta = session.GetItemDelta(itemId);
                var startY = ImGui.GetCursorPosY();

                // Icon + name
                ImGui.SetCursorPosY(startY + (28 - ItemIcon.Y) / 2 + 2);
                if (item.IconId > 0)
                {
                    MainWindow.DrawGameIcon(item.IconId, new Vector2(24, 24));
                    ImGui.SameLine(0, Theme.PadSmall);
                }
                ImGui.SetCursorPosY(startY + (28 - ImGui.GetTextLineHeight()) / 2 + 2);
                ImGui.Text(item.Name);

                // Delta on the right
                ImGui.SameLine(ImGui.GetContentRegionAvail().X - 50);
                ImGui.SetCursorPosY(startY + (28 - ImGui.GetTextLineHeight()) / 2 + 2);
                if (delta > 0)
                    ImGui.TextColored(Theme.Success, $"+{delta}");
                else
                    ImGui.TextColored(Theme.TextMuted, "—");

                ImGui.SetCursorPosY(startY + 30);
            }
        }

        ImGui.EndChild();
    }

    private static void DrawMetricCard(string label, string value, Vector4 color, float width)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.CardBg);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 4f);
        ImGui.BeginChild($"Metric_{label}", new Vector2(width, 64), true);
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();

        ImGui.Spacing();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Theme.Pad);
        ImGui.TextColored(Theme.TextMuted, label);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Theme.Pad);
        ImGui.PushFont(null); // Use default font — just make text bigger via color
        ImGui.TextColored(color, value);
        ImGui.PopFont();

        ImGui.EndChild();
    }

    // ──────────────────────────────────────────────
    // XP Sub-tab
    // ──────────────────────────────────────────────

    private static void DrawXpDashboard(Expedition plugin)
    {
        var session = plugin.DiademSession;
        session.Update();

        ImGui.Spacing();

        ImGui.BeginChild("XpDashboard", Vector2.Zero, false);

        // Side-by-side XP cards
        var cardWidth = (ImGui.GetContentRegionAvail().X - Theme.Pad) / 2;

        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.CardBg);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 4f);
        ImGui.BeginChild("XpCard_MIN", new Vector2(cardWidth, 0), true);
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();

        DrawXpCardContent("Miner", Theme.Accent, 62116,
            session.CurrentMinLevel, session.CurrentMinXp, session.MinXpToNextLevel,
            session.MinXpGained, session.MinXpPerHour, session.EstMinTimeToLevel,
            session.IsActive);

        ImGui.EndChild();

        ImGui.SameLine(0, Theme.Pad);

        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.CardBg);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 4f);
        ImGui.BeginChild("XpCard_BTN", new Vector2(cardWidth, 0), true);
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();

        DrawXpCardContent("Botanist", Theme.Success, 62117,
            session.CurrentBtnLevel, session.CurrentBtnXp, session.BtnXpToNextLevel,
            session.BtnXpGained, session.BtnXpPerHour, session.EstBtnTimeToLevel,
            session.IsActive);

        ImGui.EndChild();

        ImGui.EndChild();
    }

    private static void DrawXpCardContent(
        string className, Vector4 color, uint classIconId,
        int level, int currentXp, int xpToNext,
        int xpGained, double xpPerHour, TimeSpan? timeToLevel,
        bool sessionActive)
    {
        ImGui.Spacing();

        // Header with class icon
        MainWindow.DrawGameIcon(classIconId, new Vector2(28, 28));
        ImGui.SameLine(0, Theme.Pad);

        var headerY = ImGui.GetCursorPosY();
        ImGui.SetCursorPosY(headerY + (28 - ImGui.GetTextLineHeight()) / 2);

        if (level >= 0)
            ImGui.TextColored(color, $"{className}  Lv{level}");
        else
            ImGui.TextColored(Theme.TextMuted, $"{className}  —");

        ImGui.Spacing();
        ImGui.Spacing();

        if (level < 0)
        {
            ImGui.TextColored(Theme.TextMuted, "Level data unavailable.");
            return;
        }

        // XP progress bar
        if (xpToNext > 0)
        {
            var fraction = (float)currentXp / xpToNext;
            var overlay = $"{currentXp:N0} / {xpToNext:N0}";
            Theme.ProgressBar(fraction, color, overlay, 22);
            ImGui.TextColored(Theme.TextMuted, $"{fraction:P1} to next level");
        }
        else
        {
            ImGui.TextColored(Theme.Gold, "MAX LEVEL");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Session XP stats
        if (sessionActive || xpGained > 0)
        {
            Theme.KeyValue("XP Gained:", $"+{xpGained:N0}", Theme.Success);
            ImGui.Spacing();
            Theme.KeyValue("XP / Hour:", xpPerHour > 0 ? $"~{xpPerHour:N0}" : "calculating...");
            ImGui.Spacing();

            if (timeToLevel.HasValue && xpToNext > 0)
            {
                var ttl = timeToLevel.Value;
                string ttlText;
                if (ttl.TotalMinutes < 1)
                    ttlText = "< 1 min";
                else if (ttl.TotalHours < 1)
                    ttlText = $"~{(int)ttl.TotalMinutes}m";
                else
                    ttlText = $"~{(int)ttl.TotalHours}h {ttl.Minutes}m";

                Theme.KeyValue("Time to Level:", ttlText, Theme.Gold);
            }
        }
        else
        {
            ImGui.TextColored(Theme.TextMuted, "Start a session to");
            ImGui.TextColored(Theme.TextMuted, "track XP gains.");
        }

        ImGui.Spacing();
    }

    // ──────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────

    private static IEnumerable<DiademItem> GetFilteredItems(DiademNodeTier tier)
    {
        var tierItems = DiademItemDatabase.GetItemsByTier(tier);
        return selectedClassIndex switch
        {
            1 => tierItems.Where(i => i.GatherClass == DiademGatherClass.Miner),
            2 => tierItems.Where(i => i.GatherClass == DiademGatherClass.Botanist),
            _ => tierItems,
        };
    }

    private static Vector4 GetTierColor(DiademNodeTier tier) => tier switch
    {
        DiademNodeTier.Tier1 => Theme.Success,
        DiademNodeTier.Tier2 => Theme.Accent,
        DiademNodeTier.Tier3 => Theme.Gold,
        DiademNodeTier.Expert => Theme.Expert,
        DiademNodeTier.Umbral => Theme.TimedNode,
        _ => Theme.TextSecondary,
    };

    private static void InjectAndStartGbr(Expedition plugin)
    {
        if (selectedItemIds.Count == 0) return;

        // Enable GBR's Windmire jumps for Diadem (defaults to OFF in GBR)
        if (Expedition.Config.DiademUseWindmires)
        {
            var windmireOk = plugin.Ipc.GatherBuddyLists.EnableDiademWindmires();
            if (windmireOk)
                DalamudApi.Log.Information("[Diadem] Enabled GBR Windmire jumps for faster island traversal.");
            else
                DalamudApi.Log.Warning("[Diadem] Could not enable GBR Windmire jumps — GBR may fly direct.");
        }

        // Apply Diadem-optimized gathering skill preset
        if (Expedition.Config.DiademAutoApplySkillPreset)
        {
            var skillOk = plugin.Ipc.GatherBuddyLists.ApplyDiademSkillPreset(
                enableCordials: Expedition.Config.DiademEnableCordials,
                enableAetherCannon: Expedition.Config.DiademEnableAetherCannon);
            if (skillOk)
                DalamudApi.Log.Information("[Diadem] Applied Diadem skill preset (rotation solver, skills, cordials).");
            else
                DalamudApi.Log.Warning("[Diadem] Could not fully apply skill preset — GBR will use its defaults.");
        }

        var items = selectedItemIds.Select(id => (id, (uint)999)).ToList();
        var success = plugin.Ipc.GatherBuddyLists.SetGatherList(items);

        if (success)
        {
            plugin.Ipc.GatherBuddy.SetAutoGatherEnabled(true);
            gbrRunning = true;
            DalamudApi.Log.Information($"[Diadem] Injected {items.Count} items into GBR and started auto-gather.");
        }
        else
        {
            DalamudApi.Log.Warning("[Diadem] Failed to inject items into GBR gather list.");
        }
    }

    private static void StopGbr(Expedition plugin)
    {
        plugin.Ipc.GatherBuddy.SetAutoGatherEnabled(false);
        plugin.Ipc.GatherBuddyLists.RemoveExpeditionList();
        gbrRunning = false;
        DalamudApi.Log.Information("[Diadem] Stopped GBR auto-gather and removed gather list.");
    }
}
