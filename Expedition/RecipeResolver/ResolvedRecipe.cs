namespace Expedition.RecipeResolver;

/// <summary>
/// The fully resolved output of recipe resolution. Contains the flattened
/// ordered lists of what needs to be gathered and crafted.
/// </summary>
public sealed class ResolvedRecipe
{
    /// <summary>The root recipe node (the target item).</summary>
    public RecipeNode RootRecipe { get; init; } = null!;

    /// <summary>All raw materials that need to be gathered (leaf nodes).</summary>
    public List<MaterialRequirement> GatherList { get; init; } = new();

    /// <summary>
    /// Ordered crafting steps from bottom-up: sub-components first, final item last.
    /// Each entry is (RecipeNode, quantity to craft).
    /// </summary>
    public List<CraftStep> CraftOrder { get; init; } = new();

    /// <summary>
    /// Materials that are neither craftable nor gatherable and must be
    /// purchased from vendors, obtained via other means, etc.
    /// </summary>
    public List<MaterialRequirement> OtherMaterials { get; init; } = new();
}

/// <summary>
/// A single step in the crafting order.
/// </summary>
public sealed class CraftStep
{
    public RecipeNode Recipe { get; init; } = null!;
    public int Quantity { get; set; }
    public bool Completed { get; set; }
}
