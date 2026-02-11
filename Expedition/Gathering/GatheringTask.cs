using Expedition.RecipeResolver;

namespace Expedition.Gathering;

/// <summary>
/// Represents a single gathering task to be executed by GatherBuddy Reborn.
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

    public int QuantityRemaining => Math.Max(0, QuantityNeeded - QuantityGathered);
    public bool IsComplete => QuantityRemaining <= 0;

    public static GatheringTask FromMaterial(MaterialRequirement mat)
    {
        return new GatheringTask
        {
            ItemId = mat.ItemId,
            ItemName = mat.ItemName,
            QuantityNeeded = mat.QuantityRemaining,
            IsCollectable = mat.IsCollectable,
            GatherType = mat.GatherType,
        };
    }
}

public enum GatheringTaskStatus
{
    Pending,
    InProgress,
    WaitingForGbr,
    Completed,
    Failed,
    Skipped,
}
