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

    /// <summary>Maximum retry attempts per task. Reads from user config (default 3).</summary>
    private static int MaxRetries => Expedition.Config.MaxRetryPerTask;

    /// <summary>
    /// Grace period (seconds) after calling CraftItem before we start checking GetIsBusy().
    /// Artisan needs time to: switch class → open crafting log → select recipe → begin craft.
    /// </summary>
    private const double StartupGraceSeconds = 8.0;

    /// <summary>
    /// Maximum time (seconds) to wait for Artisan to report busy after the grace period.
    /// If Artisan never reports busy within this window, we assume the craft command was lost.
    /// </summary>
    private const double BusyConfirmationTimeoutSeconds = 15.0;

    /// <summary>
    /// Maximum time (seconds) to wait for Artisan to become idle before sending a new CraftItem.
    /// Artisan may still be wrapping up the previous craft (closing craft log, animations, etc.)
    /// even after we've detected completion via inventory delta.
    /// </summary>
    private const double PreSendBusyWaitTimeoutSeconds = 30.0;

    /// <summary>Delay (seconds) between tasks. Reads from user config.</summary>
    private static double InterTaskDelaySeconds => Expedition.Config.CraftStepDelaySeconds;

    /// <summary>Delay multiplier for retries (retries use 2x the inter-task delay for recovery).</summary>
    private const double RetryDelayMultiplier = 2.0;

    /// <summary>When the current CraftItem command was sent to Artisan.</summary>
    private DateTime craftItemSentTime = DateTime.MinValue;

    /// <summary>If set, we are in an inter-task or retry delay. Don't do anything until this time.</summary>
    private DateTime? delayUntil;

    /// <summary>If true, a retry is pending after the delay expires.</summary>
    private bool pendingRetry;

    /// <summary>
    /// If true, we're waiting for Artisan to report not-busy before sending the next CraftItem.
    /// This prevents sending commands while Artisan is still wrapping up the previous craft.
    /// </summary>
    private bool waitingForArtisanIdle;

    /// <summary>When we started waiting for Artisan to become idle (for timeout).</summary>
    private DateTime artisanIdleWaitStart = DateTime.MinValue;

    /// <summary>
    /// Whether Artisan has reported busy at least once since the last CraftItem command.
    /// This prevents false "finished" detection if Artisan briefly reports not-busy during startup.
    /// </summary>
    private bool artisanEverReportedBusy;

    /// <summary>
    /// CraftTypeId of the last task we sent to Artisan. Used to detect class switches
    /// between consecutive crafts (e.g., BSM→CRP→BSM). When a class change is detected,
    /// we reset Artisan before sending the next CraftItem to prevent silent failures.
    /// -1 = no previous craft (first task in the queue).
    /// </summary>
    private int lastCraftTypeId = -1;

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
        lastCraftTypeId = -1;

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
    /// Checks if Artisan is idle before dispatching the first command.
    /// </summary>
    public void Start()
    {
        if (taskQueue.Count == 0)
        {
            State = CraftingOrchestratorState.Idle;
            return;
        }

        // Clear any lingering Artisan stop request from a previous workflow
        ipc.Artisan.SetStopRequest(false);

        State = CraftingOrchestratorState.Running;
        currentTaskIndex = 0;

        // Gate: if Artisan is still busy from a previous workflow or manual craft,
        // wait for it to become idle before dispatching the first CraftItem.
        if (ipc.Artisan.GetIsBusy())
        {
            DalamudApi.Log.Information(
                "Artisan is still busy at craft queue start. Waiting for idle before dispatching first task.");
            waitingForArtisanIdle = true;
            artisanIdleWaitStart = DateTime.Now;
            pendingStartNext = true;
        }
        else
        {
            StartCurrentTask();
        }
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
        artisanEverReportedBusy = false;
        waitingForArtisanIdle = false;
        delayUntil = null;
        pendingRetry = false;
        lastCraftTypeId = -1;
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

        // If we're waiting for Artisan to become idle before sending the next command
        if (waitingForArtisanIdle)
        {
            var artisanBusy = ipc.Artisan.GetIsBusy();
            if (artisanBusy)
            {
                var waitElapsed = (DateTime.Now - artisanIdleWaitStart).TotalSeconds;
                if (waitElapsed > PreSendBusyWaitTimeoutSeconds)
                {
                    DalamudApi.Log.Warning(
                        $"Artisan still busy after {waitElapsed:F0}s idle-wait timeout. Proceeding anyway.");
                    waitingForArtisanIdle = false;
                    // Fall through to dispatch below
                }
                else
                {
                    StatusMessage = $"Waiting for Artisan to finish previous craft... ({waitElapsed:F0}s)";
                    return;
                }
            }
            else
            {
                var waitElapsed = (DateTime.Now - artisanIdleWaitStart).TotalSeconds;
                DalamudApi.Log.Information($"Artisan is now idle after {waitElapsed:F1}s wait. Dispatching next command.");
                waitingForArtisanIdle = false;
                // Fall through to dispatch below
            }

            // Dispatch the pending action
            if (pendingStartNext)
            {
                pendingStartNext = false;
                StartCurrentTask();
            }
            else if (pendingRetry)
            {
                pendingRetry = false;
                var task = CurrentTask;
                if (task != null)
                    SendCraftCommand(task, task.QuantityRemaining);
            }
            return;
        }

        // If we're in a delay (inter-task or retry), wait it out
        if (delayUntil.HasValue)
        {
            if (DateTime.Now < delayUntil.Value) return;
            delayUntil = null;

            // Delay expired — before dispatching, ensure Artisan is idle.
            // Artisan may still be wrapping up the previous craft even after inventory delta confirmed completion.
            if (ipc.Artisan.GetIsBusy())
            {
                DalamudApi.Log.Information(
                    "Inter-task delay expired but Artisan is still busy. Waiting for idle before sending next command.");
                waitingForArtisanIdle = true;
                artisanIdleWaitStart = DateTime.Now;
                return;
            }

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
            var elapsed = (DateTime.Now - craftItemSentTime).TotalSeconds;
            var artisanBusy = ipc.Artisan.GetIsBusy();

            // Phase 1: Startup grace period — don't act on busy state too early
            if (elapsed < StartupGraceSeconds)
            {
                // Track if Artisan has acknowledged the craft during the grace period
                if (artisanBusy) artisanEverReportedBusy = true;
                StatusMessage = $"Waiting for Artisan to start {currentTask.ItemName}... ({elapsed:F0}s)";
                return;
            }

            // Phase 2: After grace period, wait for Artisan to report busy at least once.
            // This prevents false "finished" detection if Artisan briefly reports not-busy
            // during class switching or recipe selection.
            if (!artisanEverReportedBusy)
            {
                if (artisanBusy)
                {
                    // Artisan is now busy — we can start watching for completion
                    artisanEverReportedBusy = true;
                    DalamudApi.Log.Information($"Artisan confirmed busy for {currentTask.ItemName} at {elapsed:F1}s");
                }
                else if (elapsed > StartupGraceSeconds + BusyConfirmationTimeoutSeconds)
                {
                    // Artisan never reported busy — the CraftItem command was likely lost
                    DalamudApi.Log.Warning(
                        $"Artisan never reported busy for {currentTask.ItemName} after {elapsed:F1}s. " +
                        "Treating as failed attempt (craft command may have been lost).");
                    waitingForArtisanToFinish = false;
                    HandleArtisanFinished(currentTask, inventoryManager);
                }
                else
                {
                    StatusMessage = $"Waiting for Artisan to acknowledge {currentTask.ItemName}... ({elapsed:F0}s)";
                }
                return;
            }

            // Phase 3: Artisan was busy and is now not busy — craft attempt has finished
            if (!artisanBusy)
            {
                DalamudApi.Log.Information($"Artisan finished for {currentTask.ItemName} at {elapsed:F1}s");
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
    public bool HasFailures
    {
        get
        {
            for (var i = 0; i < taskQueue.Count; i++)
                if (taskQueue[i].Status == CraftingTaskStatus.Failed) return true;
            return false;
        }
    }

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
            var retryDelay = InterTaskDelaySeconds * RetryDelayMultiplier;
            if (task.RetryCount <= MaxRetries)
            {
                DalamudApi.Log.Warning(
                    $"Craft task {task.ItemName} produced 0 items (attempt {task.RetryCount}/{MaxRetries}). " +
                    $"Retrying in {retryDelay:F1}s... (busyEverSeen={artisanEverReportedBusy})");
                StatusMessage = $"Retrying {task.ItemName} (attempt {task.RetryCount}/{MaxRetries})...";

                // Schedule retry with longer delay to give Artisan time to recover
                pendingRetry = true;
                delayUntil = DateTime.Now.AddSeconds(retryDelay);
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
            var retryDelay = InterTaskDelaySeconds * RetryDelayMultiplier;
            if (task.RetryCount <= MaxRetries)
            {
                DalamudApi.Log.Warning(
                    $"Craft task {task.ItemName}: crafted {task.QuantityCrafted}/{task.Quantity}. " +
                    $"Retrying remaining {task.QuantityRemaining} (attempt {task.RetryCount}/{MaxRetries}) in {retryDelay:F1}s...");
                StatusMessage = $"Crafting {task.ItemName} {task.QuantityCrafted}/{task.Quantity}, retrying...";

                pendingRetry = true;
                delayUntil = DateTime.Now.AddSeconds(retryDelay);
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
    /// If Artisan is still busy, defers to the idle-wait gate instead of sending immediately.
    ///
    /// When the craft class changes between tasks (e.g., BSM→CRP→BSM), Artisan's CraftItem
    /// IPC can silently fail — it never reports busy and the craft never starts. To work around
    /// this, we do a stop→restart cycle on Artisan whenever a class switch is detected.
    /// </summary>
    private void SendCraftCommand(CraftingTask task, int quantity)
    {
        // Defensive gate: if Artisan is still busy, defer dispatch to the idle-wait loop
        // instead of sending a command that will be silently dropped.
        var artisanBusy = ipc.Artisan.GetIsBusy();
        var artisanEndurance = ipc.Artisan.GetEnduranceStatus();
        var artisanStopReq = ipc.Artisan.GetStopRequest();

        if (artisanBusy)
        {
            DalamudApi.Log.Warning(
                $"Artisan is STILL BUSY when about to send CraftItem for {task.ItemName}. " +
                "Deferring to idle-wait gate instead of sending command that would be dropped.");
            waitingForArtisanIdle = true;
            artisanIdleWaitStart = DateTime.Now;

            // Determine whether this is a retry or a new task dispatch
            if (task.RetryCount > 0 && task.Status == CraftingTaskStatus.InProgress)
                pendingRetry = true;
            else
                pendingStartNext = true;
            return;
        }

        DalamudApi.Log.Information(
            $"Artisan state before CraftItem: busy={artisanBusy}, endurance={artisanEndurance}, " +
            $"stopRequest={artisanStopReq}");

        // --- Class-switch workaround ---
        // Artisan's CraftItem IPC sometimes silently fails when it needs to switch crafting
        // classes internally (e.g. BSM→CRP→BSM). The CraftItem call returns, but Artisan never
        // opens the recipe or reports busy. A stop→restart cycle before the command fixes this.
        if (lastCraftTypeId >= 0 && task.CraftTypeId != lastCraftTypeId)
        {
            var prevClassName = RecipeResolver.RecipeResolverService.GetCraftTypeName(lastCraftTypeId);
            var newClassName = RecipeResolver.RecipeResolverService.GetCraftTypeName(task.CraftTypeId);
            DalamudApi.Log.Information(
                $"[Craft:ClassSwitch] Class change detected: {prevClassName} → {newClassName}. " +
                "Resetting Artisan (stop→restart) to prevent silent CraftItem failure.");
            ipc.Artisan.SetStopRequest(true);
            ipc.Artisan.SetStopRequest(false);
        }

        // Record inventory count before crafting so we can verify the delta
        if (cachedInventoryManager != null)
            inventoryCountBeforeCraft = cachedInventoryManager.GetItemCount(task.ItemId);
        else
            inventoryCountBeforeCraft = 0;

        // Reset busy tracking — must see Artisan report busy before treating not-busy as completion
        artisanEverReportedBusy = false;

        // Clear any lingering stop request from a previous task or workflow —
        // Artisan may silently ignore CraftItem if a stop was previously requested.
        if (artisanStopReq)
        {
            ipc.Artisan.SetStopRequest(false);
            DalamudApi.Log.Information("Cleared Artisan stop request before sending new CraftItem.");
        }

        ipc.Artisan.CraftItem((ushort)task.RecipeId, quantity);
        craftItemSentTime = DateTime.Now;
        waitingForArtisanToFinish = true;
        lastCraftTypeId = task.CraftTypeId;

        DalamudApi.Log.Information(
            $"Sent CraftItem to Artisan: {task.ItemName} x{quantity} (recipeId={task.RecipeId}) " +
            $"(inventory baseline={inventoryCountBeforeCraft}, retries so far={task.RetryCount}/{MaxRetries})");
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
