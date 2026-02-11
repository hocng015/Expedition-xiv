namespace Expedition.RecipeResolver;

/// <summary>
/// A node in the recipe dependency tree. Each node represents one item
/// and optionally contains sub-ingredients that must be crafted first.
/// Extended with specialist, durability, and master book metadata.
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

    /// <summary>True if this is an expert recipe (random crafting conditions).</summary>
    public bool IsExpert { get; init; }

    /// <summary>True if this recipe requires specialist status in the class.</summary>
    public bool RequiresSpecialist { get; init; }

    /// <summary>Recipe durability (35, 40, 60, 70, 80 etc). Affects macro/solver choice.</summary>
    public int RecipeDurability { get; init; }

    /// <summary>Suggested craftsmanship for HQ/collectability targets.</summary>
    public int SuggestedCraftsmanship { get; init; }

    /// <summary>Suggested control for HQ/collectability targets.</summary>
    public int SuggestedControl { get; init; }

    /// <summary>True if this recipe requires a master recipe book to be unlocked.</summary>
    public bool RequiresMasterBook { get; init; }

    /// <summary>The master book index/ID required, if any.</summary>
    public uint MasterBookId { get; init; }

    /// <summary>Direct ingredients for this recipe.</summary>
    public List<MaterialRequirement> Ingredients { get; init; } = new();

    /// <summary>Sub-recipes (ingredients that are themselves craftable).</summary>
    public List<RecipeNode> SubRecipes { get; init; } = new();
}
