using System.Numerics;
using ImGuiNET;

namespace Expedition.UI;

/// <summary>
/// Settings UI drawn as a tab within the main window.
/// </summary>
public static class SettingsTab
{
    private static readonly string[] SolverOptions = {
        "(Default)", "Standard", "Raphael", "Expert", "Progress Only"
    };
    private static int selectedSolverIndex;
    private static bool initialized;

    public static void Draw(Configuration config)
    {
        if (!initialized)
        {
            selectedSolverIndex = Array.IndexOf(SolverOptions, config.PreferredSolver);
            if (selectedSolverIndex < 0) selectedSolverIndex = 0;
            initialized = true;
        }

        ImGui.Text("Expedition Settings");
        ImGui.Separator();

        // --- General ---
        if (ImGui.CollapsingHeader("General", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Indent();

            var autoRepair = config.AutoRepairEnabled;
            if (ImGui.Checkbox("Enable auto-repair check", ref autoRepair))
            {
                config.AutoRepairEnabled = autoRepair;
                config.Save();
            }
            ImGui.SetItemTooltip("Warn when gear durability is below threshold before starting.");

            if (config.AutoRepairEnabled)
            {
                var threshold = config.RepairThresholdPercent;
                ImGui.SetNextItemWidth(150);
                if (ImGui.SliderInt("Repair threshold %", ref threshold, 0, 99))
                {
                    config.RepairThresholdPercent = threshold;
                    config.Save();
                }
            }

            var extractMateria = config.AutoExtractMateriaEnabled;
            if (ImGui.Checkbox("Auto-extract materia when spiritbond full", ref extractMateria))
            {
                config.AutoExtractMateriaEnabled = extractMateria;
                config.Save();
            }

            ImGui.Unindent();
        }

        // --- Gathering ---
        if (ImGui.CollapsingHeader("Gathering", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Indent();

            var collectGather = config.UseCollectableGathering;
            if (ImGui.Checkbox("Enable collectable gathering mode", ref collectGather))
            {
                config.UseCollectableGathering = collectGather;
                config.Save();
            }
            ImGui.SetItemTooltip("Use GBR's collectable gathering mode when ingredients are collectables.");

            var retryLimit = config.GatherRetryLimit;
            ImGui.SetNextItemWidth(150);
            if (ImGui.InputInt("Gather retry limit", ref retryLimit))
            {
                config.GatherRetryLimit = Math.Clamp(retryLimit, 0, 10);
                config.Save();
            }
            ImGui.SetItemTooltip("Max retries if GBR AutoGather stops unexpectedly for an item.");

            var gatherBuffer = config.GatherQuantityBuffer;
            ImGui.SetNextItemWidth(150);
            if (ImGui.InputInt("Gather quantity buffer", ref gatherBuffer))
            {
                config.GatherQuantityBuffer = Math.Max(0, gatherBuffer);
                config.Save();
            }
            ImGui.SetItemTooltip("Gather this many extra of each material as a safety margin.");

            ImGui.Unindent();
        }

        // --- Crafting ---
        if (ImGui.CollapsingHeader("Crafting", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Indent();

            var collectCraft = config.UseCollectableCrafting;
            if (ImGui.Checkbox("Enable collectable crafting mode", ref collectCraft))
            {
                config.UseCollectableCrafting = collectCraft;
                config.Save();
            }
            ImGui.SetItemTooltip("Support crafting collectable items through Artisan.");

            ImGui.SetNextItemWidth(200);
            if (ImGui.Combo("Preferred Artisan solver", ref selectedSolverIndex, SolverOptions, SolverOptions.Length))
            {
                config.PreferredSolver = selectedSolverIndex == 0 ? string.Empty : SolverOptions[selectedSolverIndex];
                config.Save();
            }
            ImGui.SetItemTooltip("Override Artisan's solver for all recipes in this workflow. Leave as Default to use Artisan's per-recipe config.");

            var subFirst = config.CraftSubRecipesFirst;
            if (ImGui.Checkbox("Craft sub-recipes before final item", ref subFirst))
            {
                config.CraftSubRecipesFirst = subFirst;
                config.Save();
            }

            var craftBuffer = config.CraftQuantityBuffer;
            ImGui.SetNextItemWidth(150);
            if (ImGui.InputInt("Craft quantity buffer", ref craftBuffer))
            {
                config.CraftQuantityBuffer = Math.Max(0, craftBuffer);
                config.Save();
            }
            ImGui.SetItemTooltip("Craft this many extra of each sub-recipe as a safety margin.");

            ImGui.Unindent();
        }

        // --- Workflow ---
        if (ImGui.CollapsingHeader("Workflow"))
        {
            ImGui.Indent();

            var pollInterval = config.PollingIntervalMs;
            ImGui.SetNextItemWidth(150);
            if (ImGui.InputInt("Polling interval (ms)", ref pollInterval))
            {
                config.PollingIntervalMs = Math.Clamp(pollInterval, 250, 10000);
                config.Save();
            }

            var timeout = config.IpcTimeoutMs;
            ImGui.SetNextItemWidth(150);
            if (ImGui.InputInt("IPC timeout (ms)", ref timeout))
            {
                config.IpcTimeoutMs = Math.Clamp(timeout, 10000, 600000);
                config.Save();
            }

            var pauseOnError = config.PauseOnError;
            if (ImGui.Checkbox("Pause workflow on error", ref pauseOnError))
            {
                config.PauseOnError = pauseOnError;
                config.Save();
            }

            var notify = config.NotifyOnCompletion;
            if (ImGui.Checkbox("Notify on completion", ref notify))
            {
                config.NotifyOnCompletion = notify;
                config.Save();
            }

            ImGui.Unindent();
        }

        // --- UI ---
        if (ImGui.CollapsingHeader("UI"))
        {
            ImGui.Indent();

            var overlay = config.ShowOverlay;
            if (ImGui.Checkbox("Show status overlay while running", ref overlay))
            {
                config.ShowOverlay = overlay;
                config.Save();
            }

            var detailed = config.ShowDetailedStatus;
            if (ImGui.Checkbox("Show detailed status in overlay", ref detailed))
            {
                config.ShowDetailedStatus = detailed;
                config.Save();
            }

            ImGui.Unindent();
        }
    }
}
