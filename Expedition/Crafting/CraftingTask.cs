using Expedition.RecipeResolver;

namespace Expedition.Crafting;

/// <summary>
/// Represents a single crafting task to be executed by Artisan.
/// </summary>
public sealed class CraftingTask
{
    public uint RecipeId { get; init; }
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public int Quantity { get; set; }
    public int QuantityCrafted { get; set; }
    public bool IsCollectable { get; init; }
    public bool IsExpert { get; init; }
    public int CraftTypeId { get; init; }
    public string? PreferredSolver { get; init; }
    public CraftingTaskStatus Status { get; set; } = CraftingTaskStatus.Pending;
    public string? ErrorMessage { get; set; }
    public int RetryCount { get; set; }

    public int QuantityRemaining => Math.Max(0, Quantity - QuantityCrafted);
    public bool IsComplete => QuantityRemaining <= 0;

    public static CraftingTask FromCraftStep(CraftStep step, string? preferredSolver = null)
    {
        return new CraftingTask
        {
            RecipeId = step.Recipe.RecipeId,
            ItemId = step.Recipe.ItemId,
            ItemName = step.Recipe.ItemName,
            Quantity = step.Quantity,
            IsCollectable = step.Recipe.IsCollectable,
            IsExpert = step.Recipe.IsExpert,
            CraftTypeId = step.Recipe.CraftTypeId,
            PreferredSolver = preferredSolver,
        };
    }
}

public enum CraftingTaskStatus
{
    Pending,
    WaitingForMaterials,
    InProgress,
    WaitingForArtisan,
    Completed,
    Failed,
    Skipped,
}
