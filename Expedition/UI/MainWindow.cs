using System.Numerics;
using ImGuiNET;

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

    public bool IsOpen;

    public MainWindow(Expedition plugin)
    {
        this.plugin = plugin;
    }

    public void Toggle() => IsOpen = !IsOpen;

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

        DrawMenuBar();

        if (showSettings)
        {
            SettingsTab.Draw(Expedition.Config);
            ImGui.End();
            return;
        }

        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(6, 4));
        if (ImGui.BeginTabBar("ExpeditionTabs"))
        {
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
    // Menu Bar
    // ──────────────────────────────────────────────

    private void DrawMenuBar()
    {
        if (!ImGui.BeginMenuBar()) return;

        var (gbr, artisan) = plugin.Ipc.GetAvailability();

        // GBR status
        Theme.StatusDot(gbr ? Theme.Success : Theme.Error, gbr ? "GBR" : "GBR");
        ImGui.SameLine(0, Theme.PadLarge);

        // Artisan status
        Theme.StatusDot(artisan ? Theme.Success : Theme.Error, artisan ? "Artisan" : "Artisan");

        if (!gbr || !artisan)
        {
            ImGui.SameLine(0, Theme.PadLarge);
            if (ImGui.SmallButton("Refresh"))
                plugin.Ipc.RefreshAvailability();
        }

        // Eorzean time on the right side
        if (Expedition.Config.ShowEorzeanTime)
        {
            var timeText = EorzeanTime.FormatCurrentTime();
            var timeWidth = ImGui.CalcTextSize(timeText).X;
            ImGui.SameLine(ImGui.GetWindowWidth() - timeWidth - 24);
            ImGui.TextColored(Theme.TimedNode, timeText);
        }

        ImGui.EndMenuBar();
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
        ImGui.BeginChild("SearchResults", new Vector2(avail.X * 0.38f, contentHeight), ImGuiChildFlags.Border);
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

                    // Build clean display label
                    var className = RecipeResolverService.GetCraftTypeName(recipe.CraftTypeId);
                    var label = $"{recipe.ItemName}##recipe{recipe.RecipeId}";

                    if (ImGui.Selectable(label, isSelected))
                    {
                        selectedRecipe = recipe;
                        PreviewResolve();
                    }

                    // Draw metadata on same line after the selectable
                    ImGui.SameLine(ImGui.GetContentRegionAvail().X - 80);
                    ImGui.TextColored(Theme.TextMuted, $"{className} {recipe.RequiredLevel}");

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
        ImGui.BeginChild("RecipeDetails", new Vector2(0, contentHeight), ImGuiChildFlags.Border);
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

        // Title
        ImGui.Spacing();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Theme.Pad);
        ImGui.PushFont(ImGui.GetFont()); // Same font but we'll use color for emphasis
        ImGui.TextColored(Theme.Gold, recipe.ItemName);
        ImGui.PopFont();

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
        Theme.KeyValue("Class:", RecipeResolverService.GetCraftTypeName(recipe.CraftTypeId), Theme.Accent);
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

            // Sub-recipes
            if (previewResolution.CraftOrder.Count > 1)
            {
                ImGui.Spacing();
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Theme.Pad);
                ImGui.TextColored(Theme.Gold, "Sub-Recipes (crafting order)");
                ImGui.Spacing();

                foreach (var step in previewResolution.CraftOrder.Take(previewResolution.CraftOrder.Count - 1))
                {
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Theme.PadLarge);
                    ImGui.TextColored(Theme.TextPrimary, $"x{step.Quantity}");
                    ImGui.SameLine();
                    ImGui.Text(step.Recipe.ItemName);
                    ImGui.SameLine();
                    ImGui.TextColored(Theme.TextMuted,
                        $"({RecipeResolverService.GetCraftTypeName(step.Recipe.CraftTypeId)})");
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
        var canStart = selectedRecipe != null && engine.CurrentState == WorkflowState.Idle;

        if (!canStart) ImGui.BeginDisabled();
        if (Theme.PrimaryButton("Start Workflow", new Vector2(140, 32)))
        {
            if (selectedRecipe != null)
                engine.Start(selectedRecipe, craftQuantity);
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
            previewResolution = plugin.RecipeResolver.Resolve(selectedRecipe, craftQuantity);
            plugin.InventoryManager.UpdateResolvedRecipe(previewResolution);
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
        ImGui.BeginChild("LogScroll", Vector2.Zero, ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar);
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
