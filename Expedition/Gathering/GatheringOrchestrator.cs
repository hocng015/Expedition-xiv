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

    // --- Count-Delta Detection ---

    /// <summary>Inventory count at the start of the current task (snapshot baseline).</summary>
    private int baselineCount;

    /// <summary>Cached slot tracker for the current task's item.</summary>
    private readonly Inventory.InventoryManager.ItemSlotCache slotCache = new();

    /// <summary>When the count-delta last changed (inventory increased).</summary>
    private DateTime lastDeltaChangeTime = DateTime.MinValue;

    /// <summary>
    /// When we first detected no delta change while GBR is idle.
    /// Used for the soft "no-delta" timeout (30s default).
    /// </summary>
    private DateTime? noDeltaTimeoutStart;

    /// <summary>
    /// Timestamp of the last inventory event we consumed from InventoryManager.
    /// Used to detect if an event arrived between polls.
    /// </summary>
    private DateTime lastConsumedEventTime = DateTime.MinValue;

    /// <summary>
    /// True when the current task's item couldn't be added to GBR's auto-gather list
    /// (e.g., crystals not in GBR's Gatherables dictionary). In this mode we rely on
    /// periodic /gather commands instead of AutoGather list-driven gathering.
    /// </summary>
    private bool commandOnlyMode;

    /// <summary>
    /// Last snapshot of GBR's AutoGather state taken when AutoGather was disabled.
    /// Used to make targeted recovery decisions instead of blind re-enable attempts.
    /// </summary>
    private GbrStateTracker.GbrDisableSnapshot? lastDisableSnapshot;

    /// <summary>
    /// How often to re-issue the /gather command in command-only mode.
    /// GBR's /gather navigates to the item and gathers one node cycle,
    /// so we need to periodically re-send it for continued gathering.
    /// </summary>
    private const double CommandReissueIntervalSeconds = 30.0;

    /// <summary>When we last sent a /gather command in command-only mode.</summary>
    private DateTime lastCommandReissueTime = DateTime.MinValue;

    /// <summary>
    /// How many consecutive times GBR immediately disabled after a command-only re-enable.
    /// Reset to 0 when GBR stays enabled (accepted the command).
    /// </summary>
    private int cmdOnlyConsecutiveRefusals;

    /// <summary>
    /// After this many consecutive refusals in command-only mode, give up and trigger retry/fail.
    /// </summary>
    private const int MaxCmdOnlyRefusals = 3;

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
    /// Tracks how many full reset cycles we've done for the current task
    /// without any inventory progress. After too many, the task is stuck and we should advance.
    /// </summary>
    private int resetCyclesWithoutProgress;

    /// <summary>
    /// Polls since last periodic summary log. Used to emit a state dump every ~30 polls (30s).
    /// </summary>
    private int pollsSinceLastSummary;

    /// <summary>How often (in poll cycles) to emit a periodic state summary to the log.</summary>
    private const int SummaryLogInterval = 30;

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

        // Seed QuantityGathered from live inventory for accurate remaining counts.
        // This ensures the GBR inject list gets correct quantities, and
        // StartCurrentTask can skip tasks that are already complete.
        var includeSaddlebag = Expedition.Config.IncludeSaddlebagInScans;
        var inventoryManager = Expedition.Instance.InventoryManager;
        for (var i = 0; i < taskQueue.Count; i++)
        {
            var t = taskQueue[i];
            t.QuantityGathered = inventoryManager.GetItemCount(t.ItemId, includeSaddlebag: includeSaddlebag);
        }

        // Log full queue details for debugging class-switch issues
        for (var i = 0; i < taskQueue.Count; i++)
        {
            var t = taskQueue[i];
            DalamudApi.Log.Information(
                $"Gather queue [{i}]: {t.ItemName} (id={t.ItemId}) — " +
                $"have={t.QuantityGathered}, need={t.QuantityNeeded}, remaining={t.QuantityRemaining}, " +
                $"type={t.GatherType}, nodeLevel={t.GatherNodeLevel}, " +
                $"timed={t.IsTimedNode}, collectable={t.IsCollectable}");
        }

        // Pre-flight level check: skip tasks where the player's gathering level is
        // too low. GBR filters items by: node.Level <= (playerLevel + 5) / 5 * 5.
        // If we send GBR items it can't gather, it silently drops them and reports
        // ListExhausted, causing minutes of wasted retry/timeout cycles.
        SkipUngatherableTasks();

        // Inject all gather items into GBR's auto-gather list via reflection.
        // This also lazily initializes GatherBuddyListManager if needed.
        InjectGatherList();

        // Ensure the GBR state tracker is initialized AFTER list injection,
        // because GatherBuddyListManager.Initialize() runs lazily inside InjectGatherList()
        // and we need GbrPluginInstance to be set before we can probe AutoGather internals.
        if (!ipc.GbrStateTracker.IsInitialized && ipc.GatherBuddyLists.GbrPluginInstance != null)
        {
            ipc.GbrStateTracker.Initialize(ipc.GatherBuddyLists.GbrPluginInstance);
        }

        // Force-reset GBR's internal state AFTER injecting the new list.
        // This clears stale task queues, amiss counters, and gather targets from
        // previous sessions that would cause GBR to immediately reject the new list.
        if (ipc.GbrStateTracker.IsInitialized)
        {
            DalamudApi.Log.Information("[Gather:Start] Pre-start GBR force-reset to clear stale state.");
            ipc.GbrStateTracker.ForceReset(ipc.GatherBuddy);
        }

        State = GatheringOrchestratorState.Running;
        currentTaskIndex = 0;
        StartCurrentTask();
    }

    /// <summary>
    /// Stops all gathering, disables GBR AutoGather, and cleans up injected list.
    /// </summary>
    public void Stop()
    {
        var task = CurrentTask;
        if (State == GatheringOrchestratorState.Running)
        {
            DalamudApi.Log.Information(
                $"[Gather:Stop] Stopping gathering. Current task: {task?.ItemName ?? "none"}, " +
                $"count={task?.QuantityGathered ?? 0}/{task?.QuantityNeeded ?? 0}, " +
                $"taskIndex={currentTaskIndex}/{taskQueue.Count}, " +
                $"reEnableFails={consecutiveReEnableFailures}, resetCycles={resetCyclesWithoutProgress}");

            // Force-reset GBR's internal state to prevent stale corruption from
            // carrying over to subsequent gather sessions in the same game session.
            ipc.GbrStateTracker.ForceReset(ipc.GatherBuddy);
        }

        CleanupGatherList();
        slotCache.Clear();

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

        // Poll dependency readiness (time-throttled internally by DependencyMonitor)
        ipc.DependencyMonitor.Poll();

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

        // --- Count-Delta Detection ---
        // Use cached slot positions for fast reads. Fall back to full scan if cache returns -1.
        var includeSaddlebag = Expedition.Config.IncludeSaddlebagInScans;
        var cachedCount = slotCache.GetCachedCount(includeSaddlebag);
        var usedCache = cachedCount >= 0;
        var currentCount = usedCache
            ? cachedCount
            : inventoryManager.GetItemCount(task.ItemId, includeSaddlebag: includeSaddlebag);

        // Check if an inventory event arrived between polls (early signal).
        // If so, do a full rescan to get the authoritative count.
        var eventTriggered = false;
        if (inventoryManager.LastInventoryEventTime > lastConsumedEventTime)
        {
            lastConsumedEventTime = inventoryManager.LastInventoryEventTime;
            var eventCount = inventoryManager.GetItemCount(task.ItemId, includeSaddlebag: includeSaddlebag);
            if (eventCount != currentCount)
            {
                DalamudApi.Log.Debug($"[Gather:Event] Recount for {task.ItemName}: {currentCount} -> {eventCount} (cache={usedCache})");
                currentCount = eventCount;
                eventTriggered = true;
                // Reinitialize cache with fresh slot positions
                slotCache.Initialize(task.ItemId, includeSaddlebag);
            }
        }

        task.QuantityGathered = currentCount;
        pollsSinceLastSummary++;

        // Track progress — reset timers whenever inventory increases
        if (currentCount > lastKnownCount)
        {
            var delta = currentCount - baselineCount;
            DalamudApi.Log.Information(
                $"[Gather:Progress] {task.ItemName}: {lastKnownCount} -> {currentCount} " +
                $"(+{currentCount - lastKnownCount} this poll, total delta +{delta} from baseline {baselineCount}, " +
                $"target={task.QuantityNeeded}, remaining={task.QuantityNeeded - currentCount}, " +
                $"source={CountSource(usedCache, eventTriggered)})");
            lastKnownCount = currentCount;
            lastProgressTime = DateTime.Now;
            lastDeltaChangeTime = DateTime.Now;
            gbrIdlePolls = 0;
            noDeltaTimeoutStart = null; // Reset soft timeout
            resetCyclesWithoutProgress = 0; // Real progress — reset cycle counter
        }

        // --- Periodic summary log (every ~30s) for at-a-glance state ---
        if (pollsSinceLastSummary >= SummaryLogInterval)
        {
            pollsSinceLastSummary = 0;
            var gbrEnabledSummary = ipc.GatherBuddy.GetAutoGatherEnabled();
            var gbrWaitingSummary = ipc.GatherBuddy.GetAutoGatherWaiting();
            var secSinceDelta = (DateTime.Now - lastDeltaChangeTime).TotalSeconds;
            var secSinceProgress = (DateTime.Now - lastProgressTime).TotalSeconds;
            var elapsedTotal = (DateTime.Now - taskStartTime).TotalSeconds;
            var lastReason = lastDisableSnapshot?.Reason.ToString() ?? "none";
            DalamudApi.Log.Information(
                $"[Gather:Summary] {task.ItemName}: {currentCount}/{task.QuantityNeeded} " +
                $"(baseline={baselineCount}, delta=+{currentCount - baselineCount}) | " +
                $"GBR: enabled={gbrEnabledSummary}, waiting={gbrWaitingSummary}, lastReason={lastReason} | " +
                $"cmdOnly={commandOnlyMode}, reEnableFails={consecutiveReEnableFailures}, " +
                $"resetCycles={resetCyclesWithoutProgress}, idlePolls={gbrIdlePolls} | " +
                $"sinceLastDelta={secSinceDelta:F0}s, sinceProgress={secSinceProgress:F0}s, " +
                $"elapsed={elapsedTotal:F0}s, retries={task.RetryCount}/{Expedition.Config.GatherRetryLimit}");
        }

        // --- PRIMARY: Target quantity met ---
        if (task.IsComplete)
        {
            var elapsedSec = (DateTime.Now - taskStartTime).TotalSeconds;
            var totalDelta = currentCount - baselineCount;

            // Target quantity met — but wait for the player to finish the current node.
            // Walking away from a half-mined node is wasteful; let GBR exhaust it.
            var isAtNode = DalamudApi.Condition[ConditionFlag.Gathering]
                        || DalamudApi.Condition[ConditionFlag.ExecutingGatheringAction];

            if (isAtNode)
            {
                if (!finishingNodeSince.HasValue)
                {
                    finishingNodeSince = DateTime.Now;
                    DalamudApi.Log.Information(
                        $"[Gather:Complete] Target met for {task.ItemName} ({currentCount}/{task.QuantityNeeded}, " +
                        $"delta=+{totalDelta} in {elapsedSec:F0}s) — finishing current node.");
                }

                // Safety timeout so we don't wait forever
                var waitingSeconds = (DateTime.Now - finishingNodeSince.Value).TotalSeconds;
                if (waitingSeconds < FinishNodeTimeoutSeconds)
                {
                    StatusMessage = $"Gathered enough {task.ItemName} — finishing node ({task.QuantityGathered}/{task.QuantityNeeded})...";
                    return; // Keep waiting, let GBR finish the node
                }

                DalamudApi.Log.Information($"[Gather:Complete] Node finish timeout ({FinishNodeTimeoutSeconds}s) for {task.ItemName}, advancing.");
            }

            // Node is done (or we weren't at one, or timeout) — advance
            finishingNodeSince = null;
            task.Status = GatheringTaskStatus.Completed;
            StatusMessage = $"Gathered enough {task.ItemName}.";
            DalamudApi.Log.Information(
                $"[Gather:Complete] {task.ItemName} DONE: {currentCount}/{task.QuantityNeeded} " +
                $"(delta=+{totalDelta}, baseline={baselineCount}, elapsed={elapsedSec:F0}s, " +
                $"retries={task.RetryCount}, source={CountSource(usedCache, eventTriggered)})");
            AdvanceToNextTask();
            return;
        }

        // --- SECONDARY: No-delta timeout (soft) ---
        // If GBR is idle/off and no inventory change for GatherNoDeltaTimeoutSeconds,
        // do a full rescan to confirm, then trigger retry.
        var gbrEnabledForDelta = ipc.GatherBuddy.GetAutoGatherEnabled();
        var gbrWaitingForDelta = ipc.GatherBuddy.GetAutoGatherWaiting();
        var gbrIdle = !gbrEnabledForDelta || gbrWaitingForDelta;
        var noDeltaTimeout = Expedition.Config.GatherNoDeltaTimeoutSeconds;
        var absoluteTimeout = Expedition.Config.GatherAbsoluteTimeoutSeconds;
        var secondsSinceDelta = (DateTime.Now - lastDeltaChangeTime).TotalSeconds;

        if (gbrIdle && secondsSinceDelta > noDeltaTimeout && noDeltaTimeout > 0)
        {
            if (!noDeltaTimeoutStart.HasValue)
            {
                // First detection — do a full authoritative rescan to confirm
                noDeltaTimeoutStart = DateTime.Now;
                DalamudApi.Log.Information(
                    $"[Gather:NoDelta] Soft timeout triggered for {task.ItemName} after {secondsSinceDelta:F0}s. " +
                    $"GBR: enabled={gbrEnabledForDelta}, waiting={gbrWaitingForDelta}. " +
                    $"Running full inventory rescan to confirm count...");
                var fullCount = inventoryManager.GetItemCount(task.ItemId, includeSaddlebag: includeSaddlebag);
                if (fullCount != currentCount)
                {
                    DalamudApi.Log.Information(
                        $"[Gather:NoDelta] Rescan corrected count for {task.ItemName}: cached={currentCount} -> full={fullCount} " +
                        $"(delta={fullCount - currentCount}). Cache was stale.");
                    task.QuantityGathered = fullCount;
                    lastKnownCount = fullCount;
                    if (fullCount > currentCount)
                    {
                        lastDeltaChangeTime = DateTime.Now;
                        noDeltaTimeoutStart = null;
                    }
                    // Reinitialize cache
                    slotCache.Initialize(task.ItemId, includeSaddlebag);
                    // Re-check completion with corrected count
                    if (task.IsComplete)
                    {
                        finishingNodeSince = null;
                        task.Status = GatheringTaskStatus.Completed;
                        StatusMessage = $"Gathered enough {task.ItemName} (confirmed by rescan).";
                        DalamudApi.Log.Information(
                            $"[Gather:NoDelta] Rescan confirmed completion for {task.ItemName}: " +
                            $"{fullCount}/{task.QuantityNeeded}");
                        AdvanceToNextTask();
                        return;
                    }
                }
                else
                {
                    DalamudApi.Log.Information(
                        $"[Gather:NoDelta] Rescan confirmed no change for {task.ItemName}. " +
                        $"Count: {currentCount}/{task.QuantityNeeded}, delta from baseline: +{currentCount - baselineCount}, " +
                        $"absoluteTimeout in {absoluteTimeout - secondsSinceDelta:F0}s");
                }
            }
        }
        else if (!gbrIdle)
        {
            // GBR is actively running — reset the soft timeout
            if (noDeltaTimeoutStart.HasValue)
            {
                DalamudApi.Log.Debug($"[Gather:NoDelta] GBR became active, resetting soft timeout for {task.ItemName}.");
            }
            noDeltaTimeoutStart = null;
        }

        // --- TERTIARY: Absolute no-delta timeout (hard) ---
        if (secondsSinceDelta > absoluteTimeout && absoluteTimeout > 0)
        {
            var elapsedSec = (DateTime.Now - taskStartTime).TotalSeconds;
            DalamudApi.Log.Warning(
                $"[Gather:HardTimeout] Absolute no-delta timeout ({absoluteTimeout}s) for {task.ItemName}. " +
                $"Count: {currentCount}/{task.QuantityNeeded}, baseline={baselineCount}, delta=+{currentCount - baselineCount}, " +
                $"elapsed={elapsedSec:F0}s, GBR: enabled={gbrEnabledForDelta}, waiting={gbrWaitingForDelta}, " +
                $"idlePolls={gbrIdlePolls}, reEnableFails={consecutiveReEnableFailures}, " +
                $"resetCycles={resetCyclesWithoutProgress}. Triggering retry.");
            noDeltaTimeoutStart = null;

            task.RetryCount++;
            if (task.RetryCount > Expedition.Config.GatherRetryLimit)
            {
                task.Status = GatheringTaskStatus.Failed;
                task.ErrorMessage = $"No gathering progress for {absoluteTimeout}s after {task.RetryCount} attempts.";
                DalamudApi.Log.Warning(
                    $"[Gather:Failed] {task.ItemName} FAILED: {task.ErrorMessage} " +
                    $"(count={currentCount}/{task.QuantityNeeded}, delta=+{currentCount - baselineCount})");
                AdvanceToNextTask();
            }
            else
            {
                DalamudApi.Log.Information(
                    $"[Gather:Retry] Stalled {task.ItemName}, retrying (attempt {task.RetryCount}/{Expedition.Config.GatherRetryLimit})");
                StartCurrentTask();
            }
            return;
        }

        // Monitor GBR state — re-enable AutoGather if it disabled itself
        var gbrEnabled = ipc.GatherBuddy.GetAutoGatherEnabled();
        var gbrWaiting = ipc.GatherBuddy.GetAutoGatherWaiting();
        var playerOccupied = IsPlayerOccupied();

        if (commandOnlyMode)
        {
            // Command-only mode: item wasn't in GBR's auto-gather list (or AutoGather
            // refused it and we escalated). We drive GBR via /gather commands instead.
            if (!playerOccupied)
            {
                var sinceLastCommand = (DateTime.Now - lastCommandReissueTime).TotalSeconds;

                if (!gbrEnabled && sinceLastCommand >= AutoGatherReEnableIntervalSeconds)
                {
                    // Track how many times GBR immediately refuses commands
                    cmdOnlyConsecutiveRefusals++;

                    if (cmdOnlyConsecutiveRefusals > MaxCmdOnlyRefusals)
                    {
                        // GBR fundamentally cannot gather this item. Stop spamming commands
                        // and let the no-delta / absolute timeout advance the task.
                        var cmdElapsed = (DateTime.Now - taskStartTime).TotalSeconds;
                        DalamudApi.Log.Warning(
                            $"[Gather:CmdOnly:GaveUp] GBR refused {cmdOnlyConsecutiveRefusals} consecutive commands " +
                            $"for {task.ItemName} ({task.QuantityGathered}/{task.QuantityNeeded}). " +
                            $"Reason: {lastDisableSnapshot?.Reason.ToString() ?? "n/a"}. " +
                            $"Giving up — triggering retry/fail. Elapsed: {cmdElapsed:F0}s");
                        StatusMessage = $"GBR cannot gather {task.ItemName} — waiting for timeout to advance...";

                        // Trigger retry/fail immediately instead of waiting for the full timeout
                        task.RetryCount++;
                        if (task.RetryCount > Expedition.Config.GatherRetryLimit)
                        {
                            task.Status = GatheringTaskStatus.Failed;
                            task.ErrorMessage = $"GBR refuses to gather {task.ItemName} (both list and command modes failed).";
                            DalamudApi.Log.Warning(
                                $"[Gather:Failed] {task.ItemName} FAILED after {task.RetryCount} attempts: {task.ErrorMessage}");
                            AdvanceToNextTask();
                        }
                        else
                        {
                            DalamudApi.Log.Information(
                                $"[Gather:Retry] {task.ItemName} retry {task.RetryCount}/{Expedition.Config.GatherRetryLimit} " +
                                $"— GBR command-only mode exhausted.");
                            StartCurrentTask();
                        }
                        return;
                    }

                    DalamudApi.Log.Information(
                        $"[Gather:CmdOnly] GBR OFF — re-issuing gather command for {task.ItemName} " +
                        $"(need {task.QuantityRemaining} more, refusal {cmdOnlyConsecutiveRefusals}/{MaxCmdOnlyRefusals})");
                    SendGatherCommand(task);
                    ipc.GatherBuddy.SetAutoGatherEnabled(true);
                    lastCommandReissueTime = DateTime.Now;
                }
                else if (gbrEnabled)
                {
                    // GBR accepted the command and is running — reset refusal counter
                    cmdOnlyConsecutiveRefusals = 0;

                    if (sinceLastCommand >= CommandReissueIntervalSeconds)
                    {
                        // Periodically re-issue /gather to keep GBR targeted on the right item
                        // (GBR may finish a node cycle and go idle without an auto-gather list entry)
                        if (gbrWaiting)
                        {
                            DalamudApi.Log.Debug($"[Gather:CmdOnly] GBR waiting, re-issuing gather command for {task.ItemName}");
                            SendGatherCommand(task);
                            lastCommandReissueTime = DateTime.Now;
                        }
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

            var disableReason = lastDisableSnapshot?.Reason.ToString() ?? "n/a";
            DalamudApi.Log.Debug(
                $"[Gather:GBR] AutoGather OFF for {task.ItemName}: " +
                $"reason={disableReason}, idlePolls={gbrIdlePolls}, " +
                $"reEnableFails={consecutiveReEnableFailures}/{MaxReEnableFailuresBeforeReset}, " +
                $"resetCycles={resetCyclesWithoutProgress}/3, playerOccupied={playerOccupied}");

            // Don't try to re-enable while the player is in a blocking state (crafting, cutscene, etc.)
            if (playerOccupied)
            {
                StatusMessage = $"Waiting for player to be free before gathering {task.ItemName}...";
                return;
            }

            // --- Reason-aware early exits ---
            // Use the snapshot captured in OnGbrEnabledChanged to make targeted recovery decisions
            // instead of blindly re-enabling. These are "terminal" reasons that won't resolve by retrying.
            if (lastDisableSnapshot != null)
            {
                var reason = lastDisableSnapshot.Reason;

                if (reason == GbrDisableReason.UserDisabled)
                {
                    // User manually turned off AutoGather — respect their intent.
                    DalamudApi.Log.Warning(
                        $"[Gather:GBR:UserStop] User disabled AutoGather while gathering {task.ItemName}. Stopping.");
                    Stop();
                    return;
                }

                if (reason == GbrDisableReason.InventoryFull)
                {
                    // Inventory full — retrying won't help. Fail the task with a clear message.
                    task.Status = GatheringTaskStatus.Failed;
                    task.ErrorMessage = "Inventory full — make space and retry.";
                    DalamudApi.Log.Warning(
                        $"[Gather:GBR:InvFull] {task.ItemName} FAILED: {task.ErrorMessage} " +
                        $"(count={currentCount}/{task.QuantityNeeded})");
                    lastDisableSnapshot = null;
                    AdvanceToNextTask();
                    return;
                }

                if (reason == GbrDisableReason.MissingPlugin || reason == GbrDisableReason.QuestIncomplete)
                {
                    // Missing dependency or quest — can't be fixed at runtime.
                    task.Status = GatheringTaskStatus.Failed;
                    task.ErrorMessage = $"GBR: {lastDisableSnapshot.AutoStatus}";
                    DalamudApi.Log.Warning(
                        $"[Gather:GBR:Prereq] {task.ItemName} FAILED: {task.ErrorMessage}");
                    lastDisableSnapshot = null;
                    AdvanceToNextTask();
                    return;
                }

                // AmissAtNode — GBR repeatedly failing at nodes. Skip the slow 3-reset-cycle
                // escalation and switch to command-only mode immediately.
                // Don't re-enable AutoGather — let the command-only loop handle it.
                if (reason == GbrDisableReason.AmissAtNode && !commandOnlyMode)
                {
                    DalamudApi.Log.Warning(
                        $"[Gather:GBR:Amiss] {task.ItemName}: GBR amiss at node (count={lastDisableSnapshot.ConsecutiveAmissCount}). " +
                        $"Switching to command-only mode immediately (no AutoGather re-enable).");
                    commandOnlyMode = true;
                    consecutiveReEnableFailures = 0;
                    resetCyclesWithoutProgress = 0;
                    lastCommandReissueTime = DateTime.MinValue;
                    cmdOnlyConsecutiveRefusals = 0;
                    CleanupGatherList();
                    SendGatherCommand(task);
                    // Don't re-enable AutoGather — the command-only loop will handle it
                    lastDisableSnapshot = null;
                    return;
                }

                // ListExhausted — GBR thinks there's nothing to gather, but we still need items.
                // GBR's internal quantity tracking disagrees with ours. Re-injecting the list
                // won't help because GBR will re-check its own inventory and reach the same
                // conclusion. Switch to command-only mode immediately — /gather commands
                // bypass the list system and directly target nodes.
                // IMPORTANT: Do NOT re-enable AutoGather here — there's no list, so GBR would
                // immediately disable again, triggering the refusal counter and killing the task.
                if (reason == GbrDisableReason.ListExhausted && !commandOnlyMode)
                {
                    DalamudApi.Log.Warning(
                        $"[Gather:GBR:ListExhausted] {task.ItemName}: GBR list reports exhausted " +
                        $"(hasItems=False) but we still need {task.QuantityRemaining} more. " +
                        $"GBR's quantity tracking disagrees — switching to command-only mode (no AutoGather re-enable).");
                    commandOnlyMode = true;
                    consecutiveReEnableFailures = 0;
                    resetCyclesWithoutProgress = 0;
                    lastCommandReissueTime = DateTime.MinValue;
                    cmdOnlyConsecutiveRefusals = 0;
                    CleanupGatherList();
                    SendGatherCommand(task);
                    // Don't re-enable AutoGather — the command-only loop will handle it
                    lastDisableSnapshot = null;
                    return;
                }
            }

            // --- Dependency-aware re-enable ---
            // Before blindly re-enabling, check if dependencies are actually ready.
            // This prevents wasted retries when vnavmesh is rebuilding or GBR is unavailable.
            var depSnapshot = ipc.DependencyMonitor.GetSnapshot();

            if (!depSnapshot.GbrAvailable)
            {
                // GBR IPC itself is gone — force a fresh availability check
                ipc.GatherBuddy.CheckAvailability();
                StatusMessage = $"Waiting for GatherBuddy Reborn... [{task.ItemName}]";
                DalamudApi.Log.Debug($"[Gather:Dep] GBR IPC unavailable, waiting. reEnableFails -> {consecutiveReEnableFailures - 1}");
                // Don't count this as a re-enable failure — it's a dependency issue
                consecutiveReEnableFailures = Math.Max(0, consecutiveReEnableFailures - 1);
                lastProgressTime = DateTime.Now; // Don't stall-timeout while waiting for deps
                return;
            }

            if (depSnapshot.VnavmeshAvailable && !depSnapshot.NavReady)
            {
                // vnavmesh is building — don't re-enable, just wait
                var blockReason = depSnapshot.GetBlockReason() ?? "vnavmesh not ready";
                StatusMessage = $"Waiting: {blockReason} [{task.ItemName}]";
                DalamudApi.Log.Debug($"[Gather:Dep] Deferring GBR re-enable: {blockReason} (progress={depSnapshot.NavBuildProgress:P0})");
                // Don't count navmesh rebuilds as re-enable failures
                consecutiveReEnableFailures = Math.Max(0, consecutiveReEnableFailures - 1);
                lastProgressTime = DateTime.Now; // Don't stall-timeout while nav is building
                return;
            }

            // Dependencies are ready — proceed with re-enable logic
            var sinceLastReEnable = (DateTime.Now - lastAutoGatherCheck).TotalSeconds;

            if (sinceLastReEnable >= AutoGatherReEnableIntervalSeconds)
            {
                lastAutoGatherCheck = DateTime.Now;

                // If GBR keeps disabling itself immediately after re-enable, its internal state
                // is likely corrupted (e.g. stale TaskManager tasks from a Dalamud plugin reload).
                // Perform a full reset cycle: disable, re-inject the gather list, then re-enable.
                if (consecutiveReEnableFailures > MaxReEnableFailuresBeforeReset)
                {
                    resetCyclesWithoutProgress++;

                    // If we've done multiple full reset cycles with zero inventory progress,
                    // GBR's AutoGather fundamentally cannot handle this item (common for crystals/clusters).
                    // Escalation strategy:
                    //   1st escalation (cycle 3): switch to command-only mode instead of AutoGather list
                    //   2nd escalation (cycle 6): give up and trigger retry/fail
                    if (resetCyclesWithoutProgress >= 3 && !commandOnlyMode)
                    {
                        // Switch to command-only mode — GBR's AutoGather list doesn't work for this item,
                        // but /gather commands may still work by directly targeting nodes.
                        DalamudApi.Log.Warning(
                            $"[Gather:ResetEscalation] {resetCyclesWithoutProgress} reset cycles with no progress " +
                            $"for {task.ItemName} ({task.QuantityGathered}/{task.QuantityNeeded}). " +
                            $"AutoGather list-driven gathering failed — switching to command-only mode.");
                        commandOnlyMode = true;
                        resetCyclesWithoutProgress = 0;
                        consecutiveReEnableFailures = 0;
                        lastCommandReissueTime = DateTime.MinValue;

                        // Clean up the injected list entry since we're abandoning list-driven mode
                        CleanupGatherList();

                        // Issue a /gather command and re-enable
                        SendGatherCommand(task);
                        ipc.GatherBuddy.SetAutoGatherEnabled(true);
                        return;
                    }
                    else if (resetCyclesWithoutProgress >= 3)
                    {
                        // Already in command-only mode and still no progress — truly stuck
                        DalamudApi.Log.Warning(
                            $"[Gather:ResetEscalation] {resetCyclesWithoutProgress} reset cycles with no progress " +
                            $"for {task.ItemName} in command-only mode ({task.QuantityGathered}/{task.QuantityNeeded}). " +
                            $"Baseline={baselineCount}, delta=+{currentCount - baselineCount}. Triggering retry/fail.");
                        resetCyclesWithoutProgress = 0;
                        consecutiveReEnableFailures = 0;

                        task.RetryCount++;
                        if (task.RetryCount > Expedition.Config.GatherRetryLimit)
                        {
                            task.Status = GatheringTaskStatus.Failed;
                            task.ErrorMessage = $"GBR repeatedly refused to gather {task.ItemName} after {task.RetryCount} attempts.";
                            DalamudApi.Log.Warning(
                                $"[Gather:Failed] {task.ItemName} FAILED: {task.ErrorMessage} " +
                                $"(count={currentCount}/{task.QuantityNeeded})");
                            AdvanceToNextTask();
                        }
                        else
                        {
                            DalamudApi.Log.Information(
                                $"[Gather:Retry] Reset cycle escalation for {task.ItemName}, " +
                                $"retrying (attempt {task.RetryCount}/{Expedition.Config.GatherRetryLimit})");
                            StartCurrentTask();
                        }
                        return;
                    }

                    DalamudApi.Log.Warning(
                        $"[Gather:ResetCycle] {consecutiveReEnableFailures} consecutive re-enable failures for {task.ItemName}. " +
                        $"Full reset cycle {resetCyclesWithoutProgress}/3: force-reset → re-inject list → re-enable. " +
                        $"Count: {currentCount}/{task.QuantityNeeded}, delta=+{currentCount - baselineCount}");

                    // Force-reset GBR's internal AutoGather state via reflection.
                    // This clears stale task queues, amiss counters, stuck timers, and
                    // gather targets — equivalent to a soft restart of AutoGather.
                    ipc.GbrStateTracker.ForceReset(ipc.GatherBuddy);

                    // Re-inject the gather list from scratch
                    CleanupGatherList();
                    InjectGatherList();

                    // Re-send the gather command and re-enable
                    SendGatherCommand(task);
                    ipc.GatherBuddy.SetAutoGatherEnabled(true);

                    DalamudApi.Log.Debug($"[Gather:ResetCycle] Force-reset complete, AutoGather re-enabled. reEnableFails reset to 0.");
                    // Reset the counter — give the fresh cycle a chance
                    consecutiveReEnableFailures = 0;
                    return;
                }

                // Normal re-enable attempt
                DalamudApi.Log.Information(
                    $"[Gather:ReEnable] GBR OFF — re-enabling for {task.ItemName} " +
                    $"(reason={disableReason}, need {task.QuantityRemaining} more, " +
                    $"attempt {consecutiveReEnableFailures}/{MaxReEnableFailuresBeforeReset}, " +
                    $"count={currentCount}/{task.QuantityNeeded}, sinceLastDelta={secondsSinceDelta:F0}s)");
                SendGatherCommand(task);
                ipc.GatherBuddy.SetAutoGatherEnabled(true);
                lastDisableSnapshot = null; // Consumed
            }
            else
            {
                DalamudApi.Log.Debug(
                    $"[Gather:GBR] Throttled re-enable for {task.ItemName}: {sinceLastReEnable:F0}s since last attempt " +
                    $"(interval={AutoGatherReEnableIntervalSeconds}s)");
            }
        }
        else
        {
            gbrIdlePolls = 0;
            // GBR is enabled and running — reset the failure counter
            if (consecutiveReEnableFailures > 0)
            {
                DalamudApi.Log.Information(
                    $"[Gather:GBR] AutoGather confirmed running after {consecutiveReEnableFailures} failures for {task.ItemName}.");
                consecutiveReEnableFailures = 0;
            }
        }

        // Check for stall: no inventory progress for a long time
        var secondsSinceProgress = (DateTime.Now - lastProgressTime).TotalSeconds;
        if (secondsSinceProgress > StallTimeoutSeconds)
        {
            var elapsedSec = (DateTime.Now - taskStartTime).TotalSeconds;
            DalamudApi.Log.Warning(
                $"[Gather:StallTimeout] No inventory progress for {StallTimeoutSeconds / 60:F0}min for {task.ItemName}. " +
                $"Count: {currentCount}/{task.QuantityNeeded}, baseline={baselineCount}, delta=+{currentCount - baselineCount}, " +
                $"elapsed={elapsedSec:F0}s, GBR: enabled={gbrEnabled}, waiting={gbrWaiting}, " +
                $"reEnableFails={consecutiveReEnableFailures}, resetCycles={resetCyclesWithoutProgress}");

            task.RetryCount++;
            if (task.RetryCount > Expedition.Config.GatherRetryLimit)
            {
                task.Status = GatheringTaskStatus.Failed;
                task.ErrorMessage = $"No gathering progress for {StallTimeoutSeconds / 60:F0} minutes after {task.RetryCount} attempts.";
                DalamudApi.Log.Warning(
                    $"[Gather:Failed] {task.ItemName} FAILED after stall: {task.ErrorMessage}");
                AdvanceToNextTask();
            }
            else
            {
                // Retry: re-send the gather command
                DalamudApi.Log.Information(
                    $"[Gather:Retry] Stall timeout for {task.ItemName}, retrying " +
                    $"(attempt {task.RetryCount}/{Expedition.Config.GatherRetryLimit})");
                StartCurrentTask();
            }
            return;
        }

        // Update status message with GBR state and dependency info
        var elapsed = (DateTime.Now - taskStartTime).TotalSeconds;
        var cmdTag = commandOnlyMode ? " [cmd]" : "";
        var depTag = "";
        {
            var depSnap = ipc.DependencyMonitor.GetSnapshot();
            if (depSnap.VnavmeshAvailable && !depSnap.NavReady)
                depTag = $" [Nav: building {depSnap.NavBuildProgress:P0}]";
            else if (!depSnap.GbrAvailable)
                depTag = " [GBR: unavailable]";
        }
        var gbrStatus = !gbrEnabled ? $" [GBR: OFF — re-enabling]{cmdTag}{depTag}" :
                        gbrWaiting  ? $" [GBR: Waiting]{cmdTag}{depTag}" : $"{cmdTag}{depTag}";

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
    /// Returns true if any tasks were skipped (e.g., due to insufficient gathering level).
    /// </summary>
    public bool HasSkippedTasks
    {
        get
        {
            for (var i = 0; i < taskQueue.Count; i++)
                if (taskQueue[i].Status == GatheringTaskStatus.Skipped) return true;
            return false;
        }
    }

    /// <summary>
    /// Injects all queued items into GBR's auto-gather list via reflection.
    /// This is what enables GBR's AutoGather to know what to gather.
    ///
    /// IMPORTANT: GBR treats list quantities as ABSOLUTE inventory targets, not deltas.
    /// It checks: InventoryManager.GetInventoryItemCount(itemId) &lt; quantity.
    /// If the player already has >= quantity, GBR considers the item "done" and skips it.
    /// So we must inject (currentInventory + remaining) as the target, not just remaining.
    /// </summary>
    private void InjectGatherList()
    {
        var inventoryManager = Expedition.Instance.InventoryManager;
        var includeSaddlebag = Expedition.Config.IncludeSaddlebagInScans;
        var items = new List<(uint, uint)>(taskQueue.Count);
        for (var i = 0; i < taskQueue.Count; i++)
        {
            var t = taskQueue[i];
            if (t.Status != GatheringTaskStatus.Completed && t.Status != GatheringTaskStatus.Failed
                && t.Status != GatheringTaskStatus.Skipped)
            {
                // GBR uses absolute inventory targets: it checks GetInventoryItemCount(itemId) < quantity.
                // We need to inject (currentCount + remaining) so GBR sees there's still work to do.
                // Note: GBR only checks main inventory (not saddlebag), so we use the same source.
                var currentInInventory = (uint)inventoryManager.GetItemCount(t.ItemId, includeSaddlebag: false);
                var absoluteTarget = currentInInventory + (uint)t.QuantityRemaining;
                items.Add((t.ItemId, absoluteTarget));

                DalamudApi.Log.Debug(
                    $"[Gather:Inject] {t.ItemName} (id={t.ItemId}): " +
                    $"inInventory={currentInInventory}, remaining={t.QuantityRemaining}, " +
                    $"absoluteTarget={absoluteTarget} (GBR needs inv < target to gather)");
            }
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

    /// <summary>Formats the count source for logging (cache/fullScan/event).</summary>
    private static string CountSource(bool usedCache, bool eventTriggered)
        => eventTriggered ? "event-rescan" : usedCache ? "slot-cache" : "full-scan";

    /// <summary>
    /// Pre-flight check: marks tasks as Skipped when the player's gathering level
    /// is too low to access the required nodes.
    ///
    /// GBR filters items by: node.Level &lt;= (playerLevel + 5) / 5 * 5.
    /// If all of a task's nodes exceed this threshold, GBR will silently drop
    /// the item from _gatherableItems, causing ListExhausted and wasted retries.
    /// </summary>
    private void SkipUngatherableTasks()
    {
        try
        {
            var minerLevel = PlayerState.JobSwitchManager.GetPlayerJobLevel(PlayerState.JobSwitchManager.MIN);
            var botanistLevel = PlayerState.JobSwitchManager.GetPlayerJobLevel(PlayerState.JobSwitchManager.BTN);
            var fisherLevel = PlayerState.JobSwitchManager.GetPlayerJobLevel(PlayerState.JobSwitchManager.FSH);

            if (minerLevel < 0 && botanistLevel < 0 && fisherLevel < 0)
            {
                DalamudApi.Log.Debug("[Gather:LevelCheck] Player levels unavailable — skipping pre-flight level check.");
                return;
            }

            // Treat unreadable levels as 0 for safety (will skip items requiring that class)
            if (minerLevel < 0) minerLevel = 0;
            if (botanistLevel < 0) botanistLevel = 0;
            if (fisherLevel < 0) fisherLevel = 0;

            // GBR's threshold formula: rounds up to next multiple of 5
            var minerThreshold = (minerLevel + 5) / 5 * 5;
            var botanistThreshold = (botanistLevel + 5) / 5 * 5;
            var fisherThreshold = (fisherLevel + 5) / 5 * 5;

            DalamudApi.Log.Information(
                $"[Gather:LevelCheck] Player gathering levels: " +
                $"MIN={minerLevel} (threshold={minerThreshold}), " +
                $"BTN={botanistLevel} (threshold={botanistThreshold}), " +
                $"FSH={fisherLevel} (threshold={fisherThreshold})");

            var skippedCount = 0;
            for (var i = 0; i < taskQueue.Count; i++)
            {
                var t = taskQueue[i];
                if (t.Status == GatheringTaskStatus.Completed || t.Status == GatheringTaskStatus.Failed)
                    continue;
                if (t.QuantityRemaining <= 0)
                    continue;

                // If GatherNodeLevel wasn't populated (e.g. StartGatherOnly path), try
                // resolving it from the gather cache so the level check isn't silently skipped.
                var nodeLevel = t.GatherNodeLevel;
                if (nodeLevel <= 0)
                {
                    nodeLevel = Expedition.Instance.RecipeResolver.GetGatherNodeLevel(t.ItemId);
                    if (nodeLevel > 0)
                    {
                        DalamudApi.Log.Debug(
                            $"[Gather:LevelCheck] {t.ItemName}: GatherNodeLevel was 0, resolved to {nodeLevel} from cache.");
                    }
                    else
                    {
                        DalamudApi.Log.Debug(
                            $"[Gather:LevelCheck] {t.ItemName}: GatherNodeLevel unknown (0) — skipping level check.");
                        continue;
                    }
                }

                var threshold = t.GatherType switch
                {
                    GatherType.Miner => minerThreshold,
                    GatherType.Botanist => botanistThreshold,
                    GatherType.Fisher => fisherThreshold,
                    _ => int.MaxValue, // Unknown type — don't skip
                };

                if (nodeLevel > threshold)
                {
                    var className = t.GatherType switch
                    {
                        GatherType.Miner => "Miner",
                        GatherType.Botanist => "Botanist",
                        GatherType.Fisher => "Fisher",
                        _ => "Gatherer",
                    };
                    var playerLevel = t.GatherType switch
                    {
                        GatherType.Miner => minerLevel,
                        GatherType.Botanist => botanistLevel,
                        GatherType.Fisher => fisherLevel,
                        _ => 0,
                    };

                    t.Status = GatheringTaskStatus.Skipped;
                    t.ErrorMessage = $"{className} Lv{playerLevel} too low for Lv{nodeLevel} nodes " +
                                     $"(GBR threshold={threshold}).";
                    DalamudApi.Log.Warning(
                        $"[Gather:LevelCheck] SKIPPING {t.ItemName}: {t.ErrorMessage} " +
                        $"Need {t.QuantityRemaining} more.");
                    skippedCount++;
                }
            }

            if (skippedCount > 0)
            {
                DalamudApi.Log.Warning(
                    $"[Gather:LevelCheck] Skipped {skippedCount}/{taskQueue.Count} tasks due to insufficient gathering level. " +
                    "Level up your gathering classes or obtain these materials another way.");
            }
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Debug($"[Gather:LevelCheck] Pre-flight check failed: {ex.Message}");
            // Non-fatal — continue with normal gathering, GBR will handle it (with retries)
        }
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

        // Skip tasks that were already marked as Skipped (e.g., by level pre-flight check)
        if (task.Status == GatheringTaskStatus.Skipped)
        {
            DalamudApi.Log.Information(
                $"[Gather:Init] Skipping {task.ItemName}: {task.ErrorMessage}");
            AdvanceToNextTask();
            return;
        }

        task.Status = GatheringTaskStatus.InProgress;
        taskStartTime = DateTime.Now;
        lastProgressTime = DateTime.Now;
        lastDeltaChangeTime = DateTime.Now;
        noDeltaTimeoutStart = null;
        resetCyclesWithoutProgress = 0;
        gbrIdlePolls = 0;
        consecutiveReEnableFailures = 0;
        cmdOnlyConsecutiveRefusals = 0;
        lastAutoGatherCheck = DateTime.MinValue;
        finishingNodeSince = null;
        pollsSinceLastSummary = 0;
        lastDisableSnapshot = null;

        // Initialize slot cache for fast inventory reads during this task
        var includeSaddlebag = Expedition.Config.IncludeSaddlebagInScans;
        slotCache.Initialize(task.ItemId, includeSaddlebag);

        // Seed QuantityGathered from actual inventory NOW, not on first Update poll.
        // This prevents telling GBR "need 400" when we really only need 1.
        var actualCount = slotCache.LastTotal;
        task.QuantityGathered = actualCount;
        lastKnownCount = actualCount;
        baselineCount = actualCount;

        DalamudApi.Log.Information(
            $"[Gather:Init] Task={task.ItemName} (id={task.ItemId}), target={task.QuantityNeeded}, " +
            $"inventoryNow={actualCount}, remaining={task.QuantityRemaining}, saddlebag={includeSaddlebag}, " +
            $"retryCount={task.RetryCount}, taskIndex={currentTaskIndex}/{taskQueue.Count}");

        // Early completion check — if inventory already meets the target, skip entirely.
        // This catches cases where items were gathered between queue build and task start.
        if (task.IsComplete)
        {
            task.Status = GatheringTaskStatus.Completed;
            DalamudApi.Log.Information(
                $"[Gather:Init] {task.ItemName} already complete ({actualCount}/{task.QuantityNeeded}), skipping.");
            AdvanceToNextTask();
            return;
        }

        // Check if this item was skipped during list injection (not in GBR's Gatherables).
        // Common for crystals/shards/clusters which GBR indexes differently.
        commandOnlyMode = ipc.GatherBuddyLists.SkippedItemIds.Contains(task.ItemId);
        lastCommandReissueTime = DateTime.MinValue;

        if (commandOnlyMode)
        {
            DalamudApi.Log.Information($"[Gather:Init] {task.ItemName} not in GBR auto-gather list — command-only mode.");
        }

        // Don't fire gather commands while the player is occupied —
        // the Update loop will re-enable once the player is free.
        if (IsPlayerOccupied())
        {
            DalamudApi.Log.Information($"[Gather:Init] Player occupied, deferring gather command for {task.ItemName}.");
            StatusMessage = $"Waiting to gather {task.ItemName} (player busy)...";
            return;
        }

        // Send the gather command to hint GBR and trigger teleport.
        // In command-only mode, use the generic /gather to let GBR pick the best class.
        DalamudApi.Log.Information(
            $"[Gather:Init] Starting: {task.ItemName} — GatherType={task.GatherType}, " +
            $"commandOnly={commandOnlyMode}, need={task.QuantityRemaining} more, " +
            $"noDeltaTimeout={Expedition.Config.GatherNoDeltaTimeoutSeconds}s, " +
            $"absoluteTimeout={Expedition.Config.GatherAbsoluteTimeoutSeconds}s, " +
            $"stallTimeout={StallTimeoutSeconds}s, retryLimit={Expedition.Config.GatherRetryLimit}");
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
        DalamudApi.Log.Debug($"[Gather:Init] AutoGather enabled. GBR state: enabled={ipc.GatherBuddy.GetAutoGatherEnabled()}");

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

        // Log final state of the completed/failed task
        if (prevTask != null)
        {
            var prevElapsed = (DateTime.Now - taskStartTime).TotalSeconds;
            DalamudApi.Log.Information(
                $"[Gather:Advance] Leaving task: {prevTask.ItemName} — status={prevTask.Status}, " +
                $"count={prevTask.QuantityGathered}/{prevTask.QuantityNeeded}, " +
                $"retries={prevTask.RetryCount}, elapsed={prevElapsed:F0}s");
        }

        currentTaskIndex++;
        if (currentTaskIndex >= taskQueue.Count)
        {
            CleanupGatherList();
            State = GatheringOrchestratorState.Completed;

            // Log a final summary of all tasks
            var completedCount = 0;
            var failedCount = 0;
            var skippedCount = 0;
            for (var i = 0; i < taskQueue.Count; i++)
            {
                if (taskQueue[i].Status == GatheringTaskStatus.Completed) completedCount++;
                else if (taskQueue[i].Status == GatheringTaskStatus.Failed) failedCount++;
                else if (taskQueue[i].Status == GatheringTaskStatus.Skipped) skippedCount++;
            }
            DalamudApi.Log.Information(
                $"[Gather:Done] All {taskQueue.Count} gathering tasks finished: " +
                $"{completedCount} completed, {failedCount} failed, {skippedCount} skipped.");
            StatusMessage = "All gathering tasks complete.";
            return;
        }

        // Log the class transition for debugging class-switch issues
        var nextTask = taskQueue[currentTaskIndex];
        var prevType = prevTask?.GatherType.ToString() ?? "?";
        var nextType = nextTask.GatherType.ToString();
        var classSwitch = prevTask != null && prevTask.GatherType != nextTask.GatherType;
        DalamudApi.Log.Information(
            $"[Gather:Advance] Task [{currentTaskIndex}/{taskQueue.Count}]: {nextTask.ItemName} ({nextType}) " +
            $"from {prevTask?.ItemName ?? "none"} ({prevType}), " +
            $"target={nextTask.QuantityNeeded}, remaining={nextTask.QuantityRemaining}" +
            (classSwitch ? " — CLASS SWITCH REQUIRED" : ""));

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
        // Retry GbrStateTracker initialization if it failed during Start()
        // (e.g. because GatherBuddyListManager wasn't initialized yet)
        if (!ipc.GbrStateTracker.IsInitialized && ipc.GatherBuddyLists.GbrPluginInstance != null)
        {
            ipc.GbrStateTracker.Initialize(ipc.GatherBuddyLists.GbrPluginInstance);
        }

        var task = CurrentTask;
        if (!enabled && State == GatheringOrchestratorState.Running && task != null)
        {
            // Capture GBR's internal state NOW — before it has a chance to clear AutoStatus.
            // The IPC callback already captured LastDisableStatusText in GatherBuddyIpc.
            var snapshot = ipc.GbrStateTracker.GetSnapshot();
            lastDisableSnapshot = snapshot;

            // Also incorporate the IPC-captured status text if reflection didn't get it
            var ipcStatus = ipc.GatherBuddy.LastDisableStatusText;
            var statusForLog = !string.IsNullOrEmpty(snapshot.AutoStatus)
                ? snapshot.AutoStatus
                : ipcStatus;

            DalamudApi.Log.Warning(
                $"[Gather:GBR:Disabled] {task.ItemName} ({task.QuantityGathered}/{task.QuantityNeeded}): " +
                $"reason={snapshot.Reason}, autoStatus=\"{statusForLog}\", " +
                $"tasks={snapshot.TaskQueueCount}, busy={snapshot.TaskManagerBusy}, " +
                $"hasItems={snapshot.HasItemsToGather}, amiss={snapshot.ConsecutiveAmissCount}, " +
                $"hasTarget={snapshot.HasGatherTarget}");

            // Run deep diagnostics when GBR reports no items to gather despite our list injection.
            // Trigger on: (a) HasItemsToGather=false from reflection, OR (b) AutoStatus="Idle..."
            // with no gather target (indicates AbortAutoGather ran because _gatherableItems was empty).
            var shouldDiagnose = !snapshot.HasItemsToGather
                || (statusForLog == "Idle..." && !snapshot.HasGatherTarget
                    && snapshot.TaskQueueCount == 0 && !snapshot.TaskManagerBusy);
            if (shouldDiagnose && ipc.GbrStateTracker.IsInitialized)
            {
                DalamudApi.Log.Warning(
                    $"[Gather:GBR:Disabled] No items/targets detected (reason={snapshot.Reason}) — running deep diagnostic...");
                ipc.GbrStateTracker.DiagnoseEmptyGatherableItems();
            }
        }
        else if (!enabled)
        {
            // Not running — just log for reference
            lastDisableSnapshot = ipc.GbrStateTracker.GetSnapshot();
            DalamudApi.Log.Debug($"GBR AutoGather disabled (not gathering). reason={lastDisableSnapshot.Reason}");
        }
        else
        {
            lastDisableSnapshot = null; // Clear on re-enable
            DalamudApi.Log.Debug($"GBR AutoGather enabled.");
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
