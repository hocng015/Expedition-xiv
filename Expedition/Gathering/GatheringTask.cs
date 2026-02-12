using Expedition.RecipeResolver;

namespace Expedition.Gathering;

/// <summary>
/// Represents a single gathering task to be executed by GatherBuddy Reborn.
/// Extended with timed node scheduling, zone routing, and aetherial reduction metadata.
/// </summary>
public sealed class GatheringTask
{
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public int QuantityNeeded { get; set; }
    public int QuantityGathered { get; set; }
    public bool IsCollectable { get; init; }
    public GatherType GatherType { get; init; }
    public GatheringTaskStatus Status { get; set; } = GatheringTaskStatus.Pending;
    public int RetryCount { get; set; }
    public string? ErrorMessage { get; set; }

    // --- Timed Node Scheduling ---

    /// <summary>True if this item comes from an unspoiled/legendary timed node.</summary>
    public bool IsTimedNode { get; init; }

    /// <summary>Eorzean hours at which this node spawns (e.g., [2, 14] for twice per ET day).</summary>
    public int[]? SpawnHours { get; init; }

    /// <summary>True if this item is gathered from ephemeral nodes (for Aetherial Reduction).</summary>
    public bool IsAetherialReductionSource { get; init; }

    // --- Zone Routing ---

    /// <summary>The TerritoryType ID (zone) where this item is gathered.</summary>
    public uint GatherZoneId { get; init; }

    /// <summary>Display name of the gathering zone.</summary>
    public string GatherZoneName { get; init; } = string.Empty;

    // --- Aetherial Reduction ---

    /// <summary>If this is an aethersand source, the reduction step details.</summary>
    public AetherialReductionStep? ReductionStep { get; set; }

    // --- GP Requirements ---

    /// <summary>Estimated GP needed per gather attempt for this item.</summary>
    public uint GpPerAttempt { get; init; }

    // --- Computed ---

    public int QuantityRemaining => Math.Max(0, QuantityNeeded - QuantityGathered);
    public bool IsComplete => QuantityRemaining <= 0;

    public static GatheringTask FromMaterial(MaterialRequirement mat)
    {
        return new GatheringTask
        {
            ItemId = mat.ItemId,
            ItemName = mat.ItemName,
            QuantityNeeded = mat.QuantityNeeded,
            IsCollectable = mat.IsCollectable,
            GatherType = mat.GatherType,
            IsTimedNode = mat.IsTimedNode,
            IsAetherialReductionSource = mat.IsAetherialReductionSource,
            SpawnHours = mat.SpawnHours,
            GatherZoneId = mat.GatherZoneId,
            GatherZoneName = mat.GatherZoneName,
        };
    }
}

public enum GatheringTaskStatus
{
    Pending,
    InProgress,
    WaitingForGbr,
    WaitingForTimedNode,
    Completed,
    Failed,
    Skipped,
}
