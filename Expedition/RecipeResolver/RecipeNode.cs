namespace Expedition.RecipeResolver;

/// <summary>
/// A node in the recipe dependency tree. Each node represents one item
/// and optionally contains sub-ingredients that must be crafted first.
/// </summary>
public sealed class RecipeNode
{
    /// <summary>Item ID of the final product.</summary>
    public uint ItemId { get; init; }

    /// <summary>Display name of the product.</summary>
    public string ItemName { get; init; } = string.Empty;

    /// <summary>Recipe ID in the game data (Lumina Recipe sheet row).</summary>
    public uint RecipeId { get; init; }

    /// <summary>Number of items produced per craft.</summary>
    public int YieldPerCraft { get; init; } = 1;

    /// <summary>The crafter class required (CRP, BSM, ARM, GSM, LTW, WVR, ALC, CUL).</summary>
    public int CraftTypeId { get; init; }

    /// <summary>Required crafter level.</summary>
    public int RequiredLevel { get; init; }

    /// <summary>True if this recipe produces a collectable.</summary>
    public bool IsCollectable { get; init; }

    /// <summary>True if this is an expert recipe.</summary>
    public bool IsExpert { get; init; }

    /// <summary>Direct ingredients for this recipe.</summary>
    public List<MaterialRequirement> Ingredients { get; init; } = new();

    /// <summary>Sub-recipes (ingredients that are themselves craftable).</summary>
    public List<RecipeNode> SubRecipes { get; init; } = new();
}
