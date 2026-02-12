namespace Expedition.RecipeResolver;

/// <summary>
/// Lightweight model for displaying a gatherable item in the browse tab.
/// </summary>
public sealed class GatherableItemInfo
{
    /// <summary>Item ID.</summary>
    public uint ItemId { get; init; }

    /// <summary>Display name.</summary>
    public string ItemName { get; init; } = string.Empty;

    /// <summary>Game icon ID for the item.</summary>
    public uint IconId { get; init; }

    /// <summary>Gathering class (Miner, Botanist, Fisher).</summary>
    public GatherType GatherClass { get; init; }

    /// <summary>Gathering level required.</summary>
    public int GatherLevel { get; init; }

    /// <summary>True if this is a collectable gather.</summary>
    public bool IsCollectable { get; init; }

    /// <summary>True if this item is a crystal/shard/cluster.</summary>
    public bool IsCrystal { get; init; }

    /// <summary>True if the item can also be crafted (has a recipe).</summary>
    public bool IsAlsoCraftable { get; init; }

    /// <summary>Item Level (ilvl) for sorting/display.</summary>
    public int ItemLevel { get; init; }
}
