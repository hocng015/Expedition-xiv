using Expedition.IPC;
using Expedition.RecipeResolver;

namespace Expedition.Gathering;

/// <summary>
/// Orchestrates gathering operations through GatherBuddy Reborn.
/// Manages a queue of gathering tasks and drives GBR via IPC and chat commands.
/// </summary>
public sealed class GatheringOrchestrator
{
    private readonly IpcManager ipc;
    private readonly List<GatheringTask> taskQueue = new();
    private int currentTaskIndex = -1;
    private DateTime lastPollTime = DateTime.MinValue;

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
    /// Begins executing the gathering queue.
    /// </summary>
    public void Start()
    {
        if (taskQueue.Count == 0)
        {
            State = GatheringOrchestratorState.Idle;
            return;
        }

        State = GatheringOrchestratorState.Running;
        currentTaskIndex = 0;
        StartCurrentTask();
    }

    /// <summary>
    /// Stops all gathering and disables GBR AutoGather.
    /// </summary>
    public void Stop()
    {
        if (State == GatheringOrchestratorState.Running)
        {
            ipc.GatherBuddy.SetAutoGatherEnabled(false);
        }

        State = GatheringOrchestratorState.Idle;
        StatusMessage = "Gathering stopped.";
    }

    /// <summary>
    /// Called each frame by the workflow engine to poll status.
    /// </summary>
    public void Update(Inventory.InventoryManager inventoryManager)
    {
        if (State != GatheringOrchestratorState.Running) return;

        // Throttle polling
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

        if (task.IsComplete)
        {
            task.Status = GatheringTaskStatus.Completed;
            StatusMessage = $"Gathered enough {task.ItemName}.";
            DalamudApi.Log.Information(StatusMessage);
            AdvanceToNextTask();
            return;
        }

        // Check if GBR is still working
        if (!ipc.GatherBuddy.GetAutoGatherEnabled())
        {
            // GBR turned off -- might have finished or errored
            if (task.Status == GatheringTaskStatus.InProgress)
            {
                task.RetryCount++;
                if (task.RetryCount > Expedition.Config.GatherRetryLimit)
                {
                    task.Status = GatheringTaskStatus.Failed;
                    task.ErrorMessage = "GBR AutoGather disabled unexpectedly after retries.";
                    DalamudApi.Log.Warning($"Gathering task failed: {task.ItemName}");
                    AdvanceToNextTask();
                }
                else
                {
                    // Retry: re-send the gather command
                    DalamudApi.Log.Information($"Retrying gather for {task.ItemName} (attempt {task.RetryCount})");
                    StartCurrentTask();
                }
            }
        }

        StatusMessage = $"Gathering {task.ItemName}: {task.QuantityGathered}/{task.QuantityNeeded}";
    }

    /// <summary>
    /// Returns true when all gathering tasks are complete (or failed/skipped).
    /// </summary>
    public bool IsComplete => State == GatheringOrchestratorState.Completed || taskQueue.Count == 0;

    /// <summary>
    /// Returns true if there are any failed tasks.
    /// </summary>
    public bool HasFailures => taskQueue.Any(t => t.Status == GatheringTaskStatus.Failed);

    private void StartCurrentTask()
    {
        var task = CurrentTask;
        if (task == null) return;

        task.Status = GatheringTaskStatus.InProgress;

        // Use the /gather command to tell GBR what to gather
        if (task.IsCollectable)
        {
            ChatIpc.StartCollectableGathering();
        }

        // Send the gather command
        ChatIpc.GatherItem(task.ItemName);

        // Enable AutoGather
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
        // GBR is waiting/idle -- the current gather target may be done
        DalamudApi.Log.Debug("GBR AutoGather entered waiting state.");
    }

    private void OnGbrEnabledChanged(bool enabled)
    {
        DalamudApi.Log.Debug($"GBR AutoGather enabled changed: {enabled}");
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
