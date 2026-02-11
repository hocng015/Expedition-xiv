using Expedition.Crafting;
using Expedition.Gathering;
using Expedition.Inventory;
using Expedition.RecipeResolver;

namespace Expedition.Workflow;

/// <summary>
/// The main workflow state machine. Drives the full gather-to-craft pipeline:
///   1. Resolve recipe tree
///   2. Check inventory
///   3. Gather missing materials (via GatherBuddy Reborn)
///   4. Craft sub-recipes bottom-up (via Artisan)
///   5. Craft final item
///
/// State transitions:
///   Idle → Resolving → CheckingInventory → Gathering → Crafting → Completed
///   Any state → Error (on failure)
///   Any state → Idle (on cancel)
/// </summary>
public sealed class WorkflowEngine : IDisposable
{
    private readonly RecipeResolverService recipeResolver;
    private readonly InventoryManager inventoryManager;
    private readonly GatheringOrchestrator gatheringOrchestrator;
    private readonly CraftingOrchestrator craftingOrchestrator;
    private readonly Configuration config;

    public WorkflowState CurrentState { get; private set; } = WorkflowState.Idle;
    public WorkflowPhase CurrentPhase { get; private set; } = WorkflowPhase.None;
    public string StatusMessage { get; private set; } = string.Empty;
    public RecipeNode? CurrentRecipe { get; private set; }
    public ResolvedRecipe? ResolvedRecipe { get; private set; }
    public int TargetQuantity { get; private set; }
    public DateTime? StartTime { get; private set; }
    public List<string> Log { get; } = new();

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
    /// Called every frame from Framework.Update.
    /// Drives the state machine forward.
    /// </summary>
    public void Update()
    {
        switch (CurrentState)
        {
            case WorkflowState.Resolving:
                ExecuteResolving();
                break;

            case WorkflowState.CheckingInventory:
                ExecuteCheckingInventory();
                break;

            case WorkflowState.PreparingGather:
                ExecutePreparingGather();
                break;

            case WorkflowState.Gathering:
                ExecuteGathering();
                break;

            case WorkflowState.PreparingCraft:
                ExecutePreparingCraft();
                break;

            case WorkflowState.Crafting:
                ExecuteCrafting();
                break;

            case WorkflowState.Completed:
            case WorkflowState.Error:
            case WorkflowState.Idle:
                break;
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

            AddLog($"Recipe resolved: {ResolvedRecipe.GatherList.Count} gatherable materials, " +
                   $"{ResolvedRecipe.CraftOrder.Count} crafting steps, " +
                   $"{ResolvedRecipe.OtherMaterials.Count} other materials.");

            if (ResolvedRecipe.OtherMaterials.Count > 0)
            {
                foreach (var mat in ResolvedRecipe.OtherMaterials)
                {
                    AddLog($"  [!] {mat.ItemName} x{mat.QuantityNeeded} - Not gatherable/craftable, must be obtained manually.");
                }
            }

            TransitionTo(WorkflowState.CheckingInventory);
        }
        catch (Exception ex)
        {
            HandleError($"Failed to resolve recipe: {ex.Message}");
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

            // Check for insufficient inventory space
            var shortfall = inventoryManager.EstimateInventoryShortfall(ResolvedRecipe);
            if (shortfall > 0)
            {
                AddLog($"[Warning] Estimated {shortfall} additional inventory slots needed. May need to free space.");
            }

            // Check for non-obtainable materials
            var missingOther = ResolvedRecipe.OtherMaterials.Where(m => m.QuantityRemaining > 0).ToList();
            if (missingOther.Count > 0)
            {
                var names = string.Join(", ", missingOther.Select(m => $"{m.ItemName} x{m.QuantityRemaining}"));
                if (config.PauseOnError)
                {
                    HandleError($"Missing non-gatherable materials: {names}. Obtain these manually and restart.");
                    return;
                }
                else
                {
                    AddLog($"[Warning] Missing non-gatherable materials: {names}. Proceeding anyway.");
                }
            }

            if (totalGatherNeeded > 0)
            {
                TransitionTo(WorkflowState.PreparingGather);
            }
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
            // Verify GatherBuddy Reborn is available
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
            gatheringOrchestrator.Start();

            AddLog($"Gathering started: {gatheringOrchestrator.Tasks.Count} tasks.");
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
                    HandleError(failMsg);
                    return;
                }
            }

            AddLog("Gathering phase complete.");

            // Re-check inventory after gathering
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
            // Verify Artisan is available
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

            AddLog($"Crafting started: {craftingOrchestrator.Tasks.Count} recipes.");
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
            SetStatus($"Completed: {CurrentRecipe!.ItemName} x{TargetQuantity}");

            if (config.NotifyOnCompletion)
            {
                DalamudApi.ChatGui.Print($"[Expedition] Workflow complete: {CurrentRecipe.ItemName} x{TargetQuantity}");
                DalamudApi.ToastGui.ShowNormal($"Expedition: {CurrentRecipe.ItemName} x{TargetQuantity} done!");
            }

            TransitionTo(WorkflowState.Completed);
            OnCompleted?.Invoke();
        }
    }

    // --- Helpers ---

    private void TransitionTo(WorkflowState newState)
    {
        var oldState = CurrentState;
        CurrentState = newState;

        if (newState == WorkflowState.Idle || newState == WorkflowState.Completed)
        {
            CurrentPhase = WorkflowPhase.None;
        }

        DalamudApi.Log.Information($"Workflow: {oldState} → {newState}");
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

    private void AddLog(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        Log.Add($"[{timestamp}] {message}");
        DalamudApi.Log.Information($"[Workflow] {message}");
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
    CheckingInventory,
    PreparingGather,
    Gathering,
    PreparingCraft,
    Crafting,
    Completed,
    Error,
}

/// <summary>
/// High-level phase indicator for UI.
/// </summary>
public enum WorkflowPhase
{
    None,
    Resolving,
    CheckingInventory,
    Gathering,
    Crafting,
}
