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
    private int inventoryCountBeforeCraft;
    private Inventory.InventoryManager? cachedInventoryManager;

    /// <summary>Maximum number of retry attempts per task when Artisan fails to craft.</summary>
    private const int MaxRetries = 2;

    /// <summary>
    /// Grace period (seconds) after calling CraftItem before we start checking GetIsBusy().
    /// Artisan needs time to: switch class → open crafting log → select recipe → begin craft.
    /// </summary>
    private const double StartupGraceSeconds = 5.0;

    /// <summary>Delay (seconds) between tasks or before retries.</summary>
    private const double InterTaskDelaySeconds = 3.0;

    /// <summary>When the current CraftItem command was sent to Artisan.</summary>
    private DateTime craftItemSentTime = DateTime.MinValue;

    /// <summary>If set, we are in an inter-task or retry delay. Don't do anything until this time.</summary>
    private DateTime? delayUntil;

    /// <summary>If true, a retry is pending after the delay expires.</summary>
    private bool pendingRetry;

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
        delayUntil = null;
        pendingRetry = false;

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
        delayUntil = null;
        pendingRetry = false;
        StatusMessage = "Crafting stopped.";
    }

    /// <summary>
    /// Called each frame by the workflow engine to poll status.
    /// All state transitions happen on the framework thread (no Task.Run for state changes).
    /// </summary>
    public void Update(Inventory.InventoryManager inventoryManager)
    {
        if (State != CraftingOrchestratorState.Running) return;
        cachedInventoryManager = inventoryManager;

        // Throttle polling to 1 second
        if ((DateTime.Now - lastPollTime).TotalMilliseconds < 1000) return;
        lastPollTime = DateTime.Now;

        // If we're in a delay (inter-task or retry), wait it out
        if (delayUntil.HasValue)
        {
            if (DateTime.Now < delayUntil.Value) return;
            delayUntil = null;

            if (pendingStartNext)
            {
                // Inter-task delay expired — start the next task
                pendingStartNext = false;
                StartCurrentTask();
            }
            else if (pendingRetry)
            {
                // Retry delay expired — resend the craft command
                pendingRetry = false;
                var task = CurrentTask;
                if (task != null)
                    SendCraftCommand(task, task.QuantityRemaining);
            }
            return;
        }

        var currentTask = CurrentTask;
        if (currentTask == null)
        {
            State = CraftingOrchestratorState.Completed;
            return;
        }

        if (waitingForArtisanToFinish)
        {
            // Enforce startup grace period — don't check busy state too early
            var elapsed = (DateTime.Now - craftItemSentTime).TotalSeconds;
            if (elapsed < StartupGraceSeconds)
            {
                StatusMessage = $"Waiting for Artisan to start {currentTask.ItemName}... ({elapsed:F0}s)";
                return;
            }

            // Check if Artisan has finished
            if (!ipc.Artisan.GetIsBusy())
            {
                waitingForArtisanToFinish = false;
                HandleArtisanFinished(currentTask, inventoryManager);
            }
            else
            {
                // Artisan still working
                var enduranceActive = ipc.Artisan.GetEnduranceStatus();
                StatusMessage = $"Crafting {currentTask.ItemName}... (Artisan busy, endurance={enduranceActive})";
            }
            return;
        }

        // Shouldn't get here normally, but handle edge case
        if (currentTask.Status == CraftingTaskStatus.InProgress)
        {
            waitingForArtisanToFinish = true;
        }
    }

    public bool IsComplete => State == CraftingOrchestratorState.Completed || taskQueue.Count == 0;
    public bool HasFailures => taskQueue.Any(t => t.Status == CraftingTaskStatus.Failed);

    /// <summary>
    /// Checks whether a task's required ingredients are available by looking at
    /// whether any prerequisite craft tasks in the queue failed.
    /// </summary>
    private bool HasFailedPrerequisites(CraftingTask task)
    {
        // Check if any earlier task in the queue failed — those tasks produce
        // ingredients that later tasks may depend on.
        for (var i = 0; i < currentTaskIndex; i++)
        {
            if (taskQueue[i].Status == CraftingTaskStatus.Failed)
                return true;
        }
        return false;
    }

    private void HandleArtisanFinished(CraftingTask task, Inventory.InventoryManager inventoryManager)
    {
        // Verify via inventory delta — how many were actually crafted?
        var currentCount = inventoryManager.GetItemCount(task.ItemId);
        var crafted = Math.Max(0, currentCount - inventoryCountBeforeCraft);
        task.QuantityCrafted += crafted;

        DalamudApi.Log.Information(
            $"Craft check: {task.ItemName} — had {inventoryCountBeforeCraft}, now {currentCount}, " +
            $"delta={crafted}, target={task.Quantity}, total crafted so far={task.QuantityCrafted}");

        if (task.QuantityCrafted >= task.Quantity)
        {
            // Successfully crafted the required amount
            task.Status = CraftingTaskStatus.Completed;
            StatusMessage = $"Finished crafting {task.ItemName} x{task.QuantityCrafted}.";
            DalamudApi.Log.Information(StatusMessage);
            AdvanceToNextTask();
        }
        else if (crafted == 0)
        {
            // Artisan stopped but nothing was crafted — likely missing ingredients or class issue
            task.RetryCount++;
            if (task.RetryCount <= MaxRetries)
            {
                DalamudApi.Log.Warning(
                    $"Craft task {task.ItemName} produced 0 items (attempt {task.RetryCount}/{MaxRetries}). " +
                    $"Retrying in {InterTaskDelaySeconds}s...");
                StatusMessage = $"Retrying {task.ItemName} (attempt {task.RetryCount}/{MaxRetries})...";

                // Schedule retry via delay (no Task.Run — stays on framework thread)
                pendingRetry = true;
                delayUntil = DateTime.Now.AddSeconds(InterTaskDelaySeconds);
            }
            else
            {
                // Exhausted retries — mark as failed
                task.Status = CraftingTaskStatus.Failed;
                task.ErrorMessage = $"Artisan produced 0 items after {MaxRetries} retries (missing ingredients?).";
                StatusMessage = $"FAILED: {task.ItemName} — {task.ErrorMessage}";
                DalamudApi.Log.Error(StatusMessage);
                AdvanceToNextTask();
            }
        }
        else
        {
            // Partial completion — some items crafted but not enough. Retry remainder.
            task.RetryCount++;
            if (task.RetryCount <= MaxRetries)
            {
                DalamudApi.Log.Warning(
                    $"Craft task {task.ItemName}: crafted {task.QuantityCrafted}/{task.Quantity}. " +
                    $"Retrying remaining {task.QuantityRemaining} (attempt {task.RetryCount}/{MaxRetries})...");
                StatusMessage = $"Crafting {task.ItemName} {task.QuantityCrafted}/{task.Quantity}, retrying...";

                pendingRetry = true;
                delayUntil = DateTime.Now.AddSeconds(InterTaskDelaySeconds);
            }
            else
            {
                task.Status = CraftingTaskStatus.Failed;
                task.ErrorMessage = $"Only crafted {task.QuantityCrafted}/{task.Quantity} after retries.";
                StatusMessage = $"FAILED: {task.ItemName} — {task.ErrorMessage}";
                DalamudApi.Log.Error(StatusMessage);
                AdvanceToNextTask();
            }
        }
    }

    /// <summary>
    /// Sends the CraftItem command to Artisan and records the inventory baseline.
    /// </summary>
    private void SendCraftCommand(CraftingTask task, int quantity)
    {
        // Record inventory count before crafting so we can verify the delta
        if (cachedInventoryManager != null)
            inventoryCountBeforeCraft = cachedInventoryManager.GetItemCount(task.ItemId);
        else
            inventoryCountBeforeCraft = 0;

        ipc.Artisan.CraftItem((ushort)task.RecipeId, quantity);
        craftItemSentTime = DateTime.Now;
        waitingForArtisanToFinish = true;

        DalamudApi.Log.Information($"Sent CraftItem to Artisan: {task.ItemName} x{quantity} (inventory baseline={inventoryCountBeforeCraft})");
    }

    private void StartCurrentTask()
    {
        var task = CurrentTask;
        if (task == null) return;

        // Check for cascading failures — if a prerequisite craft failed, skip this task
        if (HasFailedPrerequisites(task))
        {
            task.Status = CraftingTaskStatus.Failed;
            task.ErrorMessage = "Skipped: a prerequisite craft step failed.";
            StatusMessage = $"Skipped {task.ItemName} — prerequisite failed.";
            DalamudApi.Log.Warning(StatusMessage);
            AdvanceToNextTask();
            return;
        }

        task.Status = CraftingTaskStatus.InProgress;

        // Set solver preference if specified
        if (!string.IsNullOrEmpty(task.PreferredSolver))
        {
            ipc.Artisan.ChangeSolver(task.RecipeId, task.PreferredSolver, temporary: true);
        }

        StatusMessage = $"Crafting {task.ItemName} x{task.Quantity} ({RecipeResolverService.GetCraftTypeName(task.CraftTypeId)})...";
        DalamudApi.Log.Information(StatusMessage);

        // Tell Artisan to craft the item
        SendCraftCommand(task, task.Quantity);
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

        // Delay before starting next craft (let Artisan settle) — handled in Update loop
        delayUntil = DateTime.Now.AddSeconds(InterTaskDelaySeconds);
        pendingRetry = false;

        // Use a callback approach: after the delay, StartCurrentTask will be called
        // We piggyback on the delay mechanism but need separate handling
        // Set a flag so the delay expiry triggers StartCurrentTask instead of a retry
        pendingStartNext = true;
    }

    /// <summary>If true, the delay expiry should start the next task (not a retry).</summary>
    private bool pendingStartNext;
}

public enum CraftingOrchestratorState
{
    Idle,
    Ready,
    Running,
    Completed,
    Error,
}
