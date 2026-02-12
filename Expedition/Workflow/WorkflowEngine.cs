using Expedition.Crafting;
using Expedition.Gathering;
using Expedition.Inventory;
using Expedition.PlayerState;
using Expedition.RecipeResolver;
using Expedition.Scheduling;

namespace Expedition.Workflow;

/// <summary>
/// The main workflow state machine. Drives the full gather-to-craft pipeline:
///
///   1. Resolve recipe tree (with full dependency analysis)
///   2. Validate prerequisites (master books, class levels, specialist, gear durability)
///   3. Run pre-flight checks (inventory space, food buffs, crystal stock)
///   4. Check inventory for existing materials
///   5. Schedule and optimize gathering route (zone grouping, timed node priority)
///   6. Gather missing materials via GatherBuddy Reborn
///      - Handle timed/unspoiled node windows (wait or fill with normal nodes)
///      - Handle ephemeral nodes for aetherial reduction sources
///      - Monitor GP and recommend cordial usage
///   7. Craft sub-recipes bottom-up via Artisan (auto-class switching)
///   8. Craft final item
///   9. Periodic health checks (durability, food buffs, inventory space)
///
/// State transitions:
///   Idle → Resolving → Validating → CheckingInventory → PreparingGather →
///   Gathering → PreparingCraft → Crafting → Completed
///   Any state → Error (on failure) or Paused (on recoverable issue)
///   Any state → Idle (on cancel)
/// </summary>
public sealed class WorkflowEngine : IDisposable
{
    private readonly RecipeResolverService recipeResolver;
    private readonly InventoryManager inventoryManager;
    private readonly GatheringOrchestrator gatheringOrchestrator;
    private readonly CraftingOrchestrator craftingOrchestrator;
    private readonly Configuration config;

    // New subsystems
    private readonly PrerequisiteValidator prerequisiteValidator = new();
    private readonly DurabilityMonitor durabilityMonitor = new();
    public BuffTracker BuffTracker { get; } = new();
    private readonly GpTracker gpTracker = new();

    // State
    public WorkflowState CurrentState { get; private set; } = WorkflowState.Idle;
    public WorkflowPhase CurrentPhase { get; private set; } = WorkflowPhase.None;
    public string StatusMessage { get; private set; } = string.Empty;
    public RecipeNode? CurrentRecipe { get; private set; }
    public ResolvedRecipe? ResolvedRecipe { get; private set; }
    public int TargetQuantity { get; private set; }
    public DateTime? StartTime { get; private set; }
    public List<string> Log { get; } = new();

    // Validation results (exposed for UI)
    public ValidationResult? LastValidation { get; private set; }
    public DurabilityReport? LastDurabilityReport { get; private set; }
    public BuffDiagnostic? LastBuffDiagnostic { get; private set; }

    // Health check timing
    private DateTime lastHealthCheck = DateTime.MinValue;
    private const double HealthCheckIntervalSeconds = 30.0;

    // One-shot state flags (prevent re-entering one-time states every frame)
    private bool resolveCompleted;
    private bool validationCompleted;
    private bool inventoryCheckCompleted;
    private bool gatherPrepCompleted;
    private bool craftPrepCompleted;

    public event Action<WorkflowState>? OnStateChanged;
    public event Action<string>? OnStatusChanged;
    public event Action? OnCompleted;
    public event Action<string>? OnError;

    public WorkflowEngine(
        RecipeResolverService recipeResolver,
        InventoryManager inventoryManager,
        GatheringOrchestrator gatheringOrchestrator,
        CraftingOrchestrator craftingOrchestrator,
        Configuration config)
    {
        this.recipeResolver = recipeResolver;
        this.inventoryManager = inventoryManager;
        this.gatheringOrchestrator = gatheringOrchestrator;
        this.craftingOrchestrator = craftingOrchestrator;
        this.config = config;
    }

    /// <summary>
    /// Starts a new workflow for the given recipe and quantity.
    /// </summary>
    public void Start(RecipeNode recipe, int quantity)
    {
        if (CurrentState != WorkflowState.Idle)
        {
            DalamudApi.Log.Warning("Cannot start workflow: already running.");
            return;
        }

        CurrentRecipe = recipe;
        TargetQuantity = quantity;
        StartTime = DateTime.Now;
        Log.Clear();
        ResetOneShots();

        AddLog($"Starting workflow: {recipe.ItemName} x{quantity}");
        TransitionTo(WorkflowState.Resolving);
    }

    /// <summary>
    /// Cancels the current workflow.
    /// </summary>
    public void Cancel()
    {
        if (CurrentState == WorkflowState.Idle) return;

        gatheringOrchestrator.Stop();
        craftingOrchestrator.Stop();

        AddLog("Workflow cancelled by user.");
        TransitionTo(WorkflowState.Idle);
    }

    /// <summary>
    /// Resumes from a Paused state (e.g., after user fixes an issue).
    /// </summary>
    public void Resume()
    {
        if (CurrentState != WorkflowState.Paused) return;

        AddLog("Workflow resumed by user.");

        // Re-run from validation to pick up any changes the user made
        validationCompleted = false;
        inventoryCheckCompleted = false;
        TransitionTo(WorkflowState.Validating);
    }

    /// <summary>
    /// Called every frame from Framework.Update.
    /// Drives the state machine forward.
    /// </summary>
    public void Update()
    {
        switch (CurrentState)
        {
            case WorkflowState.Resolving:
                if (!resolveCompleted) ExecuteResolving();
                break;

            case WorkflowState.Validating:
                if (!validationCompleted) ExecuteValidation();
                break;

            case WorkflowState.CheckingInventory:
                if (!inventoryCheckCompleted) ExecuteCheckingInventory();
                break;

            case WorkflowState.PreparingGather:
                if (!gatherPrepCompleted) ExecutePreparingGather();
                break;

            case WorkflowState.Gathering:
                ExecuteGathering();
                break;

            case WorkflowState.PreparingCraft:
                if (!craftPrepCompleted) ExecutePreparingCraft();
                break;

            case WorkflowState.Crafting:
                ExecuteCrafting();
                break;

            case WorkflowState.Completed:
            case WorkflowState.Error:
            case WorkflowState.Idle:
            case WorkflowState.Paused:
                break;
        }

        // Periodic health checks during long-running phases
        if (CurrentState == WorkflowState.Gathering || CurrentState == WorkflowState.Crafting)
        {
            RunPeriodicHealthChecks();
        }
    }

    // --- State Handlers ---

    private void ExecuteResolving()
    {
        CurrentPhase = WorkflowPhase.Resolving;
        SetStatus("Resolving recipe dependency tree...");

        try
        {
            ResolvedRecipe = recipeResolver.Resolve(CurrentRecipe!, TargetQuantity);

            var timedCount = ResolvedRecipe.GatherList.Count(g => g.IsTimedNode);
            var collectableCount = ResolvedRecipe.GatherList.Count(g => g.IsCollectable);
            var aethersandCount = ResolvedRecipe.GatherList.Count(g => g.IsAetherialReductionSource);
            var crystalCount = ResolvedRecipe.GatherList.Count(g => g.IsCrystal);

            AddLog($"Recipe resolved: {ResolvedRecipe.GatherList.Count} gatherable, " +
                   $"{ResolvedRecipe.CraftOrder.Count} craft steps, " +
                   $"{ResolvedRecipe.OtherMaterials.Count} other.");

            if (timedCount > 0)
                AddLog($"  Timed nodes: {timedCount} items require unspoiled/legendary nodes.");
            if (collectableCount > 0)
                AddLog($"  Collectables: {collectableCount} items are collectables (won't stack).");
            if (aethersandCount > 0)
                AddLog($"  Aethersands: {aethersandCount} items require Aetherial Reduction.");
            if (crystalCount > 0)
                AddLog($"  Crystals: {crystalCount} crystal/shard/cluster types needed.");

            if (ResolvedRecipe.OtherMaterials.Count > 0)
            {
                foreach (var mat in ResolvedRecipe.OtherMaterials)
                    AddLog($"  [!] {mat.ItemName} x{mat.QuantityNeeded} — vendor/drop/other source.");
            }

            resolveCompleted = true;
            TransitionTo(WorkflowState.Validating);
        }
        catch (Exception ex)
        {
            HandleError($"Failed to resolve recipe: {ex.Message}");
        }
    }

    private void ExecuteValidation()
    {
        CurrentPhase = WorkflowPhase.Validating;
        SetStatus("Validating prerequisites...");

        try
        {
            // Prerequisite validation
            if (config.ValidatePrerequisites)
            {
                LastValidation = prerequisiteValidator.Validate(ResolvedRecipe!, config);

                foreach (var warning in LastValidation.Warnings)
                {
                    var prefix = warning.Severity switch
                    {
                        Severity.Critical => "[CRITICAL]",
                        Severity.Error => "[ERROR]",
                        Severity.Warning => "[Warning]",
                        _ => "[Info]",
                    };
                    AddLog($"  {prefix} [{warning.Category}] {warning.Message}");
                }

                if (LastValidation.HasCritical && config.BlockOnCriticalWarnings)
                {
                    AddLog("Critical prerequisite issues found. Workflow paused.");
                    SetStatus("Paused: Critical prerequisite issues. Fix and resume.");
                    TransitionTo(WorkflowState.Paused);
                    validationCompleted = true;
                    return;
                }
            }

            // Gear durability
            if (config.CheckDurabilityBeforeStart)
            {
                LastDurabilityReport = durabilityMonitor.GetReport();
                AddLog($"  {LastDurabilityReport.StatusText}");

                if (LastDurabilityReport.LowestPercent < config.DurabilityWarningPercent)
                {
                    var hasDarkMatter = durabilityMonitor.HasDarkMatter();
                    AddLog($"  Dark Matter available: {(hasDarkMatter ? "Yes" : "No — purchase before continuing")}");

                    if (LastDurabilityReport.LowestPercent == 0)
                    {
                        HandleError("Equipment is broken (0% durability). Repair before starting.");
                        validationCompleted = true;
                        return;
                    }
                }
            }

            // Food buffs
            if (config.WarnOnMissingFood)
            {
                LastBuffDiagnostic = BuffTracker.GetDiagnostic();
                foreach (var warning in LastBuffDiagnostic.GetWarnings())
                    AddLog($"  [Buff] {warning}");
            }

            AddLog("Prerequisite validation complete.");
            validationCompleted = true;
            TransitionTo(WorkflowState.CheckingInventory);
        }
        catch (Exception ex)
        {
            HandleError($"Validation failed: {ex.Message}");
        }
    }

    private void ExecuteCheckingInventory()
    {
        CurrentPhase = WorkflowPhase.CheckingInventory;
        SetStatus("Checking inventory...");

        try
        {
            inventoryManager.UpdateResolvedRecipe(ResolvedRecipe!);

            var totalGatherNeeded = ResolvedRecipe!.GatherList.Sum(g => g.QuantityRemaining);
            var totalGatherItems = ResolvedRecipe.GatherList.Count(g => g.QuantityRemaining > 0);

            AddLog($"Inventory checked: {totalGatherItems} items need gathering ({totalGatherNeeded} total units).");

            // Collectable inventory pressure
            var collectableSlots = ResolvedRecipe.GatherList
                .Where(g => g.IsCollectable && g.QuantityRemaining > 0)
                .Sum(g => g.QuantityRemaining);

            var normalSlots = inventoryManager.EstimateInventoryShortfall(ResolvedRecipe);

            if (collectableSlots > 0)
                AddLog($"  [!] Collectables will consume ~{collectableSlots} slots (cannot stack).");
            if (normalSlots > 0)
                AddLog($"  [Warning] Estimated {normalSlots} additional inventory slots needed.");

            // Non-obtainable materials
            var missingOther = ResolvedRecipe.OtherMaterials.Where(m => m.QuantityRemaining > 0).ToList();
            if (missingOther.Count > 0)
            {
                var names = string.Join(", ", missingOther.Select(m => $"{m.ItemName} x{m.QuantityRemaining}"));
                if (config.PauseOnError)
                {
                    AddLog($"[!] Missing non-gatherable materials: {names}");
                    SetStatus($"Paused: Need {names}. Obtain manually and resume.");
                    TransitionTo(WorkflowState.Paused);
                    inventoryCheckCompleted = true;
                    return;
                }
                else
                {
                    AddLog($"  [Warning] Missing non-gatherable materials: {names}. Proceeding anyway.");
                }
            }

            inventoryCheckCompleted = true;

            if (totalGatherNeeded > 0)
                TransitionTo(WorkflowState.PreparingGather);
            else
            {
                AddLog("All materials already in inventory. Skipping gathering phase.");
                TransitionTo(WorkflowState.PreparingCraft);
            }
        }
        catch (Exception ex)
        {
            HandleError($"Failed to check inventory: {ex.Message}");
        }
    }

    private void ExecutePreparingGather()
    {
        CurrentPhase = WorkflowPhase.Gathering;
        SetStatus("Preparing gathering queue...");

        try
        {
            if (!Expedition.Instance.Ipc.GatherBuddy.IsAvailable)
            {
                Expedition.Instance.Ipc.GatherBuddy.CheckAvailability();
                if (!Expedition.Instance.Ipc.GatherBuddy.IsAvailable)
                {
                    HandleError("GatherBuddy Reborn is not available. Please ensure it is installed and enabled.");
                    return;
                }
            }

            gatheringOrchestrator.BuildQueue(ResolvedRecipe!, config.GatherQuantityBuffer);

            if (config.OptimizeGatherRoute)
            {
                gatheringOrchestrator.OptimizeQueue(config.PrioritizeTimedNodes);
                var routeDesc = ZoneRouteOptimizer.GetRouteDescription(gatheringOrchestrator.Tasks);
                if (routeDesc.Count > 0)
                {
                    AddLog("Optimized gathering route:");
                    foreach (var line in routeDesc)
                        AddLog(line);
                }
            }

            gatheringOrchestrator.Start();
            AddLog($"Gathering started: {gatheringOrchestrator.Tasks.Count} tasks.");

            gatherPrepCompleted = true;
            TransitionTo(WorkflowState.Gathering);
        }
        catch (Exception ex)
        {
            HandleError($"Failed to start gathering: {ex.Message}");
        }
    }

    private void ExecuteGathering()
    {
        CurrentPhase = WorkflowPhase.Gathering;
        gatheringOrchestrator.Update(inventoryManager);

        SetStatus(gatheringOrchestrator.StatusMessage);

        if (gatheringOrchestrator.IsComplete)
        {
            if (gatheringOrchestrator.HasFailures)
            {
                var failures = gatheringOrchestrator.Tasks
                    .Where(t => t.Status == GatheringTaskStatus.Failed)
                    .Select(t => t.ItemName);
                var failMsg = $"Some gathering tasks failed: {string.Join(", ", failures)}";
                AddLog(failMsg);

                if (config.PauseOnError)
                {
                    SetStatus($"Paused: {failMsg}. Fix and resume, or cancel.");
                    TransitionTo(WorkflowState.Paused);
                    return;
                }
            }

            AddLog("Gathering phase complete.");
            inventoryManager.UpdateResolvedRecipe(ResolvedRecipe!);
            TransitionTo(WorkflowState.PreparingCraft);
        }
    }

    private void ExecutePreparingCraft()
    {
        CurrentPhase = WorkflowPhase.Crafting;
        SetStatus("Preparing crafting queue...");

        try
        {
            if (!Expedition.Instance.Ipc.Artisan.IsAvailable)
            {
                Expedition.Instance.Ipc.Artisan.CheckAvailability();
                if (!Expedition.Instance.Ipc.Artisan.IsAvailable)
                {
                    HandleError("Artisan is not available. Please ensure it is installed and enabled.");
                    return;
                }
            }

            var solver = string.IsNullOrEmpty(config.PreferredSolver) ? null : config.PreferredSolver;
            craftingOrchestrator.BuildQueue(ResolvedRecipe!, solver, config.CraftQuantityBuffer);
            craftingOrchestrator.Start();

            var classes = JobSwitchManager.GetRequiredCraftClasses(ResolvedRecipe!.CraftOrder);
            var classNames = string.Join(", ", classes.Select(c => RecipeResolverService.GetCraftTypeName(c)));
            AddLog($"Crafting started: {craftingOrchestrator.Tasks.Count} recipes across {classNames}.");

            craftPrepCompleted = true;
            TransitionTo(WorkflowState.Crafting);
        }
        catch (Exception ex)
        {
            HandleError($"Failed to start crafting: {ex.Message}");
        }
    }

    private void ExecuteCrafting()
    {
        CurrentPhase = WorkflowPhase.Crafting;
        craftingOrchestrator.Update(inventoryManager);

        SetStatus(craftingOrchestrator.StatusMessage);

        if (craftingOrchestrator.IsComplete)
        {
            if (craftingOrchestrator.HasFailures)
            {
                var failures = craftingOrchestrator.Tasks
                    .Where(t => t.Status == CraftingTaskStatus.Failed)
                    .Select(t => t.ItemName);
                AddLog($"Some crafting tasks failed: {string.Join(", ", failures)}");
            }

            AddLog("Workflow complete!");
            var elapsed = StartTime.HasValue ? (DateTime.Now - StartTime.Value) : TimeSpan.Zero;
            SetStatus($"Completed: {CurrentRecipe!.ItemName} x{TargetQuantity} in {elapsed.TotalMinutes:F1}m");

            if (config.NotifyOnCompletion)
            {
                DalamudApi.ChatGui.Print($"[Expedition] Workflow complete: {CurrentRecipe.ItemName} x{TargetQuantity}");
                DalamudApi.ToastGui.ShowNormal($"Expedition: {CurrentRecipe.ItemName} x{TargetQuantity} done!");
            }

            TransitionTo(WorkflowState.Completed);
            OnCompleted?.Invoke();
        }
    }

    // --- Periodic Health Checks ---

    private void RunPeriodicHealthChecks()
    {
        if ((DateTime.Now - lastHealthCheck).TotalSeconds < HealthCheckIntervalSeconds) return;
        lastHealthCheck = DateTime.Now;

        // Durability check
        if (config.MonitorDurabilityDuringRun)
        {
            var report = durabilityMonitor.GetReport();
            if (report.LowestPercent == 0)
            {
                AddLog("[Health] Equipment BROKEN! Pausing workflow.");
                SetStatus("Paused: Equipment broken. Repair and resume.");
                TransitionTo(WorkflowState.Paused);
                return;
            }

            if (report.LowestPercent < config.DurabilityWarningPercent)
            {
                AddLog($"  [Health] {report.StatusText} — consider repairing.");
                DalamudApi.ChatGui.Print($"[Expedition] {report.StatusText}");
            }
        }

        // Food buff expiry
        if (config.WarnOnFoodExpiring)
        {
            var diagnostic = BuffTracker.GetDiagnostic();
            if (diagnostic.FoodExpiringSoon)
            {
                AddLog($"  [Health] Food buff expiring in {diagnostic.FoodRemainingSeconds:F0}s!");
                DalamudApi.ChatGui.Print("[Expedition] Food buff expiring soon! Re-eat food.");
            }
        }
    }

    // --- Helpers ---

    private void TransitionTo(WorkflowState newState)
    {
        var oldState = CurrentState;
        CurrentState = newState;

        if (newState == WorkflowState.Idle || newState == WorkflowState.Completed)
            CurrentPhase = WorkflowPhase.None;

        DalamudApi.Log.Information($"Workflow: {oldState} -> {newState}");
        OnStateChanged?.Invoke(newState);
    }

    private void SetStatus(string message)
    {
        StatusMessage = message;
        OnStatusChanged?.Invoke(message);
    }

    private void HandleError(string message)
    {
        AddLog($"[ERROR] {message}");
        SetStatus($"Error: {message}");
        DalamudApi.Log.Error($"Workflow error: {message}");
        DalamudApi.ChatGui.PrintError($"[Expedition] {message}");

        TransitionTo(WorkflowState.Error);
        OnError?.Invoke(message);
    }

    public void AddLog(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        Log.Add($"[{timestamp}] {message}");
        DalamudApi.Log.Information($"[Workflow] {message}");
    }

    private void ResetOneShots()
    {
        resolveCompleted = false;
        validationCompleted = false;
        inventoryCheckCompleted = false;
        gatherPrepCompleted = false;
        craftPrepCompleted = false;
    }

    public void Dispose()
    {
        Cancel();
    }
}

/// <summary>
/// Top-level workflow states.
/// </summary>
public enum WorkflowState
{
    Idle,
    Resolving,
    Validating,
    CheckingInventory,
    PreparingGather,
    Gathering,
    PreparingCraft,
    Crafting,
    Completed,
    Paused,
    Error,
}

/// <summary>
/// High-level phase indicator for UI.
/// </summary>
public enum WorkflowPhase
{
    None,
    Resolving,
    Validating,
    CheckingInventory,
    Gathering,
    Crafting,
}
