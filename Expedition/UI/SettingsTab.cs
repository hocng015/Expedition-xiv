using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Expedition.UI;

/// <summary>
/// Settings UI drawn as a tab within the main window.
/// Organized into logical sections with descriptions.
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

        ImGui.Spacing();
        Theme.SectionHeader("Settings", Theme.Accent);
        ImGui.Spacing();
        ImGui.Spacing();

        ImGui.BeginChild("SettingsScroll", Vector2.Zero, false);

        DrawGeneralSection(config);
        DrawGatheringSection(config);
        DrawGpSection(config);
        DrawCraftingSection(config);
        DrawBuffSection(config);
        DrawDurabilitySection(config);
        DrawPrerequisiteSection(config);
        DrawWorkflowSection(config);
        DrawUiSection(config);

        ImGui.EndChild();
    }

    // ──────────────────────────────────────────────
    // Section Drawing
    // ──────────────────────────────────────────────

    private static void DrawGeneralSection(Configuration config)
    {
        if (!ImGui.CollapsingHeader("General", ImGuiTreeNodeFlags.DefaultOpen)) return;

        BeginSection();
        ImGui.TextColored(Theme.TextMuted, "Basic automation preferences.");
        ImGui.Spacing();

        var autoRepair = config.AutoRepairEnabled;
        if (ImGui.Checkbox("Enable auto-repair check", ref autoRepair))
        {
            config.AutoRepairEnabled = autoRepair;
            config.Save();
        }
        Theme.HelpMarker("Warn when gear durability is below threshold before starting a workflow.");

        if (config.AutoRepairEnabled)
        {
            ImGui.Indent(20);
            var threshold = config.RepairThresholdPercent;
            ImGui.SetNextItemWidth(150);
            if (ImGui.SliderInt("Repair threshold %", ref threshold, 0, 99))
            {
                config.RepairThresholdPercent = threshold;
                config.Save();
            }
            ImGui.Unindent(20);
        }

        var extractMateria = config.AutoExtractMateriaEnabled;
        if (ImGui.Checkbox("Auto-extract materia when spiritbond full", ref extractMateria))
        {
            config.AutoExtractMateriaEnabled = extractMateria;
            config.Save();
        }
        Theme.HelpMarker("Automatically extract materia from fully spiritbonded gear during workflows.");

        EndSection();
    }

    private static void DrawGatheringSection(Configuration config)
    {
        if (!ImGui.CollapsingHeader("Gathering", ImGuiTreeNodeFlags.DefaultOpen)) return;

        BeginSection();
        ImGui.TextColored(Theme.TextMuted, "Controls how GatherBuddy Reborn gathers materials.");
        ImGui.Spacing();

        var collectGather = config.UseCollectableGathering;
        if (ImGui.Checkbox("Enable collectable gathering mode", ref collectGather))
        {
            config.UseCollectableGathering = collectGather;
            config.Save();
        }
        Theme.HelpMarker("Use GBR's collectable gathering mode when ingredients are collectables.");

        var optimizeRoute = config.OptimizeGatherRoute;
        if (ImGui.Checkbox("Optimize gathering route", ref optimizeRoute))
        {
            config.OptimizeGatherRoute = optimizeRoute;
            config.Save();
        }
        Theme.HelpMarker("Group gathering tasks by zone to minimize teleport costs.");

        var prioritizeTimed = config.PrioritizeTimedNodes;
        if (ImGui.Checkbox("Prioritize timed nodes", ref prioritizeTimed))
        {
            config.PrioritizeTimedNodes = prioritizeTimed;
            config.Save();
        }
        Theme.HelpMarker("Move active timed/unspoiled nodes to the front of the queue.");

        var gatherNormal = config.GatherNormalWhileWaiting;
        if (ImGui.Checkbox("Gather normal nodes while waiting for timed", ref gatherNormal))
        {
            config.GatherNormalWhileWaiting = gatherNormal;
            config.Save();
        }
        Theme.HelpMarker("Fill downtime between timed node windows by gathering regular materials.");

        ImGui.Spacing();

        var retryLimit = config.GatherRetryLimit;
        ImGui.SetNextItemWidth(150);
        if (ImGui.InputInt("Gather retry limit", ref retryLimit))
        {
            config.GatherRetryLimit = Math.Clamp(retryLimit, 0, 10);
            config.Save();
        }
        Theme.HelpMarker("Max retries if GBR AutoGather stops unexpectedly for an item.");

        var gatherBuffer = config.GatherQuantityBuffer;
        ImGui.SetNextItemWidth(150);
        if (ImGui.InputInt("Gather quantity buffer", ref gatherBuffer))
        {
            config.GatherQuantityBuffer = Math.Max(0, gatherBuffer);
            config.Save();
        }
        Theme.HelpMarker("Gather this many extra of each material as a safety margin.");

        EndSection();
    }

    private static void DrawGpSection(Configuration config)
    {
        if (!ImGui.CollapsingHeader("GP Management")) return;

        BeginSection();
        ImGui.TextColored(Theme.TextMuted, "Gatherer's Points (GP) consumption strategy.");
        ImGui.Spacing();

        var useCordials = config.UseCordials;
        if (ImGui.Checkbox("Use cordials for GP recovery", ref useCordials))
        {
            config.UseCordials = useCordials;
            config.Save();
        }
        Theme.HelpMarker("Recommend cordial usage when GP is low during gathering.");

        if (config.UseCordials)
        {
            ImGui.Indent(20);
            var preferHi = config.PreferHiCordials;
            if (ImGui.Checkbox("Prefer Hi-Cordials", ref preferHi))
            {
                config.PreferHiCordials = preferHi;
                config.Save();
            }
            Theme.HelpMarker("Hi-Cordials restore 400 GP vs 300 GP for regular cordials.");
            ImGui.Unindent(20);
        }

        var minGp = config.MinGpBeforeGathering;
        ImGui.SetNextItemWidth(150);
        if (ImGui.InputInt("Min GP before gathering", ref minGp))
        {
            config.MinGpBeforeGathering = Math.Clamp(minGp, 0, 1000);
            config.Save();
        }
        Theme.HelpMarker("Wait for GP to reach this threshold before starting a gathering rotation.");

        EndSection();
    }

    private static void DrawCraftingSection(Configuration config)
    {
        if (!ImGui.CollapsingHeader("Crafting", ImGuiTreeNodeFlags.DefaultOpen)) return;

        BeginSection();
        ImGui.TextColored(Theme.TextMuted, "Controls how Artisan handles crafting.");
        ImGui.Spacing();

        var collectCraft = config.UseCollectableCrafting;
        if (ImGui.Checkbox("Enable collectable crafting mode", ref collectCraft))
        {
            config.UseCollectableCrafting = collectCraft;
            config.Save();
        }
        Theme.HelpMarker("Support crafting collectable items through Artisan.");

        ImGui.SetNextItemWidth(200);
        if (ImGui.Combo("Preferred Artisan solver", ref selectedSolverIndex, SolverOptions, SolverOptions.Length))
        {
            config.PreferredSolver = selectedSolverIndex == 0 ? string.Empty : SolverOptions[selectedSolverIndex];
            config.Save();
        }
        Theme.HelpMarker("Override Artisan's solver for all recipes in this workflow. Default uses Artisan's per-recipe config.");

        var subFirst = config.CraftSubRecipesFirst;
        if (ImGui.Checkbox("Craft sub-recipes before final item", ref subFirst))
        {
            config.CraftSubRecipesFirst = subFirst;
            config.Save();
        }
        Theme.HelpMarker("Process dependency recipes in bottom-up order before the target recipe.");

        var craftBuffer = config.CraftQuantityBuffer;
        ImGui.SetNextItemWidth(150);
        if (ImGui.InputInt("Craft quantity buffer", ref craftBuffer))
        {
            config.CraftQuantityBuffer = Math.Max(0, craftBuffer);
            config.Save();
        }
        Theme.HelpMarker("Craft this many extra of each sub-recipe as a safety margin.");

        EndSection();
    }

    private static void DrawBuffSection(Configuration config)
    {
        if (!ImGui.CollapsingHeader("Food & Buffs")) return;

        BeginSection();
        ImGui.TextColored(Theme.TextMuted, "Monitor food and medicine buff timers.");
        ImGui.Spacing();

        var warnMissing = config.WarnOnMissingFood;
        if (ImGui.Checkbox("Warn when no food buff active", ref warnMissing))
        {
            config.WarnOnMissingFood = warnMissing;
            config.Save();
        }
        Theme.HelpMarker("Show a warning in the log if you start a workflow without a food buff.");

        var warnExpiring = config.WarnOnFoodExpiring;
        if (ImGui.Checkbox("Warn when food buff expiring", ref warnExpiring))
        {
            config.WarnOnFoodExpiring = warnExpiring;
            config.Save();
        }
        Theme.HelpMarker("Alert when food buff is about to expire during a long session.");

        if (config.WarnOnFoodExpiring)
        {
            ImGui.Indent(20);
            var expirySec = config.FoodExpiryWarningSeconds;
            ImGui.SetNextItemWidth(150);
            if (ImGui.InputInt("Warning threshold (sec)", ref expirySec))
            {
                config.FoodExpiryWarningSeconds = Math.Clamp(expirySec, 30, 600);
                config.Save();
            }
            ImGui.Unindent(20);
        }

        EndSection();
    }

    private static void DrawDurabilitySection(Configuration config)
    {
        if (!ImGui.CollapsingHeader("Gear Durability")) return;

        BeginSection();
        ImGui.TextColored(Theme.TextMuted, "Prevent crafting/gathering failure from broken equipment.");
        ImGui.Spacing();

        var checkBefore = config.CheckDurabilityBeforeStart;
        if (ImGui.Checkbox("Check durability before start", ref checkBefore))
        {
            config.CheckDurabilityBeforeStart = checkBefore;
            config.Save();
        }
        Theme.HelpMarker("Run a gear durability check during the validation phase.");

        var monitorDuring = config.MonitorDurabilityDuringRun;
        if (ImGui.Checkbox("Monitor durability during workflow", ref monitorDuring))
        {
            config.MonitorDurabilityDuringRun = monitorDuring;
            config.Save();
        }
        Theme.HelpMarker("Periodically check durability during gathering and crafting. Pauses if broken.");

        var durWarn = config.DurabilityWarningPercent;
        ImGui.SetNextItemWidth(150);
        if (ImGui.SliderInt("Warning threshold %", ref durWarn, 5, 60))
        {
            config.DurabilityWarningPercent = durWarn;
            config.Save();
        }
        Theme.HelpMarker("Show a warning when any equipped gear falls below this durability percentage.");

        EndSection();
    }

    private static void DrawPrerequisiteSection(Configuration config)
    {
        if (!ImGui.CollapsingHeader("Prerequisites")) return;

        BeginSection();
        ImGui.TextColored(Theme.TextMuted, "Validate requirements before starting a workflow.");
        ImGui.Spacing();

        var validate = config.ValidatePrerequisites;
        if (ImGui.Checkbox("Validate prerequisites", ref validate))
        {
            config.ValidatePrerequisites = validate;
            config.Save();
        }
        Theme.HelpMarker("Check for master books, specialist requirements, class levels, etc.");

        if (config.ValidatePrerequisites)
        {
            ImGui.Indent(20);

            var blockCritical = config.BlockOnCriticalWarnings;
            if (ImGui.Checkbox("Pause on critical warnings", ref blockCritical))
            {
                config.BlockOnCriticalWarnings = blockCritical;
                config.Save();
            }
            Theme.HelpMarker("Automatically pause the workflow if critical issues are found.");

            var blockExpert = config.BlockOnExpertRecipes;
            if (ImGui.Checkbox("Pause on expert recipes", ref blockExpert))
            {
                config.BlockOnExpertRecipes = blockExpert;
                config.Save();
            }
            Theme.HelpMarker("Expert recipes have RNG conditions and high failure rates. Pause for confirmation.");

            ImGui.Unindent(20);
        }

        EndSection();
    }

    private static void DrawWorkflowSection(Configuration config)
    {
        if (!ImGui.CollapsingHeader("Workflow")) return;

        BeginSection();
        ImGui.TextColored(Theme.TextMuted, "Fine-tune workflow execution behavior.");
        ImGui.Spacing();

        var pauseOnError = config.PauseOnError;
        if (ImGui.Checkbox("Pause workflow on error", ref pauseOnError))
        {
            config.PauseOnError = pauseOnError;
            config.Save();
        }
        Theme.HelpMarker("Pause instead of stopping when a recoverable error occurs.");

        var notify = config.NotifyOnCompletion;
        if (ImGui.Checkbox("Notify on completion", ref notify))
        {
            config.NotifyOnCompletion = notify;
            config.Save();
        }
        Theme.HelpMarker("Show a chat message and toast notification when the workflow finishes.");

        ImGui.Spacing();
        ImGui.TextColored(Theme.TextSecondary, "Advanced");
        ImGui.Spacing();

        var maxRetry = config.MaxRetryPerTask;
        ImGui.SetNextItemWidth(150);
        if (ImGui.InputInt("Max retries per task", ref maxRetry))
        {
            config.MaxRetryPerTask = Math.Clamp(maxRetry, 0, 10);
            config.Save();
        }

        var retryDelay = config.RetryDelayMs;
        ImGui.SetNextItemWidth(150);
        if (ImGui.InputInt("Retry delay (ms)", ref retryDelay))
        {
            config.RetryDelayMs = Math.Clamp(retryDelay, 1000, 30000);
            config.Save();
        }

        var pollInterval = config.PollingIntervalMs;
        ImGui.SetNextItemWidth(150);
        if (ImGui.InputInt("Polling interval (ms)", ref pollInterval))
        {
            config.PollingIntervalMs = Math.Clamp(pollInterval, 250, 10000);
            config.Save();
        }
        Theme.HelpMarker("How often to poll Artisan/GBR status. Lower = more responsive but higher CPU.");

        var timeout = config.IpcTimeoutMs;
        ImGui.SetNextItemWidth(150);
        if (ImGui.InputInt("IPC timeout (ms)", ref timeout))
        {
            config.IpcTimeoutMs = Math.Clamp(timeout, 10000, 600000);
            config.Save();
        }

        EndSection();
    }

    private static void DrawUiSection(Configuration config)
    {
        if (!ImGui.CollapsingHeader("UI")) return;

        BeginSection();
        ImGui.TextColored(Theme.TextMuted, "Customize the plugin's visual elements.");
        ImGui.Spacing();

        var overlay = config.ShowOverlay;
        if (ImGui.Checkbox("Show status overlay while running", ref overlay))
        {
            config.ShowOverlay = overlay;
            config.Save();
        }
        Theme.HelpMarker("Display a compact floating overlay with workflow progress.");

        var detailed = config.ShowDetailedStatus;
        if (ImGui.Checkbox("Show detailed status in overlay", ref detailed))
        {
            config.ShowDetailedStatus = detailed;
            config.Save();
        }
        Theme.HelpMarker("Include durability and food buff status in the overlay.");

        var eorzeanTime = config.ShowEorzeanTime;
        if (ImGui.Checkbox("Show Eorzean time", ref eorzeanTime))
        {
            config.ShowEorzeanTime = eorzeanTime;
            config.Save();
        }
        Theme.HelpMarker("Display the current Eorzean time in the menu bar and overlay.");

        EndSection();
    }

    // ──────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────

    private static void BeginSection()
    {
        ImGui.Indent(12);
        ImGui.Spacing();
    }

    private static void EndSection()
    {
        ImGui.Unindent(12);
        ImGui.Spacing();
        ImGui.Spacing();
    }
}
