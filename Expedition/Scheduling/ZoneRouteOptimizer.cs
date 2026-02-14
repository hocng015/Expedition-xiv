using Expedition.Gathering;

namespace Expedition.Scheduling;

/// <summary>
/// Optimizes gathering task order to minimize zone transitions.
///
/// Pain points addressed:
/// - Recipes requiring materials from 4+ zones means 4+ teleports + loading screens
/// - Each zone transition costs Gil for teleport + real time for loading
/// - Items in the same zone should be gathered together
/// - Timed nodes override zone grouping (window priority > zone efficiency)
/// - GBR handles navigation within zones, but we control the order of targets
/// </summary>
public sealed class ZoneRouteOptimizer
{
    /// <summary>
    /// Groups gathering tasks by zone and reorders them to minimize transitions.
    /// Timed/unspoiled items retain priority regardless of zone grouping.
    /// </summary>
    public static List<GatheringTask> OptimizeRoute(IReadOnlyList<GatheringTask> tasks)
    {
        // Separate timed and normal tasks
        var timed = new List<GatheringTask>();
        var normal = new List<GatheringTask>();

        foreach (var task in tasks)
        {
            if (task.IsTimedNode || task.IsAetherialReductionSource)
                timed.Add(task);
            else
                normal.Add(task);
        }

        // Group normal tasks by zone to minimize zone transitions
        // Sort by zone ID instead of LINQ GroupBy+SelectMany (avoids intermediate grouping allocations)
        normal.Sort((a, b) => a.GatherZoneId.CompareTo(b.GatherZoneId));

        // Build final list: timed items can be interleaved based on Eorzean time,
        // but normal items should be zone-clustered.
        var result = new List<GatheringTask>(tasks.Count);

        // Start with any currently-active timed nodes (for-loop instead of LINQ .Where+.Any)
        for (var i = 0; i < timed.Count; i++)
        {
            var t = timed[i];
            if (t.SpawnHours == null) continue;
            var duration = t.IsAetherialReductionSource ? 4 : 2;
            for (var j = 0; j < t.SpawnHours.Length; j++)
            {
                if (EorzeanTime.IsWithinWindow(t.SpawnHours[j], duration))
                {
                    result.Add(t);
                    break;
                }
            }
        }

        // Then normal tasks grouped by zone
        result.AddRange(normal);

        // Then remaining timed tasks (will be scheduled by NodeScheduler when their window opens)
        var alreadyAdded = new HashSet<uint>(result.Count);
        for (var i = 0; i < result.Count; i++) alreadyAdded.Add(result[i].ItemId);
        for (var i = 0; i < timed.Count; i++)
            if (!alreadyAdded.Contains(timed[i].ItemId)) result.Add(timed[i]);

        return result;
    }

    /// <summary>
    /// Estimates total teleport costs for a gathering route.
    /// </summary>
    public static int EstimateTeleportCosts(IReadOnlyList<GatheringTask> orderedTasks)
    {
        var transitions = 0;
        uint lastZone = 0;

        foreach (var task in orderedTasks)
        {
            if (task.GatherZoneId != lastZone && lastZone != 0)
                transitions++;
            lastZone = task.GatherZoneId;
        }

        // Rough estimate: ~500 Gil per teleport average
        return transitions * 500;
    }

    /// <summary>
    /// Returns a summary of zone transitions in the current route.
    /// </summary>
    public static List<string> GetRouteDescription(IReadOnlyList<GatheringTask> orderedTasks)
    {
        var desc = new List<string>();
        uint currentZone = 0;
        var itemsInZone = 0;

        foreach (var task in orderedTasks)
        {
            if (task.GatherZoneId != currentZone)
            {
                if (currentZone != 0)
                    desc.Add($"  Zone {currentZone}: {itemsInZone} items");

                currentZone = task.GatherZoneId;
                itemsInZone = 0;
            }
            itemsInZone++;
        }

        if (currentZone != 0)
            desc.Add($"  Zone {currentZone}: {itemsInZone} items");

        return desc;
    }
}
