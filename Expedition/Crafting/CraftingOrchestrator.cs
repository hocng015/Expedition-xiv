using Expedition.IPC;
using Expedition.RecipeResolver;

namespace Expedition.Crafting;

/// <summary>
/// Orchestrates crafting operations through Artisan.
/// Manages a queue of crafting tasks in dependency order (sub-recipes first).
/// </summary>
public sealed class CraftingOrchestrator
{
    private readonly IpcManager ipc;
    private readonly List<CraftingTask> taskQueue = new();
    private int currentTaskIndex = -1;
    private DateTime lastPollTime = DateTime.MinValue;
    private bool waitingForArtisanToFinish;

    public CraftingOrchestratorState State { get; private set; } = CraftingOrchestratorState.Idle;
    public IReadOnlyList<CraftingTask> Tasks => taskQueue;
    public CraftingTask? CurrentTask => currentTaskIndex >= 0 && currentTaskIndex < taskQueue.Count
        ? taskQueue[currentTaskIndex] : null;
    public string StatusMessage { get; private set; } = string.Empty;

    public CraftingOrchestrator(IpcManager ipc)
    {
        this.ipc = ipc;
    }

    /// <summary>
    /// Builds the crafting queue from a resolved recipe's craft order.
    /// The order is already dependency-sorted (sub-recipes first).
    /// </summary>
    public void BuildQueue(ResolvedRecipe resolved, string? preferredSolver = null, int quantityBuffer = 0)
    {
        taskQueue.Clear();
        currentTaskIndex = -1;

        foreach (var step in resolved.CraftOrder)
        {
            var task = CraftingTask.FromCraftStep(step, preferredSolver);
            task.Quantity += quantityBuffer;
            taskQueue.Add(task);
        }

        State = taskQueue.Count > 0 ? CraftingOrchestratorState.Ready : CraftingOrchestratorState.Idle;
        StatusMessage = $"Crafting queue: {taskQueue.Count} recipes to craft.";
        DalamudApi.Log.Information(StatusMessage);
    }

    /// <summary>
    /// Begins executing the crafting queue.
    /// </summary>
    public void Start()
    {
        if (taskQueue.Count == 0)
        {
            State = CraftingOrchestratorState.Idle;
            return;
        }

        State = CraftingOrchestratorState.Running;
        currentTaskIndex = 0;
        StartCurrentTask();
    }

    /// <summary>
    /// Stops all crafting and requests Artisan to stop.
    /// </summary>
    public void Stop()
    {
        if (State == CraftingOrchestratorState.Running)
        {
            ipc.Artisan.SetStopRequest(true);
        }

        State = CraftingOrchestratorState.Idle;
        waitingForArtisanToFinish = false;
        StatusMessage = "Crafting stopped.";
    }

    /// <summary>
    /// Called each frame by the workflow engine to poll status.
    /// </summary>
    public void Update(Inventory.InventoryManager inventoryManager)
    {
        if (State != CraftingOrchestratorState.Running) return;

        // Throttle polling
        if ((DateTime.Now - lastPollTime).TotalMilliseconds < 1000) return;
        lastPollTime = DateTime.Now;

        var task = CurrentTask;
        if (task == null)
        {
            State = CraftingOrchestratorState.Completed;
            return;
        }

        if (waitingForArtisanToFinish)
        {
            // Check if Artisan has finished
            if (!ipc.Artisan.GetIsBusy())
            {
                waitingForArtisanToFinish = false;

                // Check how many we crafted via inventory
                var currentCount = inventoryManager.GetItemCount(task.RecipeId);
                task.QuantityCrafted = task.Quantity; // Assume completion if Artisan finished

                task.Status = CraftingTaskStatus.Completed;
                StatusMessage = $"Finished crafting {task.ItemName}.";
                DalamudApi.Log.Information(StatusMessage);

                AdvanceToNextTask();
            }
            else
            {
                // Artisan still working
                var enduranceActive = ipc.Artisan.GetEnduranceStatus();
                var listRunning = ipc.Artisan.GetIsListRunning();
                StatusMessage = $"Crafting {task.ItemName}... (Artisan busy, endurance={enduranceActive})";
            }
            return;
        }

        // Shouldn't get here normally, but handle edge case
        if (task.Status == CraftingTaskStatus.InProgress)
        {
            waitingForArtisanToFinish = true;
        }
    }

    public bool IsComplete => State == CraftingOrchestratorState.Completed || taskQueue.Count == 0;
    public bool HasFailures => taskQueue.Any(t => t.Status == CraftingTaskStatus.Failed);

    private void StartCurrentTask()
    {
        var task = CurrentTask;
        if (task == null) return;

        task.Status = CraftingTaskStatus.InProgress;

        // Set solver preference if specified
        if (!string.IsNullOrEmpty(task.PreferredSolver))
        {
            ipc.Artisan.ChangeSolver(task.RecipeId, task.PreferredSolver, temporary: true);
        }

        // Tell Artisan to craft the item
        ipc.Artisan.CraftItem((ushort)task.RecipeId, task.Quantity);

        waitingForArtisanToFinish = true;
        StatusMessage = $"Crafting {task.ItemName} x{task.Quantity} ({RecipeResolverService.GetCraftTypeName(task.CraftTypeId)})...";
        DalamudApi.Log.Information(StatusMessage);
    }

    private void AdvanceToNextTask()
    {
        // Reset solver if we changed it
        var prevTask = CurrentTask;
        if (prevTask != null && !string.IsNullOrEmpty(prevTask.PreferredSolver))
        {
            ipc.Artisan.ResetSolver(prevTask.RecipeId);
        }

        currentTaskIndex++;
        if (currentTaskIndex >= taskQueue.Count)
        {
            State = CraftingOrchestratorState.Completed;
            StatusMessage = "All crafting tasks complete.";
            DalamudApi.Log.Information(StatusMessage);
            return;
        }

        // Small delay before starting next craft (let Artisan settle)
        Task.Run(async () =>
        {
            await Task.Delay(2000);
            StartCurrentTask();
        });
    }
}

public enum CraftingOrchestratorState
{
    Idle,
    Ready,
    Running,
    Completed,
    Error,
}
