using System.Numerics;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;

using Expedition.Activation;
using Expedition.Crafting;
using Expedition.Gathering;
using Expedition.RecipeResolver;
using Expedition.Scheduling;
using Expedition.Workflow;

namespace Expedition.UI;

/// <summary>
/// Primary plugin window. Contains recipe search, workflow control,
/// material breakdown, and settings tabs.
/// </summary>
public sealed class MainWindow
{
    private readonly Expedition plugin;
    private string searchQuery = string.Empty;
    private List<RecipeNode> searchResults = new();
    private RecipeNode? selectedRecipe;
    private ResolvedRecipe? previewResolution;
    private int craftQuantity = 1;
    private bool showSettings;
    private string logFilter = string.Empty;

    // Activation prompt state
    private string activationKeyInput = string.Empty;
    private string activationError = string.Empty;
    private string activationSuccess = string.Empty;

    // Browse tab state
    private bool browseIsDol; // false = DOH crafting, true = DOL gathering
    private int? browseSelectedClass; // DOH: CraftType index (0-7), DOL: unused (use browseSelectedGatherType)
    private GatherType? browseSelectedGatherType; // DOL: Miner/Botanist/Fisher
    private int browseMinLevel = 1;
    private int browseMaxLevel = 100;
    private bool browseCollectableOnly;
    private bool browseExpertOnly;
    private bool browseSpecialistOnly;
    private bool browseMasterBookOnly;
    private bool browseHideCrystals = true;
    private List<RecipeNode> browseResults = new();
    private List<GatherableItemInfo> browseGatherResults = new();
    private GatherableItemInfo? selectedGatherItem;
    private bool browseNeedsRefresh = true;
    private bool resetTabToBrowse;

    // CraftType index -> ClassJob RowId (for job icon lookup: icon = 62100 + classJobId)
    private static readonly uint[] CraftTypeToClassJobId = { 8, 9, 10, 11, 12, 13, 14, 15 };
    // DOL ClassJob RowIds: MIN=16, BTN=17, FSH=18
    private static readonly (string Name, uint ClassJobId, GatherType Type)[] GatherClasses =
    {
        ("MIN", 16, GatherType.Miner),
        ("BTN", 17, GatherType.Botanist),
        ("FSH", 18, GatherType.Fisher),
    };

    public bool IsOpen;

    public MainWindow(Expedition plugin)
    {
        this.plugin = plugin;
    }

    public void Toggle()
    {
        IsOpen = !IsOpen;
        if (IsOpen) resetTabToBrowse = true;
    }

    public void OpenSettings()
    {
        IsOpen = true;
        showSettings = true;
    }

    public void Draw()
    {
        if (!IsOpen) return;

        ImGui.SetNextWindowSize(new Vector2(780, 600), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(600, 400), new Vector2(float.MaxValue, float.MaxValue));

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12, 12));
        if (!ImGui.Begin("Expedition###ExpeditionMain", ref IsOpen, ImGuiWindowFlags.MenuBar))
        {
            ImGui.PopStyleVar();
            ImGui.End();
            return;
        }
        ImGui.PopStyleVar();

        // Gate all functionality behind activation
        if (!ActivationService.IsActivated)
        {
            DrawActivationPrompt();
            ImGui.End();
            return;
        }

        DrawMenuBar();
        DrawHeaderBar();

        if (showSettings)
        {
            SettingsTab.Draw(Expedition.Config);
            ImGui.End();
            return;
        }

        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(6, 4));
        if (ImGui.BeginTabBar("ExpeditionTabs"))
        {
            var browseFlags = resetTabToBrowse ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
            if (resetTabToBrowse) resetTabToBrowse = false;
            if (ImGui.BeginTabItem("Browse", browseFlags))
            {
                DrawBrowseTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Recipe"))
            {
                DrawRecipeTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Workflow"))
            {
                DrawWorkflowTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Log"))
            {
                DrawLogTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Settings"))
            {
                SettingsTab.Draw(Expedition.Config);
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
        ImGui.PopStyleVar();

        ImGui.End();
    }

    // ──────────────────────────────────────────────
    // Activation Prompt
    // ──────────────────────────────────────────────

    private void DrawActivationPrompt()
    {
        var avail = ImGui.GetContentRegionAvail();

        // Center the activation content vertically
        var contentHeight = 280f;
        var offsetY = Math.Max(0, (avail.Y - contentHeight) / 2);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + offsetY);

        // Plugin icon centered at top
        var wrap = DalamudApi.TextureProvider
            .GetFromManifestResource(Assembly.GetExecutingAssembly(), "Expedition.Images.icon.png")
            .GetWrapOrDefault();
        if (wrap != null)
        {
            const float iconSize = 96f;
            var iconOffsetX = (avail.X - iconSize) / 2;
            if (iconOffsetX > 0) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + iconOffsetX);
            ImGui.Image(wrap.Handle, new Vector2(iconSize, iconSize));
            ImGui.Spacing();
        }

        // Title
        var title = "Expedition";
        var titleSize = ImGui.CalcTextSize(title);
        ImGui.SetCursorPosX(ImGui.GetWindowContentRegionMin().X + (avail.X - titleSize.X) / 2);
        ImGui.TextColored(Theme.Gold, title);

        var subtitle = "Activation Required";
        var subtitleSize = ImGui.CalcTextSize(subtitle);
        ImGui.SetCursorPosX(ImGui.GetWindowContentRegionMin().X + (avail.X - subtitleSize.X) / 2);
        ImGui.TextColored(Theme.TextSecondary, subtitle);

        ImGui.Spacing();
        ImGui.Spacing();

        // Centered input area
        var inputWidth = Math.Min(420f, avail.X - 40);
        var inputOffsetX = (avail.X - inputWidth) / 2;
        ImGui.SetCursorPosX(ImGui.GetWindowContentRegionMin().X + inputOffsetX);

        ImGui.BeginGroup();
        {
            ImGui.SetNextItemWidth(inputWidth);
            var enterPressed = ImGui.InputTextWithHint(
                "##ActivationKey", "EXP-...", ref activationKeyInput, 256,
                ImGuiInputTextFlags.EnterReturnsTrue);

            ImGui.Spacing();

            // Activate button
            var buttonWidth = 120f;
            var buttonOffsetX = (inputWidth - buttonWidth) / 2;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + buttonOffsetX);

            if (Theme.PrimaryButton("Activate", new Vector2(buttonWidth, 32)) || enterPressed)
            {
                activationError = string.Empty;
                activationSuccess = string.Empty;

                if (string.IsNullOrWhiteSpace(activationKeyInput))
                {
                    activationError = "Please enter an activation key.";
                }
                else
                {
                    var result = ActivationService.Activate(activationKeyInput.Trim(), Expedition.Config);
                    if (result.IsValid)
                    {
                        activationSuccess = "Plugin activated successfully!";
                        activationKeyInput = string.Empty;
                    }
                    else
                    {
                        activationError = result.ErrorMessage;
                    }
                }
            }

            // Error / success messages
            if (!string.IsNullOrEmpty(activationError))
            {
                ImGui.Spacing();
                var errSize = ImGui.CalcTextSize(activationError);
                var errOffsetX = (inputWidth - errSize.X) / 2;
                if (errOffsetX > 0) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + errOffsetX);
                ImGui.TextColored(Theme.Error, activationError);
            }

            if (!string.IsNullOrEmpty(activationSuccess))
            {
                ImGui.Spacing();
                var sucSize = ImGui.CalcTextSize(activationSuccess);
                var sucOffsetX = (inputWidth - sucSize.X) / 2;
                if (sucOffsetX > 0) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + sucOffsetX);
                ImGui.TextColored(Theme.Success, activationSuccess);
            }
        }
        ImGui.EndGroup();

        // Hint text at the bottom
        ImGui.Spacing();
        ImGui.Spacing();
        var hint = "Or use: /expedition activate <key>";
        var hintSize = ImGui.CalcTextSize(hint);
        ImGui.SetCursorPosX(ImGui.GetWindowContentRegionMin().X + (avail.X - hintSize.X) / 2);
        ImGui.TextColored(Theme.TextMuted, hint);
    }

    // ──────────────────────────────────────────────
    // Menu Bar
    // ──────────────────────────────────────────────

    private void DrawMenuBar()
    {
        if (!ImGui.BeginMenuBar()) return;

        var (gbr, artisan) = plugin.Ipc.GetAvailability();

        // GBR status
        Theme.StatusDot(gbr ? Theme.Success : Theme.Error, "GBR");
        ImGui.SameLine(0, Theme.PadLarge);

        // Artisan status
        Theme.StatusDot(artisan ? Theme.Success : Theme.Error, "Artisan");

        if (!gbr || !artisan)
        {
            ImGui.SameLine(0, Theme.PadLarge);
            if (ImGui.SmallButton("Refresh"))
                plugin.Ipc.RefreshAvailability();
        }

        ImGui.EndMenuBar();
    }

    // ──────────────────────────────────────────────
    // Header Bar (ET time, countdown, buffs)
    // ──────────────────────────────────────────────

    private void DrawHeaderBar()
    {
        var drawList = ImGui.GetWindowDrawList();
        var windowWidth = ImGui.GetContentRegionAvail().X;
        var barHeight = 26f;
        var barStart = ImGui.GetCursorScreenPos();
        var cursorStart = ImGui.GetCursorPos();

        // Dark background strip
        drawList.AddRectFilled(
            barStart,
            new Vector2(barStart.X + windowWidth, barStart.Y + barHeight),
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.08f, 0.08f, 0.10f, 1.00f)));

        var textY = barStart.Y + (barHeight - ImGui.GetTextLineHeight()) / 2;

        // ── Left: Eorzean Time box ──
        if (Expedition.Config.ShowEorzeanTime)
        {
            var etText = $"ET {EorzeanTime.CurrentHour:D2}:{EorzeanTime.CurrentMinute:D2}";
            var etSize = ImGui.CalcTextSize(etText);
            var etBoxPad = 6f;
            var etBoxStart = new Vector2(barStart.X + 4, barStart.Y + 3);
            var etBoxEnd = new Vector2(etBoxStart.X + etSize.X + etBoxPad * 2, barStart.Y + barHeight - 3);

            // ET background box (greenish tint like GBR)
            drawList.AddRectFilled(etBoxStart, etBoxEnd,
                ImGui.ColorConvertFloat4ToU32(new Vector4(0.15f, 0.35f, 0.15f, 1.00f)), 3f);
            drawList.AddRect(etBoxStart, etBoxEnd,
                ImGui.ColorConvertFloat4ToU32(new Vector4(0.30f, 0.60f, 0.30f, 0.50f)), 3f);

            drawList.AddText(new Vector2(etBoxStart.X + etBoxPad, textY),
                ImGui.ColorConvertFloat4ToU32(Theme.Success), etText);

            // ── Center: Time to next ET hour ──
            var secondsToNext = EorzeanTime.SecondsUntilEorzeanHour((EorzeanTime.CurrentHour + 1) % 24);
            var minutesToNext = secondsToNext / 60.0;
            var countdownText = $"{minutesToNext:00}:{(secondsToNext % 60):00} Min to next hour.";
            var countdownSize = ImGui.CalcTextSize(countdownText);
            var countdownX = barStart.X + (windowWidth - countdownSize.X) / 2;

            drawList.AddText(new Vector2(countdownX, textY),
                ImGui.ColorConvertFloat4ToU32(Theme.TextSecondary), countdownText);
        }

        // ── Right: Food buff status ──
        var player = DalamudApi.ObjectTable.LocalPlayer;
        if (player != null)
        {
            var buffTracker = plugin.WorkflowEngine.BuffTracker;
            var foodRemaining = buffTracker.GetFoodBuffRemainingSeconds();

            if (foodRemaining > 0)
            {
                var foodMin = foodRemaining / 60f;
                var foodText = $"{foodMin:F1} Min.";
                var foodSize = ImGui.CalcTextSize(foodText);

                var foodBoxPad = 6f;
                var foodBoxEnd = new Vector2(barStart.X + windowWidth - 4, barStart.Y + barHeight - 3);
                var foodBoxStart = new Vector2(foodBoxEnd.X - foodSize.X - foodBoxPad * 2, barStart.Y + 3);

                // Color: teal/cyan for active food
                var foodColor = foodRemaining < 120
                    ? new Vector4(0.40f, 0.35f, 0.10f, 1.00f)  // Amber when expiring
                    : new Vector4(0.10f, 0.30f, 0.35f, 1.00f);  // Teal normally

                var foodBorderColor = foodRemaining < 120
                    ? new Vector4(0.70f, 0.60f, 0.20f, 0.50f)
                    : new Vector4(0.20f, 0.60f, 0.70f, 0.50f);

                drawList.AddRectFilled(foodBoxStart, foodBoxEnd,
                    ImGui.ColorConvertFloat4ToU32(foodColor), 3f);
                drawList.AddRect(foodBoxStart, foodBoxEnd,
                    ImGui.ColorConvertFloat4ToU32(foodBorderColor), 3f);

                var foodTextColor = foodRemaining < 120 ? Theme.Warning : Theme.Collectable;
                drawList.AddText(new Vector2(foodBoxStart.X + foodBoxPad, textY),
                    ImGui.ColorConvertFloat4ToU32(foodTextColor), foodText);

                // Food dot indicator before the box
                var dotRadius = 4f;
                var dotCenter = new Vector2(foodBoxStart.X - dotRadius - 6, barStart.Y + barHeight / 2);
                drawList.AddCircleFilled(dotCenter, dotRadius,
                    ImGui.ColorConvertFloat4ToU32(foodRemaining < 120 ? Theme.Warning : Theme.Success));
            }
            else
            {
                // No food buff - show muted indicator
                var noFoodText = "No Food";
                var noFoodSize = ImGui.CalcTextSize(noFoodText);
                drawList.AddText(
                    new Vector2(barStart.X + windowWidth - noFoodSize.X - 8, textY),
                    ImGui.ColorConvertFloat4ToU32(Theme.TextMuted), noFoodText);
            }
        }

        // Advance cursor past the header bar
        ImGui.SetCursorPos(new Vector2(cursorStart.X, cursorStart.Y + barHeight + 4));
    }

    // ──────────────────────────────────────────────
    // Icon Helpers
    // ──────────────────────────────────────────────

    private static void DrawGameIcon(uint iconId, Vector2 size)
    {
        if (iconId == 0) return;
        var wrap = DalamudApi.TextureProvider
            .GetFromGameIcon(new GameIconLookup(iconId))
            .GetWrapOrDefault();
        if (wrap != null)
            ImGui.Image(wrap.Handle, size);
        else
            ImGui.Dummy(size);
    }

    private static void DrawJobIcon(int craftTypeId, Vector2 size)
    {
        if (craftTypeId < 0 || craftTypeId >= CraftTypeToClassJobId.Length) return;
        DrawGameIcon(62100 + CraftTypeToClassJobId[craftTypeId], size);
    }

    // ──────────────────────────────────────────────
    // Browse Tab
    // ──────────────────────────────────────────────

    private void DrawBrowseTab()
    {
        ImGui.Spacing();

        var avail = ImGui.GetContentRegionAvail();
        var bottomBarHeight = 48f;
        var contentHeight = avail.Y - bottomBarHeight;

        // Left panel: Filters
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.SectionBg);
        ImGui.BeginChild("BrowseFilters", new Vector2(220, contentHeight), true);
        ImGui.PopStyleColor();
        DrawBrowseFilters();
        ImGui.EndChild();

        ImGui.SameLine();

        // Middle panel: Results list
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.SectionBg);
        ImGui.BeginChild("BrowseResults", new Vector2(avail.X * 0.35f, contentHeight), true);
        ImGui.PopStyleColor();
        DrawBrowseResults();
        ImGui.EndChild();

        ImGui.SameLine();

        // Right panel: Recipe details
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.SectionBg);
        ImGui.BeginChild("BrowseDetails", new Vector2(0, contentHeight), true);
        ImGui.PopStyleColor();
        {
            if (selectedRecipe != null)
                DrawRecipeDetails();
            else if (selectedGatherItem != null)
                DrawGatherItemDetails();
            else
            {
                var center = ImGui.GetContentRegionAvail();
                ImGui.SetCursorPos(new Vector2(Theme.PadLarge, center.Y / 2 - 20));
                ImGui.TextColored(Theme.TextMuted, "Select an item to view details");
                ImGui.SetCursorPosX(Theme.PadLarge);
                ImGui.TextColored(Theme.TextMuted, "and start a workflow.");
            }
        }
        ImGui.EndChild();

        // Bottom action bar
        ImGui.Spacing();
        DrawRecipeActionBar();
    }

    private void DrawBrowseFilters()
    {
        // Shared icon layout
        var columns = 4;
        var buttonPad = Theme.PadSmall;
        var framePad = ImGui.GetStyle().FramePadding;
        var regionWidth = ImGui.GetContentRegionAvail().X;
        var btnSide = (regionWidth - (columns - 1) * buttonPad) / columns;
        var iconSide = btnSide - framePad.X * 2;
        if (iconSide < 16) iconSide = 16;
        var iconSize = new Vector2(iconSide, iconSide);
        var selectedColor = new Vector4(0.25f, 0.50f, 0.85f, 1.00f);

        // ── DOH Section ──
        Theme.SectionHeader("Crafting");
        ImGui.Spacing();

        var classNames = new[] { "CRP", "BSM", "ARM", "GSM", "LTW", "WVR", "ALC", "CUL" };

        // "All" button inline as first item, then DOH class icons
        var isAllDoh = !browseIsDol && !browseSelectedClass.HasValue;
        if (isAllDoh)
            ImGui.PushStyleColor(ImGuiCol.Button, selectedColor);
        ImGui.PushID("dohAll");
        if (ImGui.Button("All", new Vector2(btnSide, btnSide)))
        {
            browseIsDol = false;
            browseSelectedClass = null;
            browseSelectedGatherType = null;
            browseNeedsRefresh = true;
        }
        ImGui.PopID();
        if (isAllDoh) ImGui.PopStyleColor();
        ImGui.SameLine(0, buttonPad);

        // DOH class icon buttons continuing from the "All" button
        for (var i = 0; i < classNames.Length; i++)
        {
            var isSelected = !browseIsDol && browseSelectedClass == i;
            var classJobId = CraftTypeToClassJobId[i];
            var jobIconId = 62100 + classJobId;

            if (isSelected)
                ImGui.PushStyleColor(ImGuiCol.Button, selectedColor);

            var wrap = DalamudApi.TextureProvider
                .GetFromGameIcon(new GameIconLookup(jobIconId))
                .GetWrapOrDefault();

            var clicked = false;
            ImGui.PushID($"doh{i}");
            if (wrap != null)
                clicked = ImGui.ImageButton(wrap.Handle, iconSize);
            else
                clicked = ImGui.Button(classNames[i], new Vector2(iconSize.X + 8, iconSize.Y + 8));
            ImGui.PopID();

            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.Text(classNames[i]);
                ImGui.EndTooltip();
            }

            if (clicked)
            {
                browseIsDol = false;
                browseSelectedClass = i;
                browseSelectedGatherType = null;
                browseNeedsRefresh = true;
            }

            if (isSelected) ImGui.PopStyleColor();

            // +1 because "All" button is slot 0
            if ((i + 2) % columns != 0 && i < classNames.Length - 1)
                ImGui.SameLine(0, buttonPad);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── DOL Section ──
        Theme.SectionHeader("Gathering");
        ImGui.Spacing();

        // "All" button inline as first item, then DOL class icons
        var isAllDol = browseIsDol && !browseSelectedGatherType.HasValue;
        if (isAllDol)
            ImGui.PushStyleColor(ImGuiCol.Button, selectedColor);
        ImGui.PushID("dolAll");
        if (ImGui.Button("All", new Vector2(btnSide, btnSide)))
        {
            browseIsDol = true;
            browseSelectedClass = null;
            browseSelectedGatherType = null;
            browseNeedsRefresh = true;
        }
        ImGui.PopID();
        if (isAllDol) ImGui.PopStyleColor();
        ImGui.SameLine(0, buttonPad);

        // DOL class icon buttons continuing from the "All" button
        for (var i = 0; i < GatherClasses.Length; i++)
        {
            var gc = GatherClasses[i];
            var isSelected = browseIsDol && browseSelectedGatherType == gc.Type;
            var jobIconId = 62100 + gc.ClassJobId;

            if (isSelected)
                ImGui.PushStyleColor(ImGuiCol.Button, selectedColor);

            var wrap = DalamudApi.TextureProvider
                .GetFromGameIcon(new GameIconLookup(jobIconId))
                .GetWrapOrDefault();

            var clicked = false;
            ImGui.PushID($"dol{i}");
            if (wrap != null)
                clicked = ImGui.ImageButton(wrap.Handle, iconSize);
            else
                clicked = ImGui.Button(gc.Name, new Vector2(iconSize.X + 8, iconSize.Y + 8));
            ImGui.PopID();

            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.Text(gc.Name);
                ImGui.EndTooltip();
            }

            if (clicked)
            {
                browseIsDol = true;
                browseSelectedClass = null;
                browseSelectedGatherType = gc.Type;
                browseNeedsRefresh = true;
            }

            if (isSelected) ImGui.PopStyleColor();

            if (i < GatherClasses.Length - 1)
                ImGui.SameLine(0, buttonPad);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Level range
        Theme.SectionHeader("Level Range");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderInt("##BrowseMinLvl", ref browseMinLevel, 1, 100, "Min: %d"))
        {
            browseMinLevel = Math.Clamp(browseMinLevel, 1, browseMaxLevel);
            browseNeedsRefresh = true;
        }

        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderInt("##BrowseMaxLvl", ref browseMaxLevel, 1, 100, "Max: %d"))
        {
            browseMaxLevel = Math.Clamp(browseMaxLevel, browseMinLevel, 100);
            browseNeedsRefresh = true;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Toggle filters (context-sensitive)
        Theme.SectionHeader("Filters");
        ImGui.Spacing();

        if (ImGui.Checkbox("Collectable", ref browseCollectableOnly))
            browseNeedsRefresh = true;

        if (!browseIsDol)
        {
            // DOH-only filters
            if (ImGui.Checkbox("Expert", ref browseExpertOnly))
                browseNeedsRefresh = true;

            if (ImGui.Checkbox("Specialist", ref browseSpecialistOnly))
                browseNeedsRefresh = true;

            if (ImGui.Checkbox("Master Book", ref browseMasterBookOnly))
                browseNeedsRefresh = true;
        }
        else
        {
            // DOL-only filters
            if (ImGui.Checkbox("Hide Crystals", ref browseHideCrystals))
                browseNeedsRefresh = true;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var count = browseIsDol ? browseGatherResults.Count : browseResults.Count;
        var label = browseIsDol ? "items" : "recipes";
        ImGui.TextColored(Theme.TextSecondary, $"{count} {label}");

        // Plugin icon at the bottom of the filter panel
        DrawPluginIcon();
    }

    private static void DrawPluginIcon()
    {
        var wrap = DalamudApi.TextureProvider
            .GetFromManifestResource(Assembly.GetExecutingAssembly(), "Expedition.Images.icon.png")
            .GetWrapOrDefault();
        if (wrap == null) return;

        const float iconSize = 180f;
        var availY = ImGui.GetContentRegionAvail().Y;
        if (availY < iconSize + 8) return;

        // Push the icon to the bottom of the panel
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + availY - iconSize - 4);

        // Center horizontally within the 220px filter panel
        var panelWidth = ImGui.GetContentRegionAvail().X;
        var offsetX = (panelWidth - iconSize) * 0.5f;
        if (offsetX > 0) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offsetX);

        ImGui.Image(wrap.Handle, new Vector2(iconSize, iconSize));
    }

    private void DrawBrowseResults()
    {
        if (browseNeedsRefresh)
        {
            DoBrowse();
            browseNeedsRefresh = false;
        }

        if (browseIsDol)
            DrawBrowseGatherResults();
        else
            DrawBrowseCraftResults();
    }

    private void DrawBrowseCraftResults()
    {
        if (browseResults.Count == 0)
        {
            ImGui.Spacing();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Theme.Pad);
            ImGui.TextColored(Theme.TextMuted, "No recipes match filters.");
            return;
        }

        ImGui.TextColored(Theme.TextSecondary, $"  {browseResults.Count} recipes");
        ImGui.Separator();

        var iconSm = new Vector2(28, 28);
        var jobIconSm = new Vector2(20, 20);

        foreach (var recipe in browseResults)
        {
            var isSelected = selectedRecipe?.RecipeId == recipe.RecipeId;

            DrawGameIcon(recipe.IconId, iconSm);
            ImGui.SameLine(0, Theme.PadSmall);

            var cursorY = ImGui.GetCursorPosY();
            ImGui.SetCursorPosY(cursorY + (iconSm.Y - ImGui.GetTextLineHeight()) / 2);

            var label = $"{recipe.ItemName}##browse{recipe.RecipeId}";
            if (ImGui.Selectable(label, isSelected, ImGuiSelectableFlags.None, new Vector2(ImGui.GetContentRegionAvail().X - 50, 0)))
            {
                selectedRecipe = recipe;
                selectedGatherItem = null;
                PreviewResolve();
            }

            ImGui.SameLine(ImGui.GetContentRegionAvail().X - 40);
            DrawJobIcon(recipe.CraftTypeId, jobIconSm);
            ImGui.SameLine(0, 2);
            ImGui.TextColored(Theme.TextMuted, $"{recipe.RequiredLevel}");

            if (ImGui.IsItemHovered() && (recipe.IsCollectable || recipe.IsExpert || recipe.RequiresSpecialist))
            {
                ImGui.BeginTooltip();
                if (recipe.IsCollectable) ImGui.TextColored(Theme.Collectable, "Collectable");
                if (recipe.IsExpert) ImGui.TextColored(Theme.Expert, "Expert Recipe");
                if (recipe.RequiresSpecialist) ImGui.TextColored(Theme.Specialist, "Specialist Required");
                ImGui.EndTooltip();
            }
        }
    }

    private void DrawBrowseGatherResults()
    {
        if (browseGatherResults.Count == 0)
        {
            ImGui.Spacing();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Theme.Pad);
            ImGui.TextColored(Theme.TextMuted, "No gatherable items match filters.");
            return;
        }

        ImGui.TextColored(Theme.TextSecondary, $"  {browseGatherResults.Count} items");
        ImGui.Separator();

        var iconSm = new Vector2(28, 28);
        var jobIconSm = new Vector2(20, 20);

        foreach (var item in browseGatherResults)
        {
            var isSelected = selectedGatherItem?.ItemId == item.ItemId;

            DrawGameIcon(item.IconId, iconSm);
            ImGui.SameLine(0, Theme.PadSmall);

            var cursorY = ImGui.GetCursorPosY();
            ImGui.SetCursorPosY(cursorY + (iconSm.Y - ImGui.GetTextLineHeight()) / 2);

            var label = $"{item.ItemName}##gather{item.ItemId}";
            if (ImGui.Selectable(label, isSelected, ImGuiSelectableFlags.None, new Vector2(ImGui.GetContentRegionAvail().X - 50, 0)))
            {
                selectedGatherItem = item;
                selectedRecipe = null;
            }

            // Gather class icon + level on the right
            ImGui.SameLine(ImGui.GetContentRegionAvail().X - 40);
            var gatherClassJobId = item.GatherClass switch
            {
                GatherType.Miner => 16u,
                GatherType.Botanist => 17u,
                GatherType.Fisher => 18u,
                _ => 0u,
            };
            if (gatherClassJobId > 0)
                DrawGameIcon(62100 + gatherClassJobId, jobIconSm);
            ImGui.SameLine(0, 2);
            ImGui.TextColored(Theme.TextMuted, $"{item.GatherLevel}");

            if (ImGui.IsItemHovered() && (item.IsCollectable || item.IsAlsoCraftable))
            {
                ImGui.BeginTooltip();
                if (item.IsCollectable) ImGui.TextColored(Theme.Collectable, "Collectable");
                if (item.IsAlsoCraftable) ImGui.TextColored(Theme.Gold, "Also Craftable");
                ImGui.EndTooltip();
            }
        }
    }

    private void DoBrowse()
    {
        if (browseIsDol)
        {
            browseGatherResults = plugin.RecipeResolver.BrowseGatherItems(
                gatherClass: browseSelectedGatherType,
                minLevel: browseMinLevel,
                maxLevel: browseMaxLevel,
                collectableOnly: browseCollectableOnly,
                hideCrystals: browseHideCrystals);
            browseResults.Clear();
        }
        else
        {
            browseResults = plugin.RecipeResolver.BrowseRecipes(
                craftTypeId: browseSelectedClass,
                minLevel: browseMinLevel,
                maxLevel: browseMaxLevel,
                collectableOnly: browseCollectableOnly,
                expertOnly: browseExpertOnly,
                specialistOnly: browseSpecialistOnly,
                masterBookOnly: browseMasterBookOnly);
            browseGatherResults.Clear();
        }
    }

    private void DrawGatherItemDetails()
    {
        var item = selectedGatherItem!;

        // Title with item icon
        ImGui.Spacing();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Theme.Pad);
        DrawGameIcon(item.IconId, new Vector2(40, 40));
        ImGui.SameLine(0, Theme.Pad);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (40 - ImGui.GetTextLineHeight()) / 2);
        ImGui.TextColored(Theme.Gold, item.ItemName);

        // Tags row
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Theme.Pad);
        if (item.IsCollectable)
            Theme.InlineBadge("Collectable", Theme.Collectable);
        if (item.IsCrystal)
            Theme.InlineBadge("Crystal", Theme.TextSecondary);
        if (item.IsAlsoCraftable)
            Theme.InlineBadge("Also Craftable", Theme.Gold);
        if (item.IsCollectable || item.IsCrystal || item.IsAlsoCraftable)
            ImGui.NewLine();

        ImGui.Spacing();

        // Stats
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Theme.Pad);
        ImGui.TextColored(Theme.TextSecondary, "Class:");
        ImGui.SameLine();
        var gatherClassJobId = item.GatherClass switch
        {
            GatherType.Miner => 16u,
            GatherType.Botanist => 17u,
            GatherType.Fisher => 18u,
            _ => 0u,
        };
        if (gatherClassJobId > 0)
            DrawGameIcon(62100 + gatherClassJobId, new Vector2(20, 20));
        ImGui.SameLine(0, 2);
        ImGui.TextColored(Theme.Accent, RecipeResolverService.GetGatherTypeName(item.GatherClass));
        ImGui.SameLine(0, Theme.PadLarge);
        Theme.KeyValue("Level:", item.GatherLevel.ToString(), Theme.Accent);

        if (item.ItemLevel > 0)
        {
            ImGui.SameLine(0, Theme.PadLarge);
            Theme.KeyValue("Item Level:", item.ItemLevel.ToString(), Theme.Accent);
        }
    }

    // ──────────────────────────────────────────────
    // Recipe Tab
    // ──────────────────────────────────────────────

    private void DrawRecipeTab()
    {
        // Search bar
        ImGui.Spacing();
        ImGui.SetNextItemWidth(-120);
        var hint = "Search recipes...";
        if (ImGui.InputTextWithHint("##RecipeSearch", hint, ref searchQuery, 256, ImGuiInputTextFlags.EnterReturnsTrue))
            DoSearch();

        ImGui.SameLine();
        if (Theme.PrimaryButton("Search", new Vector2(105, 0)))
            DoSearch();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Two-column layout
        var avail = ImGui.GetContentRegionAvail();
        var bottomBarHeight = 48f;
        var contentHeight = avail.Y - bottomBarHeight;

        // Left panel: Search results
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.SectionBg);
        ImGui.BeginChild("SearchResults", new Vector2(avail.X * 0.38f, contentHeight), true);
        ImGui.PopStyleColor();
        {
            if (searchResults.Count == 0)
            {
                ImGui.Spacing();
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Theme.Pad);
                ImGui.TextColored(Theme.TextMuted, searchQuery.Length > 0
                    ? "No results found."
                    : "Type a recipe name to search.");
            }
            else
            {
                ImGui.TextColored(Theme.TextSecondary, $"  {searchResults.Count} results");
                ImGui.Separator();

                foreach (var recipe in searchResults)
                {
                    var isSelected = selectedRecipe?.RecipeId == recipe.RecipeId;

                    // Item icon + name
                    DrawGameIcon(recipe.IconId, new Vector2(28, 28));
                    ImGui.SameLine(0, Theme.PadSmall);
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (28 - ImGui.GetTextLineHeight()) / 2);

                    var label = $"{recipe.ItemName}##recipe{recipe.RecipeId}";
                    if (ImGui.Selectable(label, isSelected, ImGuiSelectableFlags.None, new Vector2(ImGui.GetContentRegionAvail().X - 50, 0)))
                    {
                        selectedRecipe = recipe;
                        PreviewResolve();
                    }

                    // Job icon + level on right
                    ImGui.SameLine(ImGui.GetContentRegionAvail().X - 40);
                    DrawJobIcon(recipe.CraftTypeId, new Vector2(20, 20));
                    ImGui.SameLine(0, 2);
                    ImGui.TextColored(Theme.TextMuted, $"{recipe.RequiredLevel}");

                    // Badges on hover tooltip
                    if (ImGui.IsItemHovered() && (recipe.IsCollectable || recipe.IsExpert || recipe.RequiresSpecialist))
                    {
                        ImGui.BeginTooltip();
                        if (recipe.IsCollectable) ImGui.TextColored(Theme.Collectable, "Collectable");
                        if (recipe.IsExpert) ImGui.TextColored(Theme.Expert, "Expert Recipe");
                        if (recipe.RequiresSpecialist) ImGui.TextColored(Theme.Specialist, "Specialist Required");
                        ImGui.EndTooltip();
                    }
                }
            }
        }
        ImGui.EndChild();

        ImGui.SameLine();

        // Right panel: Recipe details
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.SectionBg);
        ImGui.BeginChild("RecipeDetails", new Vector2(0, contentHeight), true);
        ImGui.PopStyleColor();
        {
            if (selectedRecipe != null)
                DrawRecipeDetails();
            else
            {
                var center = ImGui.GetContentRegionAvail();
                ImGui.SetCursorPos(new Vector2(Theme.PadLarge, center.Y / 2 - 20));
                ImGui.TextColored(Theme.TextMuted, "Select a recipe from the search results");
                ImGui.SetCursorPosX(Theme.PadLarge);
                ImGui.TextColored(Theme.TextMuted, "to view details and start a workflow.");
            }
        }
        ImGui.EndChild();

        // Bottom action bar
        ImGui.Spacing();
        DrawRecipeActionBar();
    }

    private void DrawRecipeDetails()
    {
        var recipe = selectedRecipe!;

        // Title with item icon
        ImGui.Spacing();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Theme.Pad);
        DrawGameIcon(recipe.IconId, new Vector2(40, 40));
        ImGui.SameLine(0, Theme.Pad);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (40 - ImGui.GetTextLineHeight()) / 2);
        ImGui.TextColored(Theme.Gold, recipe.ItemName);

        // Tags row
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Theme.Pad);
        if (recipe.IsCollectable)
        {
            Theme.InlineBadge("Collectable", Theme.Collectable);
        }
        if (recipe.IsExpert)
        {
            Theme.InlineBadge("Expert", Theme.Expert);
        }
        if (recipe.RequiresSpecialist)
        {
            Theme.InlineBadge("Specialist", Theme.Specialist);
        }
        if (recipe.RequiresMasterBook)
        {
            Theme.InlineBadge("Master Book", Theme.MasterBook);
        }
        if (recipe.IsCollectable || recipe.IsExpert || recipe.RequiresSpecialist || recipe.RequiresMasterBook)
            ImGui.NewLine();

        ImGui.Spacing();

        // Stats grid
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Theme.Pad);
        ImGui.TextColored(Theme.TextSecondary, "Class:");
        ImGui.SameLine();
        DrawJobIcon(recipe.CraftTypeId, new Vector2(20, 20));
        ImGui.SameLine(0, 2);
        ImGui.TextColored(Theme.Accent, RecipeResolverService.GetCraftTypeName(recipe.CraftTypeId));
        ImGui.SameLine(0, Theme.PadLarge);
        Theme.KeyValue("Level:", recipe.RequiredLevel.ToString(), Theme.Accent);
        ImGui.SameLine(0, Theme.PadLarge);
        Theme.KeyValue("Yield:", recipe.YieldPerCraft.ToString(), Theme.Accent);

        if (recipe.RecipeDurability > 0)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Theme.Pad);
            Theme.KeyValue("Durability:", recipe.RecipeDurability.ToString());
            ImGui.SameLine(0, Theme.PadLarge);
            Theme.KeyValue("Craftsmanship:", recipe.SuggestedCraftsmanship.ToString());
            ImGui.SameLine(0, Theme.PadLarge);
            Theme.KeyValue("Control:", recipe.SuggestedControl.ToString());
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Direct ingredients
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Theme.Pad);
        Theme.SectionHeader("Ingredients");
        ImGui.Spacing();

        foreach (var ing in recipe.Ingredients)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Theme.PadLarge);
            DrawGameIcon(ing.IconId, new Vector2(28, 28));
            ImGui.SameLine(0, Theme.PadSmall);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (28 - ImGui.GetTextLineHeight()) / 2);
            ImGui.TextColored(Theme.TextPrimary, $"x{ing.QuantityNeeded}");
            ImGui.SameLine();
            ImGui.Text(ing.ItemName);

            // Small inline tags
            ImGui.SameLine();
            if (ing.IsCraftable) { ImGui.TextColored(Theme.Gold, "[Craft]"); ImGui.SameLine(); }
            if (ing.IsGatherable) { ImGui.TextColored(Theme.Success, "[Gather]"); ImGui.SameLine(); }
            if (ing.IsCollectable) { ImGui.TextColored(Theme.Collectable, "[Coll]"); ImGui.SameLine(); }
            ImGui.NewLine();
        }

        // Full material breakdown (resolved)
        if (previewResolution != null)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Theme.Pad);
            Theme.SectionHeader($"Full Breakdown (x{craftQuantity})");
            ImGui.Spacing();

            // Gatherable materials
            if (previewResolution.GatherList.Count > 0)
            {
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Theme.Pad);
                ImGui.TextColored(Theme.Success, "Gatherable Materials");
                ImGui.Spacing();

                foreach (var mat in previewResolution.GatherList)
                {
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Theme.PadLarge);
                    DrawGameIcon(mat.IconId, new Vector2(28, 28));
                    ImGui.SameLine(0, Theme.PadSmall);
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (28 - ImGui.GetTextLineHeight()) / 2);

                    // Quantity with owned indicator
                    var remaining = mat.QuantityRemaining;
                    var quantityColor = remaining == 0 ? Theme.Success : Theme.TextPrimary;
                    ImGui.TextColored(quantityColor, $"x{mat.QuantityNeeded}");
                    ImGui.SameLine();
                    ImGui.Text(mat.ItemName);

                    if (mat.QuantityOwned > 0)
                    {
                        ImGui.SameLine();
                        ImGui.TextColored(remaining == 0 ? Theme.SuccessDim : Theme.TextSecondary,
                            $"(have {mat.QuantityOwned})");
                    }

                    // Flags
                    if (mat.IsTimedNode)
                    {
                        ImGui.SameLine();
                        ImGui.TextColored(Theme.TimedNode, "[Timed]");
                    }
                    if (mat.IsCollectable)
                    {
                        ImGui.SameLine();
                        ImGui.TextColored(Theme.Collectable, "[Coll]");
                    }
                }
            }

            // Sub-recipes (filter out zero-quantity steps already covered by inventory)
            var subRecipes = previewResolution.CraftOrder
                .Take(previewResolution.CraftOrder.Count - 1)
                .Where(s => s.Quantity > 0)
                .ToList();

            if (subRecipes.Count > 0)
            {
                ImGui.Spacing();
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Theme.Pad);
                ImGui.TextColored(Theme.Gold, "Sub-Recipes (crafting order)");
                ImGui.Spacing();

                foreach (var step in subRecipes)
                {
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Theme.PadLarge);
                    DrawGameIcon(step.Recipe.IconId, new Vector2(28, 28));
                    ImGui.SameLine(0, Theme.PadSmall);
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (28 - ImGui.GetTextLineHeight()) / 2);
                    ImGui.TextColored(Theme.TextPrimary, $"x{step.Quantity}");
                    ImGui.SameLine();
                    ImGui.Text(step.Recipe.ItemName);
                    ImGui.SameLine();
                    DrawJobIcon(step.Recipe.CraftTypeId, new Vector2(18, 18));

                    // Show how many the player already owns for this intermediate
                    var saddlebag = Expedition.Config.IncludeSaddlebagInScans;
                    var owned = plugin.InventoryManager.GetItemCount(step.Recipe.ItemId, includeSaddlebag: saddlebag);
                    if (owned > 0)
                    {
                        ImGui.SameLine();
                        ImGui.TextColored(Theme.TextSecondary, $"(have {owned})");
                    }
                }
            }

            // Other materials
            if (previewResolution.OtherMaterials.Count > 0)
            {
                ImGui.Spacing();
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Theme.Pad);
                ImGui.TextColored(Theme.Warning, "Other Sources (vendor/drops)");
                ImGui.Spacing();

                foreach (var mat in previewResolution.OtherMaterials)
                {
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Theme.PadLarge);
                    DrawGameIcon(mat.IconId, new Vector2(28, 28));
                    ImGui.SameLine(0, Theme.PadSmall);
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (28 - ImGui.GetTextLineHeight()) / 2);
                    ImGui.TextColored(Theme.TextPrimary, $"x{mat.QuantityNeeded}");
                    ImGui.SameLine();
                    ImGui.Text(mat.ItemName);
                    if (mat.QuantityOwned > 0)
                    {
                        ImGui.SameLine();
                        ImGui.TextColored(Theme.TextSecondary, $"(have {mat.QuantityOwned})");
                    }
                }
            }
        }
    }

    private void DrawRecipeActionBar()
    {
        var engine = plugin.WorkflowEngine;
        var hasSelection = selectedRecipe != null || selectedGatherItem != null;
        var canStart = hasSelection && (engine.CurrentState == WorkflowState.Idle
            || engine.CurrentState == WorkflowState.Completed
            || engine.CurrentState == WorkflowState.Error);

        if (!canStart) ImGui.BeginDisabled();
        var buttonLabel = selectedGatherItem != null ? "Start Gathering" : "Start Workflow";
        if (Theme.PrimaryButton(buttonLabel, new Vector2(160, 32)))
        {
            if (selectedRecipe != null)
                engine.Start(selectedRecipe, craftQuantity);
            else if (selectedGatherItem != null)
                engine.StartGather(selectedGatherItem, craftQuantity);
        }
        if (!canStart) ImGui.EndDisabled();

        ImGui.SameLine(0, Theme.Pad);
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("##Quantity", ref craftQuantity, 1, 10))
        {
            craftQuantity = Math.Clamp(craftQuantity, 1, 9999);
            if (selectedRecipe != null) PreviewResolve();
        }
        ImGui.SameLine();
        ImGui.TextColored(Theme.TextSecondary, "qty");

        if (engine.CurrentState != WorkflowState.Idle && engine.CurrentState != WorkflowState.Completed)
        {
            ImGui.SameLine(0, Theme.PadLarge);
            if (Theme.DangerButton("Stop", new Vector2(80, 32)))
                engine.Cancel();
        }
    }

    private void DoSearch()
    {
        if (string.IsNullOrWhiteSpace(searchQuery)) return;
        searchResults = plugin.RecipeResolver.SearchRecipes(searchQuery, 100);
        selectedRecipe = null;
        previewResolution = null;
    }

    private void PreviewResolve()
    {
        if (selectedRecipe == null) return;

        try
        {
            // Build inventory lookup so preview deducts owned intermediates
            var saddlebag = Expedition.Config.IncludeSaddlebagInScans;
            Func<uint, int> inventoryLookup = itemId
                => plugin.InventoryManager.GetItemCount(itemId, includeSaddlebag: saddlebag);

            previewResolution = plugin.RecipeResolver.Resolve(selectedRecipe, craftQuantity, inventoryLookup);
            plugin.InventoryManager.UpdateResolvedRecipe(previewResolution, saddlebag);
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Error(ex, "Failed to preview resolve recipe");
            previewResolution = null;
        }
    }

    // ──────────────────────────────────────────────
    // Workflow Tab
    // ──────────────────────────────────────────────

    private void DrawWorkflowTab()
    {
        var engine = plugin.WorkflowEngine;

        ImGui.Spacing();

        if (engine.CurrentState == WorkflowState.Idle && engine.CurrentRecipe == null)
        {
            DrawWorkflowIdleState();
            return;
        }

        // Phase pipeline
        DrawPhasePipeline(engine);

        ImGui.Spacing();

        // Status card
        DrawWorkflowStatusCard(engine);

        ImGui.Spacing();

        // Health indicators
        DrawHealthIndicators(engine);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Progress section
        DrawProgressSection(engine);

        // Validation warnings
        DrawValidationSection(engine);

        // Controls
        ImGui.Spacing();
        DrawWorkflowControls(engine);
    }

    private void DrawWorkflowIdleState()
    {
        ImGui.Spacing();
        ImGui.Spacing();
        var avail = ImGui.GetContentRegionAvail();
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + avail.Y / 3);

        var text = "No active workflow";
        var textSize = ImGui.CalcTextSize(text);
        ImGui.SetCursorPosX((avail.X - textSize.X) / 2);
        ImGui.TextColored(Theme.TextMuted, text);

        var subText = "Search for a recipe in the Recipe tab to get started.";
        var subSize = ImGui.CalcTextSize(subText);
        ImGui.SetCursorPosX((avail.X - subSize.X) / 2);
        ImGui.TextColored(Theme.TextDisabled, subText);
    }

    private void DrawPhasePipeline(WorkflowEngine engine)
    {
        var phase = engine.CurrentPhase;
        var state = engine.CurrentState;
        var isComplete = state == WorkflowState.Completed;
        var isError = state == WorkflowState.Error;
        var isPaused = state == WorkflowState.Paused;

        // Phase steps
        var phases = new[]
        {
            ("Resolve", WorkflowPhase.Resolving),
            ("Validate", WorkflowPhase.Validating),
            ("Inventory", WorkflowPhase.CheckingInventory),
            ("Gather", WorkflowPhase.Gathering),
            ("Craft", WorkflowPhase.Crafting),
        };

        var phaseIndex = Array.FindIndex(phases, p => p.Item2 == phase);
        for (var i = 0; i < phases.Length; i++)
        {
            var (label, p) = phases[i];
            var isCurrent = p == phase && !isComplete;
            var isDone = isComplete || (phaseIndex >= 0 && i < phaseIndex);

            Theme.PipelineStep(label, isCurrent, isDone, i == 0);
            ImGui.SameLine(0, 0);
        }

        // Terminal state indicator
        if (isComplete)
        {
            ImGui.SameLine(0, Theme.Pad);
            ImGui.TextColored(Theme.Success, "Complete");
        }
        else if (isError)
        {
            ImGui.SameLine(0, Theme.Pad);
            ImGui.TextColored(Theme.Error, "Error");
        }
        else if (isPaused)
        {
            ImGui.SameLine(0, Theme.Pad);
            ImGui.TextColored(Theme.PhasePaused, "Paused");
        }

        ImGui.NewLine();
    }

    private void DrawWorkflowStatusCard(WorkflowEngine engine)
    {
        Theme.BeginCard("StatusCard", 0);
        {
            ImGui.Spacing();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Theme.Pad);

            // Target item
            if (engine.CurrentRecipe != null)
            {
                Theme.KeyValue("Target:", $"{engine.CurrentRecipe.ItemName} x{engine.TargetQuantity}", Theme.Gold);
            }

            // Elapsed time
            if (engine.StartTime.HasValue && engine.CurrentState != WorkflowState.Idle)
            {
                var elapsed = DateTime.Now - engine.StartTime.Value;
                ImGui.SameLine(0, Theme.PadLarge);
                Theme.KeyValue("Elapsed:", $"{elapsed.Hours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}", Theme.TextSecondary);
            }

            // Status message
            if (!string.IsNullOrEmpty(engine.StatusMessage))
            {
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Theme.Pad);
                ImGui.TextWrapped(engine.StatusMessage);
            }

            ImGui.Spacing();
        }
        Theme.EndCard();
    }

    private void DrawHealthIndicators(WorkflowEngine engine)
    {
        var showDurability = engine.LastDurabilityReport != null;
        var showFood = engine.LastBuffDiagnostic != null;
        if (!showDurability && !showFood) return;

        // Inline health row
        if (showDurability)
        {
            var dur = engine.LastDurabilityReport!;
            var durColor = dur.LowestPercent switch
            {
                0 => Theme.Critical,
                < 20 => Theme.Error,
                < 50 => Theme.Warning,
                _ => Theme.Success,
            };

            Theme.StatusDot(durColor, $"Durability: {dur.LowestPercent}%");
            if (showFood) ImGui.SameLine(0, Theme.PadLarge);
        }

        if (showFood)
        {
            var buff = engine.LastBuffDiagnostic!;
            var foodColor = buff.HasFood
                ? (buff.FoodExpiringSoon ? Theme.Warning : Theme.Success)
                : Theme.TextMuted;

            Theme.StatusDot(foodColor, buff.FoodStatusText);
        }
    }

    private void DrawProgressSection(WorkflowEngine engine)
    {
        // Gathering progress
        if (engine.CurrentPhase == WorkflowPhase.Gathering || engine.CurrentState == WorkflowState.Gathering)
        {
            Theme.SectionHeader("Gathering", Theme.Success);
            ImGui.Spacing();
            DrawGatheringProgress();
            ImGui.Spacing();
        }

        // Crafting progress
        if (engine.CurrentPhase == WorkflowPhase.Crafting || engine.CurrentState == WorkflowState.Crafting)
        {
            Theme.SectionHeader("Crafting", Theme.Gold);
            ImGui.Spacing();
            DrawCraftingProgress();
            ImGui.Spacing();
        }
    }

    private void DrawGatheringProgress()
    {
        var orch = plugin.GatheringOrchestrator;
        if (orch.Tasks.Count == 0) return;

        // Overall progress
        var completedCount = orch.Tasks.Count(t => t.Status == GatheringTaskStatus.Completed);
        var totalCount = orch.Tasks.Count;
        var overallFraction = totalCount > 0 ? (float)completedCount / totalCount : 0;
        Theme.ProgressBar(overallFraction, Theme.AccentDim,
            $"{completedCount}/{totalCount} items", 6);
        ImGui.Spacing();

        // Individual tasks
        foreach (var task in orch.Tasks)
        {
            DrawGatheringTaskRow(task);
        }
    }

    private void DrawGatheringTaskRow(GatheringTask task)
    {
        var (icon, color) = task.Status switch
        {
            GatheringTaskStatus.Completed => ("  ", Theme.Success),
            GatheringTaskStatus.InProgress => ("  ", Theme.Accent),
            GatheringTaskStatus.WaitingForTimedNode => ("  ", Theme.TimedNode),
            GatheringTaskStatus.Failed => ("  ", Theme.Error),
            GatheringTaskStatus.Skipped => ("  ", Theme.TextMuted),
            _ => ("  ", Theme.TextSecondary),
        };

        // Status dot + name
        Theme.StatusDot(color, "");
        ImGui.SameLine(0, 0);
        ImGui.Text(task.ItemName);

        // Progress count on the right
        var progressText = $"{task.QuantityGathered}/{task.QuantityNeeded}";
        var progressWidth = ImGui.CalcTextSize(progressText).X;
        ImGui.SameLine(ImGui.GetContentRegionMax().X - progressWidth - Theme.Pad);
        ImGui.TextColored(task.IsComplete ? Theme.SuccessDim : Theme.TextSecondary, progressText);

        // Progress bar for active task
        if (task.Status == GatheringTaskStatus.InProgress && task.QuantityNeeded > 0)
        {
            var fraction = (float)task.QuantityGathered / task.QuantityNeeded;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 20);
            ImGui.PushItemWidth(-Theme.Pad);
            Theme.ProgressBar(fraction, Theme.Accent, null, 4);
            ImGui.PopItemWidth();
        }

        // Error message
        if (task.Status == GatheringTaskStatus.Failed && task.ErrorMessage != null)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 20);
            ImGui.TextColored(Theme.ErrorDim, task.ErrorMessage);
        }
    }

    private void DrawCraftingProgress()
    {
        var orch = plugin.CraftingOrchestrator;
        if (orch.Tasks.Count == 0) return;

        // Overall progress
        var completedCount = orch.Tasks.Count(t => t.Status == CraftingTaskStatus.Completed);
        var totalCount = orch.Tasks.Count;
        var overallFraction = totalCount > 0 ? (float)completedCount / totalCount : 0;
        Theme.ProgressBar(overallFraction, Theme.GoldDim,
            $"{completedCount}/{totalCount} recipes", 6);
        ImGui.Spacing();

        foreach (var task in orch.Tasks)
        {
            DrawCraftingTaskRow(task);
        }
    }

    private void DrawCraftingTaskRow(CraftingTask task)
    {
        var color = task.Status switch
        {
            CraftingTaskStatus.Completed => Theme.Success,
            CraftingTaskStatus.InProgress or CraftingTaskStatus.WaitingForArtisan => Theme.Accent,
            CraftingTaskStatus.Failed => Theme.Error,
            CraftingTaskStatus.Skipped => Theme.TextMuted,
            _ => Theme.TextSecondary,
        };

        Theme.StatusDot(color, "");
        ImGui.SameLine(0, 0);
        ImGui.Text(task.ItemName);

        ImGui.SameLine();
        ImGui.TextColored(Theme.TextMuted,
            $"x{task.Quantity} ({RecipeResolverService.GetCraftTypeName(task.CraftTypeId)})");

        // Error message
        if (task.Status == CraftingTaskStatus.Failed && task.ErrorMessage != null)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 20);
            ImGui.TextColored(Theme.ErrorDim, task.ErrorMessage);
        }
    }

    private void DrawValidationSection(WorkflowEngine engine)
    {
        if (engine.LastValidation == null || !engine.LastValidation.HasWarnings) return;

        ImGui.Separator();
        ImGui.Spacing();

        var warningCount = engine.LastValidation.Warnings.Count;
        var hasErrors = engine.LastValidation.HasErrors;
        var headerColor = hasErrors ? Theme.Error : Theme.Warning;

        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(headerColor.X, headerColor.Y, headerColor.Z, 0.15f));
        if (ImGui.CollapsingHeader($"Warnings ({warningCount})###ValidationWarnings"))
        {
            ImGui.Spacing();
            foreach (var w in engine.LastValidation.Warnings)
            {
                var severityColor = Theme.SeverityColor(w.Severity);

                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Theme.Pad);
                Theme.StatusDot(severityColor, "");
                ImGui.SameLine(0, 0);
                ImGui.TextColored(Theme.TextSecondary, $"[{w.Category}]");
                ImGui.SameLine();
                ImGui.TextWrapped(w.Message);
            }
            ImGui.Spacing();
        }
        ImGui.PopStyleColor();
    }

    private void DrawWorkflowControls(WorkflowEngine engine)
    {
        var state = engine.CurrentState;

        if (state == WorkflowState.Paused)
        {
            if (Theme.PrimaryButton("Resume", new Vector2(120, 32)))
                engine.Resume();
            ImGui.SameLine(0, Theme.Pad);
            if (Theme.DangerButton("Cancel", new Vector2(120, 32)))
                engine.Cancel();
        }
        else if (state == WorkflowState.Error || state == WorkflowState.Completed)
        {
            if (Theme.SecondaryButton("Reset", new Vector2(120, 32)))
                engine.Cancel();
        }
        else if (state != WorkflowState.Idle)
        {
            if (Theme.DangerButton("Cancel Workflow", new Vector2(160, 32)))
                engine.Cancel();
        }
    }

    // ──────────────────────────────────────────────
    // Log Tab
    // ──────────────────────────────────────────────

    private void DrawLogTab()
    {
        var engine = plugin.WorkflowEngine;

        // Controls row
        ImGui.Spacing();
        ImGui.SetNextItemWidth(200);
        ImGui.InputTextWithHint("##LogFilter", "Filter...", ref logFilter, 128);
        ImGui.SameLine();
        if (Theme.SecondaryButton("Clear", new Vector2(60, 0)))
            engine.Log.Clear();
        ImGui.SameLine();
        ImGui.TextColored(Theme.TextMuted, $"{engine.Log.Count} entries");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Log entries
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.SectionBg);
        ImGui.BeginChild("LogScroll", Vector2.Zero, false, ImGuiWindowFlags.HorizontalScrollbar);
        ImGui.PopStyleColor();

        var hasFilter = !string.IsNullOrWhiteSpace(logFilter);

        foreach (var entry in engine.Log)
        {
            if (hasFilter && !entry.Contains(logFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            Vector4 color;
            if (entry.Contains("[ERROR]") || entry.Contains("[CRITICAL]"))
                color = Theme.Error;
            else if (entry.Contains("[Warning]") || entry.Contains("[!]"))
                color = Theme.Warning;
            else if (entry.Contains("[Health]"))
                color = Theme.TimedNode;
            else if (entry.Contains("[Info]") || entry.Contains("[Buff]"))
                color = Theme.TextSecondary;
            else
                color = Theme.TextPrimary;

            ImGui.TextColored(color, entry);
        }

        // Auto-scroll
        if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 10)
            ImGui.SetScrollHereY(1.0f);

        ImGui.EndChild();
    }
}
