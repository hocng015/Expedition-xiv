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
            optimized = scheduled.Select(s => s.Task).ToList();
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

        var task = CurrentTask;
        if (task == null)
        {
            State = GatheringOrchestratorState.Completed;
            return;
        }

        // Check inventory to see if we've gathered enough
        var currentCount = inventoryManager.GetItemCount(task.ItemId);
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

        if (!gbrEnabled)
        {
            gbrIdlePolls++;

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

                // Re-send gather command and re-enable AutoGather
                DalamudApi.Log.Information($"GBR AutoGather is OFF — re-enabling for {task.ItemName} (need {task.QuantityRemaining} more)");
                SendGatherCommand(task);
                ipc.GatherBuddy.SetAutoGatherEnabled(true);
            }
        }
        else
        {
            gbrIdlePolls = 0;
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
        var gbrStatus = !gbrEnabled ? " [GBR: OFF — re-enabling]" :
                        gbrWaiting  ? " [GBR: Waiting]" : "";

        if (task.IsTimedNode && task.SpawnHours != null)
        {
            var isActive = task.SpawnHours.Any(h => EorzeanTime.IsWithinWindow(h, 2));
            if (isActive)
                StatusMessage = $"Gathering {task.ItemName}: {task.QuantityGathered}/{task.QuantityNeeded} (timed node ACTIVE){gbrStatus}";
            else
            {
                var nextSpawn = task.SpawnHours.Min(h => EorzeanTime.SecondsUntilEorzeanHour(h));
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
    /// </summary>
    public bool HasFailures => taskQueue.Any(t => t.Status == GatheringTaskStatus.Failed);

    /// <summary>
    /// Injects all queued items into GBR's auto-gather list via reflection.
    /// This is what enables GBR's AutoGather to know what to gather.
    /// </summary>
    private void InjectGatherList()
    {
        var items = taskQueue
            .Where(t => t.Status != GatheringTaskStatus.Completed && t.Status != GatheringTaskStatus.Failed)
            .Select(t => (t.ItemId, (uint)t.QuantityRemaining))
            .ToList();

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
        lastAutoGatherCheck = DateTime.MinValue;
        finishingNodeSince = null;

        // Don't fire gather commands while the player is occupied —
        // the Update loop will re-enable once the player is free.
        if (IsPlayerOccupied())
        {
            DalamudApi.Log.Information($"Player occupied, deferring gather command for {task.ItemName}.");
            StatusMessage = $"Waiting to gather {task.ItemName} (player busy)...";
            return;
        }

        // Send the class-specific gather command to hint GBR and trigger teleport
        SendGatherCommand(task);

        if (task.IsCollectable)
        {
            ChatIpc.StartCollectableGathering();
        }

        // Enable AutoGather — with items in the injected list, GBR will
        // automatically path to nodes and gather via vnavmesh.
        ipc.GatherBuddy.SetAutoGatherEnabled(true);

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

        // Small delay before starting next task (let GBR settle)
        Task.Run(async () =>
        {
            await Task.Delay(2000);
            StartCurrentTask();
        });
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
