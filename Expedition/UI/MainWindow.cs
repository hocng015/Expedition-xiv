using System.Numerics;
using ImGuiNET;

using Expedition.Crafting;
using Expedition.Gathering;
using Expedition.RecipeResolver;
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
    private int selectedTab;
    private bool showSettings;

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

        ImGui.SetNextWindowSize(new Vector2(720, 560), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin($"Expedition###ExpeditionMain", ref IsOpen, ImGuiWindowFlags.MenuBar))
        {
            ImGui.End();
            return;
        }

        DrawMenuBar();

        if (showSettings)
        {
            SettingsTab.Draw(Expedition.Config);
            ImGui.End();
            return;
        }

        // Tab bar
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

        ImGui.End();
    }

    private void DrawMenuBar()
    {
        if (ImGui.BeginMenuBar())
        {
            // Plugin dependency status
            var (gbr, artisan) = plugin.Ipc.GetAvailability();
            var gbrColor = gbr ? new Vector4(0.3f, 1f, 0.3f, 1f) : new Vector4(1f, 0.3f, 0.3f, 1f);
            var artColor = artisan ? new Vector4(0.3f, 1f, 0.3f, 1f) : new Vector4(1f, 0.3f, 0.3f, 1f);

            ImGui.TextColored(gbrColor, gbr ? "GBR: OK" : "GBR: N/A");
            ImGui.SameLine();
            ImGui.TextColored(artColor, artisan ? "Artisan: OK" : "Artisan: N/A");

            if (!gbr || !artisan)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Refresh"))
                    plugin.Ipc.RefreshAvailability();
            }

            ImGui.EndMenuBar();
        }
    }

    // --- Recipe Tab ---

    private void DrawRecipeTab()
    {
        ImGui.Text("Search for a recipe:");
        ImGui.SetNextItemWidth(-100);
        if (ImGui.InputText("##RecipeSearch", ref searchQuery, 256, ImGuiInputTextFlags.EnterReturnsTrue))
        {
            DoSearch();
        }
        ImGui.SameLine();
        if (ImGui.Button("Search", new Vector2(90, 0)))
        {
            DoSearch();
        }

        ImGui.Separator();

        // Two-column layout: results on left, details on right
        var avail = ImGui.GetContentRegionAvail();

        // Left: Search results
        ImGui.BeginChild("SearchResults", new Vector2(avail.X * 0.4f, avail.Y - 40), ImGuiChildFlags.Border);
        {
            foreach (var recipe in searchResults)
            {
                var label = $"{recipe.ItemName} ({RecipeResolverService.GetCraftTypeName(recipe.CraftTypeId)} Lv{recipe.RequiredLevel})";
                if (recipe.IsCollectable) label += " [C]";
                if (recipe.IsExpert) label += " [E]";

                if (ImGui.Selectable(label, selectedRecipe?.RecipeId == recipe.RecipeId))
                {
                    selectedRecipe = recipe;
                    PreviewResolve();
                }
            }
        }
        ImGui.EndChild();

        ImGui.SameLine();

        // Right: Recipe details
        ImGui.BeginChild("RecipeDetails", new Vector2(0, avail.Y - 40), ImGuiChildFlags.Border);
        {
            if (selectedRecipe != null)
            {
                DrawRecipeDetails();
            }
            else
            {
                ImGui.TextWrapped("Select a recipe from the search results to see details and start a workflow.");
            }
        }
        ImGui.EndChild();

        // Bottom: Start button
        ImGui.Separator();
        var canStart = selectedRecipe != null && plugin.WorkflowEngine.CurrentState == WorkflowState.Idle;

        if (!canStart) ImGui.BeginDisabled();
        if (ImGui.Button("Start Workflow", new Vector2(150, 30)))
        {
            if (selectedRecipe != null)
            {
                plugin.WorkflowEngine.Start(selectedRecipe, craftQuantity);
            }
        }
        if (!canStart) ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.SetNextItemWidth(100);
        ImGui.InputInt("Quantity", ref craftQuantity);
        craftQuantity = Math.Max(1, Math.Min(craftQuantity, 9999));

        if (plugin.WorkflowEngine.CurrentState != WorkflowState.Idle)
        {
            ImGui.SameLine();
            if (ImGui.Button("Stop", new Vector2(80, 30)))
            {
                plugin.WorkflowEngine.Cancel();
            }
        }
    }

    private void DrawRecipeDetails()
    {
        var recipe = selectedRecipe!;

        ImGui.TextColored(new Vector4(1f, 0.9f, 0.4f, 1f), recipe.ItemName);

        ImGui.Text($"Recipe ID: {recipe.RecipeId}  |  Yield: {recipe.YieldPerCraft}");
        ImGui.Text($"Class: {RecipeResolverService.GetCraftTypeName(recipe.CraftTypeId)}  |  Level: {recipe.RequiredLevel}");

        if (recipe.IsCollectable) ImGui.TextColored(new Vector4(0.4f, 0.8f, 1f, 1f), "Collectable");
        if (recipe.IsExpert) ImGui.TextColored(new Vector4(1f, 0.5f, 0.5f, 1f), "Expert Recipe (RNG conditions!)");
        if (recipe.RequiresSpecialist) ImGui.TextColored(new Vector4(1f, 0.6f, 0.8f, 1f), "Specialist Required");
        if (recipe.RequiresMasterBook) ImGui.TextColored(new Vector4(1f, 0.8f, 0.4f, 1f), "Master Recipe Book Required");
        if (recipe.RecipeDurability > 0) ImGui.Text($"Durability: {recipe.RecipeDurability}  |  Suggested: {recipe.SuggestedCraftsmanship}C/{recipe.SuggestedControl}Ctrl");

        ImGui.Separator();
        ImGui.Text("Direct Ingredients:");

        foreach (var ing in recipe.Ingredients)
        {
            var flags = new List<string>();
            if (ing.IsCraftable) flags.Add("Craftable");
            if (ing.IsGatherable) flags.Add("Gatherable");
            if (ing.IsCollectable) flags.Add("Collectable");
            var flagStr = flags.Count > 0 ? $" [{string.Join(", ", flags)}]" : "";

            ImGui.BulletText($"{ing.ItemName} x{ing.QuantityNeeded}{flagStr}");
        }

        if (previewResolution != null)
        {
            ImGui.Separator();
            ImGui.Text($"Full Material Breakdown (for x{craftQuantity}):");

            if (previewResolution.GatherList.Count > 0)
            {
                ImGui.TextColored(new Vector4(0.3f, 1f, 0.3f, 1f), "Gatherable:");
                foreach (var mat in previewResolution.GatherList)
                {
                    var owned = $" (have {mat.QuantityOwned})";
                    ImGui.BulletText($"{mat.ItemName} x{mat.QuantityNeeded}{owned}");
                }
            }

            if (previewResolution.CraftOrder.Count > 1) // >1 because the root recipe is always included
            {
                ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), "Sub-recipes (crafting order):");
                foreach (var step in previewResolution.CraftOrder.Take(previewResolution.CraftOrder.Count - 1))
                {
                    ImGui.BulletText($"{step.Recipe.ItemName} x{step.Quantity} ({RecipeResolverService.GetCraftTypeName(step.Recipe.CraftTypeId)})");
                }
            }

            if (previewResolution.OtherMaterials.Count > 0)
            {
                ImGui.TextColored(new Vector4(1f, 0.5f, 0.5f, 1f), "Other (vendor/drops/etc.):");
                foreach (var mat in previewResolution.OtherMaterials)
                {
                    ImGui.BulletText($"{mat.ItemName} x{mat.QuantityNeeded}");
                }
            }
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

    // --- Workflow Tab ---

    private void DrawWorkflowTab()
    {
        var engine = plugin.WorkflowEngine;

        // Eorzean time
        if (Expedition.Config.ShowEorzeanTime)
        {
            ImGui.TextColored(new Vector4(0.6f, 0.8f, 1f, 1f), Scheduling.EorzeanTime.FormatCurrentTime());
            ImGui.SameLine();
        }

        // Status header
        var stateColor = engine.CurrentState switch
        {
            WorkflowState.Idle => new Vector4(0.5f, 0.5f, 0.5f, 1f),
            WorkflowState.Completed => new Vector4(0.3f, 1f, 0.3f, 1f),
            WorkflowState.Error => new Vector4(1f, 0.3f, 0.3f, 1f),
            WorkflowState.Paused => new Vector4(1f, 0.7f, 0.2f, 1f),
            _ => new Vector4(1f, 0.9f, 0.4f, 1f),
        };

        ImGui.TextColored(stateColor, $"State: {engine.CurrentState}");
        if (engine.CurrentPhase != WorkflowPhase.None)
        {
            ImGui.SameLine();
            ImGui.Text($"  Phase: {engine.CurrentPhase}");
        }

        if (engine.CurrentRecipe != null)
            ImGui.Text($"Target: {engine.CurrentRecipe.ItemName} x{engine.TargetQuantity}");

        if (!string.IsNullOrEmpty(engine.StatusMessage))
            ImGui.TextWrapped(engine.StatusMessage);

        // Health indicators
        if (engine.LastDurabilityReport != null)
        {
            var durColor = engine.LastDurabilityReport.LowestPercent switch
            {
                0 => new Vector4(1f, 0f, 0f, 1f),
                < 20 => new Vector4(1f, 0.3f, 0.3f, 1f),
                < 50 => new Vector4(1f, 0.9f, 0.4f, 1f),
                _ => new Vector4(0.3f, 1f, 0.3f, 1f),
            };
            ImGui.TextColored(durColor, engine.LastDurabilityReport.StatusText);
        }

        if (engine.LastBuffDiagnostic != null)
            ImGui.Text(engine.LastBuffDiagnostic.FoodStatusText);

        ImGui.Separator();

        // Gathering progress
        if (engine.CurrentPhase == WorkflowPhase.Gathering || engine.CurrentState == WorkflowState.Gathering)
        {
            ImGui.TextColored(new Vector4(0.3f, 1f, 0.3f, 1f), "Gathering Progress:");
            DrawGatheringProgress();
        }

        // Crafting progress
        if (engine.CurrentPhase == WorkflowPhase.Crafting || engine.CurrentState == WorkflowState.Crafting)
        {
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), "Crafting Progress:");
            DrawCraftingProgress();
        }

        ImGui.Separator();

        // Validation warnings
        if (engine.LastValidation != null && engine.LastValidation.HasWarnings)
        {
            if (ImGui.CollapsingHeader("Validation Warnings"))
            {
                foreach (var w in engine.LastValidation.Warnings)
                {
                    var wColor = w.Severity switch
                    {
                        PlayerState.Severity.Critical => new Vector4(1f, 0f, 0f, 1f),
                        PlayerState.Severity.Error => new Vector4(1f, 0.3f, 0.3f, 1f),
                        PlayerState.Severity.Warning => new Vector4(1f, 0.9f, 0.4f, 1f),
                        _ => new Vector4(0.7f, 0.7f, 0.7f, 1f),
                    };
                    ImGui.TextColored(wColor, $"[{w.Category}] {w.Message}");
                }
            }
        }

        // Controls
        var isRunning = engine.CurrentState != WorkflowState.Idle &&
                        engine.CurrentState != WorkflowState.Completed &&
                        engine.CurrentState != WorkflowState.Error &&
                        engine.CurrentState != WorkflowState.Paused;

        if (isRunning)
        {
            if (ImGui.Button("Cancel Workflow", new Vector2(150, 30)))
                engine.Cancel();
        }

        if (engine.CurrentState == WorkflowState.Paused)
        {
            if (ImGui.Button("Resume", new Vector2(100, 30)))
                engine.Resume();
            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(100, 30)))
                engine.Cancel();
        }

        if (engine.CurrentState == WorkflowState.Error)
        {
            ImGui.SameLine();
            if (ImGui.Button("Reset", new Vector2(80, 30)))
                engine.Cancel();
        }
    }

    private void DrawGatheringProgress()
    {
        var orch = plugin.GatheringOrchestrator;
        foreach (var task in orch.Tasks)
        {
            var statusIcon = task.Status switch
            {
                GatheringTaskStatus.Completed => "[Done]",
                GatheringTaskStatus.InProgress => "[>>>]",
                GatheringTaskStatus.Failed => "[FAIL]",
                GatheringTaskStatus.Skipped => "[Skip]",
                _ => "[....]",
            };

            var color = task.Status switch
            {
                GatheringTaskStatus.Completed => new Vector4(0.3f, 1f, 0.3f, 1f),
                GatheringTaskStatus.InProgress => new Vector4(1f, 0.9f, 0.4f, 1f),
                GatheringTaskStatus.Failed => new Vector4(1f, 0.3f, 0.3f, 1f),
                _ => new Vector4(0.7f, 0.7f, 0.7f, 1f),
            };

            ImGui.TextColored(color, $"  {statusIcon} {task.ItemName}: {task.QuantityGathered}/{task.QuantityNeeded}");

            if (task.Status == GatheringTaskStatus.InProgress)
            {
                var progress = task.QuantityNeeded > 0
                    ? (float)task.QuantityGathered / task.QuantityNeeded
                    : 0f;
                ImGui.ProgressBar(progress, new Vector2(-1, 0), $"{task.QuantityGathered}/{task.QuantityNeeded}");
            }
        }
    }

    private void DrawCraftingProgress()
    {
        var orch = plugin.CraftingOrchestrator;
        foreach (var task in orch.Tasks)
        {
            var statusIcon = task.Status switch
            {
                CraftingTaskStatus.Completed => "[Done]",
                CraftingTaskStatus.InProgress => "[>>>]",
                CraftingTaskStatus.WaitingForArtisan => "[Wait]",
                CraftingTaskStatus.Failed => "[FAIL]",
                CraftingTaskStatus.Skipped => "[Skip]",
                _ => "[....]",
            };

            var color = task.Status switch
            {
                CraftingTaskStatus.Completed => new Vector4(0.3f, 1f, 0.3f, 1f),
                CraftingTaskStatus.InProgress or CraftingTaskStatus.WaitingForArtisan => new Vector4(1f, 0.9f, 0.4f, 1f),
                CraftingTaskStatus.Failed => new Vector4(1f, 0.3f, 0.3f, 1f),
                _ => new Vector4(0.7f, 0.7f, 0.7f, 1f),
            };

            var className = RecipeResolverService.GetCraftTypeName(task.CraftTypeId);
            ImGui.TextColored(color, $"  {statusIcon} {task.ItemName} x{task.Quantity} ({className})");
        }
    }

    // --- Log Tab ---

    private void DrawLogTab()
    {
        var engine = plugin.WorkflowEngine;

        if (ImGui.Button("Clear Log"))
            engine.Log.Clear();

        ImGui.Separator();

        ImGui.BeginChild("LogScroll", Vector2.Zero, ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar);
        foreach (var entry in engine.Log)
        {
            if (entry.Contains("[ERROR]"))
                ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), entry);
            else if (entry.Contains("[Warning]") || entry.Contains("[!]"))
                ImGui.TextColored(new Vector4(1f, 0.9f, 0.4f, 1f), entry);
            else
                ImGui.Text(entry);
        }

        // Auto-scroll to bottom
        if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY())
            ImGui.SetScrollHereY(1.0f);

        ImGui.EndChild();
    }
}
