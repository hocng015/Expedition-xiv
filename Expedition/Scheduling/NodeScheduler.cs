using Expedition.RecipeResolver;

namespace Expedition.Scheduling;

/// <summary>
/// Classifies gathering nodes by their spawn behavior and manages
/// scheduling for timed/unspoiled/ephemeral nodes.
///
/// Pain points addressed:
/// - Unspoiled nodes only spawn for 2 ET hours (~5:50 real), twice per ET day
/// - Ephemeral nodes spawn every 4 ET hours for 4 ET hours (~11:40 real)
/// - Missing a window means waiting ~35 real minutes for the next one
/// - Multiple timed items sharing the same spawn hour creates conflicts
/// </summary>
public sealed class NodeScheduler
{
    /// <summary>
    /// Sorts gathering tasks so that timed-node items are gathered first
    /// when their window is active, and normal-node items fill the gaps.
    /// Returns a new ordered list.
    /// </summary>
    public static List<ScheduledGatherTask> BuildScheduledQueue(
        IReadOnlyList<Gathering.GatheringTask> tasks)
    {
        var scheduled = new List<ScheduledGatherTask>();

        foreach (var task in tasks)
        {
            scheduled.Add(new ScheduledGatherTask
            {
                Task = task,
                NodeType = ClassifyNode(task),
                SpawnHours = GetSpawnHours(task),
            });
        }

        // Sort: currently-active timed nodes first, then upcoming timed nodes
        // sorted by soonest spawn, then normal nodes last.
        scheduled.Sort((a, b) =>
        {
            var aActive = a.IsCurrentlyActive;
            var bActive = b.IsCurrentlyActive;

            // Active timed nodes come first
            if (aActive && !bActive) return -1;
            if (!aActive && bActive) return 1;

            // Among timed nodes, sort by soonest next spawn
            if (a.NodeType != GatherNodeType.Normal && b.NodeType != GatherNodeType.Normal)
                return a.SecondsUntilNextSpawn.CompareTo(b.SecondsUntilNextSpawn);

            // Timed before normal
            if (a.NodeType != GatherNodeType.Normal && b.NodeType == GatherNodeType.Normal) return -1;
            if (a.NodeType == GatherNodeType.Normal && b.NodeType != GatherNodeType.Normal) return 1;

            return 0;
        });

        return scheduled;
    }

    /// <summary>
    /// Given a list of scheduled tasks, finds the next task that should be
    /// worked on right now. Prefers active timed nodes, falls back to normal nodes,
    /// and if only waiting tasks remain, returns the one with the shortest wait.
    /// </summary>
    public static ScheduledGatherTask? PickNextTask(List<ScheduledGatherTask> queue)
    {
        // First: any active timed node that isn't complete
        var activeTimed = queue.FirstOrDefault(t =>
            t.NodeType != GatherNodeType.Normal && t.IsCurrentlyActive && !t.Task.IsComplete);

        if (activeTimed != null) return activeTimed;

        // Second: any normal node that isn't complete (fill gap while waiting for timed)
        var normal = queue.FirstOrDefault(t =>
            t.NodeType == GatherNodeType.Normal && !t.Task.IsComplete);

        if (normal != null) return normal;

        // Third: the timed node with the shortest wait
        return queue
            .Where(t => !t.Task.IsComplete)
            .OrderBy(t => t.SecondsUntilNextSpawn)
            .FirstOrDefault();
    }

    private static GatherNodeType ClassifyNode(Gathering.GatheringTask task)
    {
        // Items requiring Aetherial Reduction come from ephemeral nodes
        if (task.IsAetherialReductionSource)
            return GatherNodeType.Ephemeral;

        // Timed nodes are identified by the item's gathering point data.
        // For now, we tag items that are flagged as timed in GBR's data.
        if (task.IsTimedNode)
            return GatherNodeType.Unspoiled;

        return GatherNodeType.Normal;
    }

    private static int[] GetSpawnHours(Gathering.GatheringTask task)
    {
        // Unspoiled nodes spawn twice per ET day at fixed hours.
        // Ephemeral nodes spawn every 4 ET hours.
        // These would ideally be looked up from game data, but for now
        // we use the task's metadata if available.
        if (task.SpawnHours != null && task.SpawnHours.Length > 0)
            return task.SpawnHours;

        // Ephemeral default: every 4 hours
        if (task.IsAetherialReductionSource)
            return new[] { 0, 4, 8, 12, 16, 20 };

        return Array.Empty<int>();
    }
}

public sealed class ScheduledGatherTask
{
    public Gathering.GatheringTask Task { get; init; } = null!;
    public GatherNodeType NodeType { get; init; }
    public int[] SpawnHours { get; init; } = Array.Empty<int>();

    /// <summary>Duration the node is active in ET hours.</summary>
    public int SpawnDurationHours => NodeType switch
    {
        GatherNodeType.Unspoiled => 2,
        GatherNodeType.Ephemeral => 4,
        _ => 24, // Always available
    };

    /// <summary>True if this node is currently spawned and gatherable.</summary>
    public bool IsCurrentlyActive
    {
        get
        {
            if (NodeType == GatherNodeType.Normal) return true;
            return SpawnHours.Any(h => EorzeanTime.IsWithinWindow(h, SpawnDurationHours));
        }
    }

    /// <summary>Real-world seconds until this node next spawns. 0 if currently active.</summary>
    public double SecondsUntilNextSpawn
    {
        get
        {
            if (IsCurrentlyActive) return 0;
            if (SpawnHours.Length == 0) return 0;

            return SpawnHours.Min(h => EorzeanTime.SecondsUntilEorzeanHour(h));
        }
    }

    /// <summary>Human-readable status for the UI.</summary>
    public string ScheduleStatus
    {
        get
        {
            if (NodeType == GatherNodeType.Normal) return "Always available";
            if (IsCurrentlyActive) return "ACTIVE NOW";

            var secs = SecondsUntilNextSpawn;
            return $"Spawns in {EorzeanTime.FormatRealDuration(secs)}";
        }
    }
}

public enum GatherNodeType
{
    Normal,
    Unspoiled,
    Ephemeral,
    Legendary, // Folklore-gated unspoiled nodes
}
