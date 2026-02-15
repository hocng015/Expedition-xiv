using Dalamud.Game.ClientState.Conditions;
using Expedition.IPC;
using Expedition.RecipeResolver;
using Expedition.Scheduling;

namespace Expedition.Gathering;

/// <summary>
/// Orchestrates gathering operations through GatherBuddy Reborn.
/// Manages a queue of gathering tasks and drives GBR via IPC and reflection.
///
/// Architecture:
///   1. Uses reflection to inject items into GBR's auto-gather list.
///      This gives GBR full knowledge of what to gather, enabling its
///      AutoGather to handle teleportation, pathfinding (vnavmesh),
///      node interaction, and gathering automatically.
///   2. Sends class-specific gather commands (/gathermin, /gatherbtn)
///      as a fallback hint and to trigger initial teleport.
///   3. Enables AutoGather via IPC.
///   4. Monitors inventory counts as the primary completion indicator.
///   5. Cleans up the injected list when gathering is done.
/// </summary>
public sealed class GatheringOrchestrator
{
    private readonly IpcManager ipc;
    private readonly List<GatheringTask> taskQueue = new();
    private int currentTaskIndex = -1;
    private DateTime lastPollTime = DateTime.MinValue;
    private DateTime taskStartTime = DateTime.MinValue;
    private int lastKnownCount = -1;
    private bool listInjected;

    /// <summary>
    /// True when the current task's item couldn't be added to GBR's auto-gather list
    /// (e.g., crystals not in GBR's Gatherables dictionary). In this mode we rely on
    /// periodic /gather commands instead of AutoGather list-driven gathering.
    /// </summary>
    private bool commandOnlyMode;

    /// <summary>
    /// How often to re-issue the /gather command in command-only mode.
    /// GBR's /gather navigates to the item and gathers one node cycle,
    /// so we need to periodically re-send it for continued gathering.
    /// </summary>
    private const double CommandReissueIntervalSeconds = 30.0;

    /// <summary>When we last sent a /gather command in command-only mode.</summary>
    private DateTime lastCommandReissueTime = DateTime.MinValue;

    /// <summary>
    /// How long to wait with no inventory progress before declaring a task stalled.
    /// This needs to be generous to account for teleport, travel time, and node respawns.
    /// </summary>
    private const double StallTimeoutSeconds = 300.0; // 5 minutes

    /// <summary>
    /// How often to re-enable AutoGather if GBR has disabled itself.
    /// </summary>
    private const double AutoGatherReEnableIntervalSeconds = 10.0;

    /// <summary>
    /// Tracks when we last saw inventory progress for the current task.
    /// </summary>
    private DateTime lastProgressTime = DateTime.MinValue;

    /// <summary>
    /// Tracks when we last attempted to re-enable AutoGather.
    /// </summary>
    private DateTime lastAutoGatherCheck = DateTime.MinValue;

    /// <summary>
    /// How many consecutive polls GBR has been in waiting/disabled state with no progress.
    /// </summary>
    private int gbrIdlePolls;

    /// <summary>
    /// Tracks consecutive times we've re-enabled AutoGather only for GBR to immediately disable itself again.
    /// If this exceeds a threshold, GBR's internal state is likely corrupted and we need a full reset cycle.
    /// </summary>
    private int consecutiveReEnableFailures;

    /// <summary>
    /// After this many consecutive re-enable failures, perform a full GBR reset cycle
    /// (disable AutoGather, wait, then re-inject list and restart).
    /// </summary>
    private const int MaxReEnableFailuresBeforeReset = 3;

    /// <summary>
    /// When non-null, we're in a delay between tasks. Update() skips gathering logic until this time.
    /// Replaces Task.Run+Task.Delay to avoid async state machine allocations.
    /// </summary>
    private DateTime? interTaskDelayUntil;

    /// <summary>
    /// When non-null, the task quantity has been met but we're waiting for the player
    /// to finish mining/harvesting the current node before advancing.
    /// </summary>
    private DateTime? finishingNodeSince;

    /// <summary>
    /// Max time to wait for the player to finish the current node after the task target is met.
    /// After this, we advance regardless.
    /// </summary>
    private const double FinishNodeTimeoutSeconds = 30.0;

    public GatheringOrchestratorState State { get; private set; } = GatheringOrchestratorState.Idle;
    public IReadOnlyList<GatheringTask> Tasks => taskQueue;
    public GatheringTask? CurrentTask => currentTaskIndex >= 0 && currentTaskIndex < taskQueue.Count
        ? taskQueue[currentTaskIndex] : null;
    public string StatusMessage { get; private set; } = string.Empty;

    public GatheringOrchestrator(IpcManager ipc)
    {
        this.ipc = ipc;
        ipc.GatherBuddy.OnAutoGatherWaiting += OnGbrWaiting;
        ipc.GatherBuddy.OnAutoGatherEnabledChanged += OnGbrEnabledChanged;
    }

    /// <summary>
    /// Builds the gathering queue from a resolved recipe's gather list.
    /// Filters out items the player already has enough of.
    /// </summary>
    public void BuildQueue(ResolvedRecipe resolved, int quantityBuffer = 0)
    {
        taskQueue.Clear();
        currentTaskIndex = -1;

        foreach (var mat in resolved.GatherList)
        {
            if (mat.QuantityRemaining <= 0) continue;

            var task = GatheringTask.FromMaterial(mat);
            task.QuantityNeeded += quantityBuffer;
            taskQueue.Add(task);
        }

        State = taskQueue.Count > 0 ? GatheringOrchestratorState.Ready : GatheringOrchestratorState.Idle;
        StatusMessage = $"Gathering queue: {taskQueue.Count} items to gather.";
        DalamudApi.Log.Information(StatusMessage);
    }

    /// <summary>
    /// Optimizes the task queue for zone efficiency and timed node scheduling.
    /// </summary>
    public void OptimizeQueue(bool prioritizeTimedNodes)
    {
        if (taskQueue.Count <= 1) return;

        var optimized = ZoneRouteOptimizer.OptimizeRoute(taskQueue);

        if (prioritizeTimedNodes)
        {
            // Further sort: active timed nodes go to the front
            var scheduled = NodeScheduler.BuildScheduledQueue(optimized);
            optimized = new List<GatheringTask>(scheduled.Count);
            for (var i = 0; i < scheduled.Count; i++)
                optimized.Add(scheduled[i].Task);
        }

        taskQueue.Clear();
        taskQueue.AddRange(optimized);

        StatusMessage = $"Gathering queue optimized: {taskQueue.Count} tasks.";
        DalamudApi.Log.Information(StatusMessage);
    }

    /// <summary>
    /// Begins executing the gathering queue.
    /// Injects all items into GBR's auto-gather list, then enables AutoGather.
    /// </summary>
    public void Start()
    {
        if (taskQueue.Count == 0)
        {
            State = GatheringOrchestratorState.Idle;
            return;
        }

        // Inject all gather items into GBR's auto-gather list via reflection
        InjectGatherList();

        State = GatheringOrchestratorState.Running;
        currentTaskIndex = 0;
        StartCurrentTask();
    }

    /// <summary>
    /// Stops all gathering, disables GBR AutoGather, and cleans up injected list.
    /// </summary>
    public void Stop()
    {
        if (State == GatheringOrchestratorState.Running)
        {
            ipc.GatherBuddy.SetAutoGatherEnabled(false);
        }

        CleanupGatherList();

        interTaskDelayUntil = null;
        State = GatheringOrchestratorState.Idle;
        StatusMessage = "Gathering stopped.";
    }

    /// <summary>
    /// Called each frame by the workflow engine to poll status.
    /// Uses inventory monitoring as the primary completion indicator,
    /// with GBR state monitoring to detect and recover from stalls.
    /// </summary>
    public void Update(Inventory.InventoryManager inventoryManager)
    {
        if (State != GatheringOrchestratorState.Running) return;

        // Throttle polling to once per second
        if ((DateTime.Now - lastPollTime).TotalMilliseconds < 1000) return;
        lastPollTime = DateTime.Now;

        // Inter-task delay (replaces Task.Run + Task.Delay)
        if (interTaskDelayUntil.HasValue)
        {
            if (DateTime.Now < interTaskDelayUntil.Value) return;
            interTaskDelayUntil = null;
            StartCurrentTask();
            return;
        }

        var task = CurrentTask;
        if (task == null)
        {
            State = GatheringOrchestratorState.Completed;
            return;
        }

        // Check inventory to see if we've gathered enough (include saddlebag to match StartGather's count)
        var currentCount = inventoryManager.GetItemCount(task.ItemId, includeSaddlebag: Expedition.Config.IncludeSaddlebagInScans);
        task.QuantityGathered = currentCount;

        // Track progress — reset the stall timer whenever inventory increases
        if (lastKnownCount < 0)
        {
            // First poll after task start: seed with actual count (don't treat as progress)
            lastKnownCount = currentCount;
        }
        else if (currentCount > lastKnownCount)
        {
            DalamudApi.Log.Debug($"Gathering progress: {task.ItemName} {lastKnownCount} -> {currentCount}");
            lastKnownCount = currentCount;
            lastProgressTime = DateTime.Now;
            gbrIdlePolls = 0;
        }

        if (task.IsComplete)
        {
            // Target quantity met — but wait for the player to finish the current node.
            // Walking away from a half-mined node is wasteful; let GBR exhaust it.
            var isAtNode = DalamudApi.Condition[ConditionFlag.Gathering]
                        || DalamudApi.Condition[ConditionFlag.ExecutingGatheringAction];

            if (isAtNode)
            {
                if (!finishingNodeSince.HasValue)
                {
                    finishingNodeSince = DateTime.Now;
                    DalamudApi.Log.Information($"Target met for {task.ItemName} — finishing current node before advancing.");
                }

                // Safety timeout so we don't wait forever
                var waitingSeconds = (DateTime.Now - finishingNodeSince.Value).TotalSeconds;
                if (waitingSeconds < FinishNodeTimeoutSeconds)
                {
                    StatusMessage = $"Gathered enough {task.ItemName} — finishing node ({task.QuantityGathered}/{task.QuantityNeeded})...";
                    return; // Keep waiting, let GBR finish the node
                }

                DalamudApi.Log.Information($"Node finish timeout for {task.ItemName}, advancing.");
            }

            // Node is done (or we weren't at one, or timeout) — advance
            finishingNodeSince = null;
            task.Status = GatheringTaskStatus.Completed;
            StatusMessage = $"Gathered enough {task.ItemName}.";
            DalamudApi.Log.Information(StatusMessage);
            AdvanceToNextTask();
            return;
        }

        // Monitor GBR state — re-enable AutoGather if it disabled itself
        var gbrEnabled = ipc.GatherBuddy.GetAutoGatherEnabled();
        var gbrWaiting = ipc.GatherBuddy.GetAutoGatherWaiting();
        var playerOccupied = IsPlayerOccupied();

        if (commandOnlyMode)
        {
            // Command-only mode: item wasn't in GBR's auto-gather list.
            // We rely on /gather commands to drive GBR. Re-issue periodically
            // and re-enable AutoGather if it disables itself.
            if (!playerOccupied)
            {
                var sinceLastCommand = (DateTime.Now - lastCommandReissueTime).TotalSeconds;

                if (!gbrEnabled && sinceLastCommand >= AutoGatherReEnableIntervalSeconds)
                {
                    DalamudApi.Log.Information(
                        $"[Command-only] GBR is OFF — re-issuing /gather for {task.ItemName} " +
                        $"(need {task.QuantityRemaining} more)");
                    ChatIpc.GatherItem(task.ItemName);
                    ipc.GatherBuddy.SetAutoGatherEnabled(true);
                    lastCommandReissueTime = DateTime.Now;
                }
                else if (gbrEnabled && sinceLastCommand >= CommandReissueIntervalSeconds)
                {
                    // Periodically re-issue /gather to keep GBR targeted on the right item
                    // (GBR may finish a node cycle and go idle without an auto-gather list entry)
                    if (gbrWaiting)
                    {
                        DalamudApi.Log.Debug($"[Command-only] GBR waiting, re-issuing /gather for {task.ItemName}");
                        ChatIpc.GatherItem(task.ItemName);
                        lastCommandReissueTime = DateTime.Now;
                    }
                }
            }
            else
            {
                StatusMessage = $"Waiting for player to be free before gathering {task.ItemName}...";
            }
        }
        else if (!gbrEnabled)
        {
            gbrIdlePolls++;
            consecutiveReEnableFailures++;

            // Don't try to re-enable while the player is in a blocking state (crafting, cutscene, etc.)
            if (playerOccupied)
            {
                StatusMessage = $"Waiting for player to be free before gathering {task.ItemName}...";
                return;
            }

            var sinceLastReEnable = (DateTime.Now - lastAutoGatherCheck).TotalSeconds;

            if (sinceLastReEnable >= AutoGatherReEnableIntervalSeconds)
            {
                lastAutoGatherCheck = DateTime.Now;

                // If GBR keeps disabling itself immediately after re-enable, its internal state
                // is likely corrupted (e.g. stale TaskManager tasks from a Dalamud plugin reload).
                // Perform a full reset cycle: disable, re-inject the gather list, then re-enable.
                if (consecutiveReEnableFailures > MaxReEnableFailuresBeforeReset)
                {
                    DalamudApi.Log.Warning(
                        $"GBR AutoGather has failed {consecutiveReEnableFailures} consecutive re-enables for {task.ItemName}. " +
                        "Performing full reset cycle (disable → re-inject list → re-enable).");

                    // Full disable to clear any stale GBR state
                    ipc.GatherBuddy.SetAutoGatherEnabled(false);

                    // Re-inject the gather list from scratch
                    CleanupGatherList();
                    InjectGatherList();

                    // Re-send the gather command and re-enable
                    SendGatherCommand(task);
                    ipc.GatherBuddy.SetAutoGatherEnabled(true);

                    // Reset the counter — give the fresh cycle a chance
                    consecutiveReEnableFailures = 0;
                    return;
                }

                // Normal re-enable attempt
                DalamudApi.Log.Information(
                    $"GBR AutoGather is OFF — re-enabling for {task.ItemName} " +
                    $"(need {task.QuantityRemaining} more, re-enable attempt {consecutiveReEnableFailures}/{MaxReEnableFailuresBeforeReset})");
                SendGatherCommand(task);
                ipc.GatherBuddy.SetAutoGatherEnabled(true);
            }
        }
        else
        {
            gbrIdlePolls = 0;
            // GBR is enabled and running — reset the failure counter
            if (consecutiveReEnableFailures > 0)
            {
                DalamudApi.Log.Information($"GBR AutoGather re-enabled successfully after {consecutiveReEnableFailures} failures.");
                consecutiveReEnableFailures = 0;
            }
        }

        // Check for stall: no inventory progress for a long time
        var secondsSinceProgress = (DateTime.Now - lastProgressTime).TotalSeconds;
        if (secondsSinceProgress > StallTimeoutSeconds)
        {
            task.RetryCount++;
            if (task.RetryCount > Expedition.Config.GatherRetryLimit)
            {
                task.Status = GatheringTaskStatus.Failed;
                task.ErrorMessage = $"No gathering progress for {StallTimeoutSeconds / 60:F0} minutes after {task.RetryCount} attempts.";
                DalamudApi.Log.Warning($"Gathering task failed: {task.ItemName} — {task.ErrorMessage}");
                AdvanceToNextTask();
            }
            else
            {
                // Retry: re-send the gather command
                DalamudApi.Log.Information($"Gathering stalled for {task.ItemName}, retrying (attempt {task.RetryCount})");
                StartCurrentTask();
            }
            return;
        }

        // Update status message with GBR state info
        var elapsed = (DateTime.Now - taskStartTime).TotalSeconds;
        var cmdTag = commandOnlyMode ? " [cmd]" : "";
        var gbrStatus = !gbrEnabled ? $" [GBR: OFF — re-enabling]{cmdTag}" :
                        gbrWaiting  ? $" [GBR: Waiting]{cmdTag}" : cmdTag;

        if (task.IsTimedNode && task.SpawnHours != null)
        {
            // For-loop instead of LINQ .Any()/.Min() to avoid delegate + enumerator allocation every second
            var isActive = false;
            var nextSpawn = double.MaxValue;
            for (var i = 0; i < task.SpawnHours.Length; i++)
            {
                if (EorzeanTime.IsWithinWindow(task.SpawnHours[i], 2))
                {
                    isActive = true;
                    break;
                }
                var s = EorzeanTime.SecondsUntilEorzeanHour(task.SpawnHours[i]);
                if (s < nextSpawn) nextSpawn = s;
            }

            if (isActive)
                StatusMessage = $"Gathering {task.ItemName}: {task.QuantityGathered}/{task.QuantityNeeded} (timed node ACTIVE){gbrStatus}";
            else
            {
                StatusMessage = $"Gathering {task.ItemName}: {task.QuantityGathered}/{task.QuantityNeeded} " +
                                $"(timed node spawns in {EorzeanTime.FormatRealDuration(nextSpawn)}){gbrStatus}";
            }
        }
        else
        {
            StatusMessage = $"Gathering {task.ItemName}: {task.QuantityGathered}/{task.QuantityNeeded} ({elapsed:F0}s){gbrStatus}";
        }
    }

    /// <summary>
    /// Returns true when all gathering tasks are complete (or failed/skipped).
    /// </summary>
    public bool IsComplete => State == GatheringOrchestratorState.Completed || taskQueue.Count == 0;

    /// <summary>
    /// Returns true if there are any failed tasks.
    /// Uses for-loop instead of LINQ to avoid enumerator allocation on every access.
    /// </summary>
    public bool HasFailures
    {
        get
        {
            for (var i = 0; i < taskQueue.Count; i++)
                if (taskQueue[i].Status == GatheringTaskStatus.Failed) return true;
            return false;
        }
    }

    /// <summary>
    /// Injects all queued items into GBR's auto-gather list via reflection.
    /// This is what enables GBR's AutoGather to know what to gather.
    /// </summary>
    private void InjectGatherList()
    {
        var items = new List<(uint, uint)>(taskQueue.Count);
        for (var i = 0; i < taskQueue.Count; i++)
        {
            var t = taskQueue[i];
            if (t.Status != GatheringTaskStatus.Completed && t.Status != GatheringTaskStatus.Failed)
                items.Add((t.ItemId, (uint)t.QuantityRemaining));
        }

        if (items.Count == 0) return;

        if (ipc.GatherBuddyLists.SetGatherList(items))
        {
            listInjected = true;
            DalamudApi.Log.Information($"Injected {items.Count} items into GBR auto-gather list.");
        }
        else
        {
            DalamudApi.Log.Warning($"Failed to inject GBR auto-gather list: {ipc.GatherBuddyLists.LastError}. " +
                                   "Falling back to /gather commands only.");
        }
    }

    /// <summary>
    /// Removes the Expedition-managed list from GBR when gathering is done.
    /// </summary>
    private void CleanupGatherList()
    {
        if (listInjected)
        {
            ipc.GatherBuddyLists.RemoveExpeditionList();
            listInjected = false;
        }
    }

    /// <summary>
    /// Returns true if the player is in a blocking state (crafting, cutscene, teleporting, etc.)
    /// that would prevent GBR from teleporting or gathering.
    /// </summary>
    private static bool IsPlayerOccupied()
    {
        var cond = DalamudApi.Condition;
        return cond[ConditionFlag.Crafting]
            || cond[ConditionFlag.PreparingToCraft]
            || cond[ConditionFlag.ExecutingCraftingAction]
            || cond[ConditionFlag.Casting]
            || cond[ConditionFlag.Jumping]
            || cond[ConditionFlag.InFlight]
            || cond[ConditionFlag.Occupied]
            || cond[ConditionFlag.Occupied30]
            || cond[ConditionFlag.Occupied33]
            || cond[ConditionFlag.Occupied38]
            || cond[ConditionFlag.Occupied39]
            || cond[ConditionFlag.OccupiedInCutSceneEvent]
            || cond[ConditionFlag.OccupiedInQuestEvent]
            || cond[ConditionFlag.BetweenAreas]
            || cond[ConditionFlag.BetweenAreas51];
    }

    /// <summary>
    /// Sends the appropriate class-specific gather command for the given task.
    /// </summary>
    private static void SendGatherCommand(GatheringTask task)
    {
        switch (task.GatherType)
        {
            case GatherType.Miner:
                ChatIpc.GatherMiner(task.ItemName);
                break;
            case GatherType.Botanist:
                ChatIpc.GatherBotanist(task.ItemName);
                break;
            default:
                ChatIpc.GatherItem(task.ItemName);
                break;
        }
    }

    private void StartCurrentTask()
    {
        var task = CurrentTask;
        if (task == null) return;

        task.Status = GatheringTaskStatus.InProgress;
        taskStartTime = DateTime.Now;
        lastProgressTime = DateTime.Now;
        lastKnownCount = -1; // Will be seeded from actual inventory on first Update poll
        gbrIdlePolls = 0;
        consecutiveReEnableFailures = 0;
        lastAutoGatherCheck = DateTime.MinValue;
        finishingNodeSince = null;

        // Check if this item was skipped during list injection (not in GBR's Gatherables).
        // Common for crystals/shards/clusters which GBR indexes differently.
        commandOnlyMode = ipc.GatherBuddyLists.SkippedItemIds.Contains(task.ItemId);
        lastCommandReissueTime = DateTime.MinValue;

        if (commandOnlyMode)
        {
            DalamudApi.Log.Information($"Item {task.ItemName} not in GBR auto-gather list — using command-only gathering mode.");
        }

        // Don't fire gather commands while the player is occupied —
        // the Update loop will re-enable once the player is free.
        if (IsPlayerOccupied())
        {
            DalamudApi.Log.Information($"Player occupied, deferring gather command for {task.ItemName}.");
            StatusMessage = $"Waiting to gather {task.ItemName} (player busy)...";
            return;
        }

        // Send the gather command to hint GBR and trigger teleport.
        // In command-only mode, use the generic /gather to let GBR pick the best class.
        if (commandOnlyMode)
            ChatIpc.GatherItem(task.ItemName);
        else
            SendGatherCommand(task);

        if (task.IsCollectable)
        {
            ChatIpc.StartCollectableGathering();
        }

        // Enable AutoGather — with items in the injected list, GBR will
        // automatically path to nodes and gather via vnavmesh.
        // In command-only mode, also enable it: the /gather command sets GBR's
        // target, and AutoGather keeps it running after the first node.
        ipc.GatherBuddy.SetAutoGatherEnabled(true);

        lastCommandReissueTime = DateTime.Now;
        StatusMessage = $"Gathering {task.ItemName} (need {task.QuantityRemaining} more)...";
        DalamudApi.Log.Information(StatusMessage);
    }

    private void AdvanceToNextTask()
    {
        // Stop collectable gathering if previous task was collectable
        var prevTask = CurrentTask;
        if (prevTask is { IsCollectable: true })
        {
            ChatIpc.StopCollectableGathering();
        }

        // Disable auto gather before switching targets
        ipc.GatherBuddy.SetAutoGatherEnabled(false);

        currentTaskIndex++;
        if (currentTaskIndex >= taskQueue.Count)
        {
            CleanupGatherList();
            State = GatheringOrchestratorState.Completed;
            StatusMessage = "All gathering tasks complete.";
            DalamudApi.Log.Information(StatusMessage);
            return;
        }

        // Small delay before starting next task (let GBR settle).
        // Uses a timestamp check in Update() instead of Task.Run to avoid async state machine allocation.
        interTaskDelayUntil = DateTime.Now.AddSeconds(2.0);
    }

    private void OnGbrWaiting()
    {
        var task = CurrentTask;
        var statusText = ipc.GatherBuddy.GetStatusText();
        DalamudApi.Log.Information($"GBR AutoGather entered waiting state. " +
            $"Current task: {task?.ItemName ?? "none"}, " +
            $"GBR status: {(string.IsNullOrEmpty(statusText) ? "unknown" : statusText)}");
    }

    private void OnGbrEnabledChanged(bool enabled)
    {
        var task = CurrentTask;
        if (!enabled && State == GatheringOrchestratorState.Running && task != null)
        {
            DalamudApi.Log.Warning($"GBR AutoGather was disabled externally while gathering " +
                $"{task.ItemName} ({task.QuantityGathered}/{task.QuantityNeeded}). Will re-enable on next poll.");
        }
        else
        {
            DalamudApi.Log.Debug($"GBR AutoGather enabled changed: {enabled}");
        }
    }
}

public enum GatheringOrchestratorState
{
    Idle,
    Ready,
    Running,
    Completed,
    Error,
}
