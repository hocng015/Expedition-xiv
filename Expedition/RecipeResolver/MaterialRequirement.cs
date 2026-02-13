namespace Expedition.RecipeResolver;

/// <summary>
/// Represents a single material needed to craft something.
/// Extended with timed node, zone, and aethersand metadata.
/// </summary>
public sealed class MaterialRequirement
{
    /// <summary>Item ID of the material.</summary>
    public uint ItemId { get; init; }

    /// <summary>Display name of the material.</summary>
    public string ItemName { get; init; } = string.Empty;

    /// <summary>Game icon ID for the material item.</summary>
    public uint IconId { get; init; }

    /// <summary>Total quantity required.</summary>
    public int QuantityNeeded { get; set; }

    /// <summary>Quantity already available in inventory.</summary>
    public int QuantityOwned { get; set; }

    /// <summary>Remaining quantity that must be obtained.</summary>
    public int QuantityRemaining => Math.Max(0, QuantityNeeded - QuantityOwned);

    /// <summary>True if this material itself has a recipe (is craftable).</summary>
    public bool IsCraftable { get; init; }

    /// <summary>True if this material is gatherable (BTN/MIN/FSH).</summary>
    public bool IsGatherable { get; init; }

    /// <summary>True if this material is a collectable variant.</summary>
    public bool IsCollectable { get; init; }

    /// <summary>Recipe ID if this material is craftable.</summary>
    public uint? RecipeId { get; init; }

    /// <summary>The type of gathering class needed, if gatherable.</summary>
    public GatherType GatherType { get; init; } = GatherType.None;

    // --- Timed Node Metadata ---

    /// <summary>True if this item only spawns at specific Eorzean times.</summary>
    public bool IsTimedNode { get; init; }

    /// <summary>Eorzean hours this item's node spawns at.</summary>
    public int[]? SpawnHours { get; init; }

    /// <summary>True if obtained from ephemeral nodes (Aetherial Reduction source).</summary>
    public bool IsAetherialReductionSource { get; init; }

    // --- Zone Information ---

    /// <summary>TerritoryType ID of the gathering zone.</summary>
    public uint GatherZoneId { get; init; }

    /// <summary>Display name of the gathering zone.</summary>
    public string GatherZoneName { get; init; } = string.Empty;

    // --- Crystal Classification ---

    /// <summary>True if this is a crystal/shard/cluster elemental catalyst.</summary>
    public bool IsCrystal { get; init; }

    // --- Vendor Information ---

    /// <summary>True if this item can be purchased from a vendor (GilShop or SpecialShop).</summary>
    public bool IsVendorItem { get; init; }

    /// <summary>Vendor info (NPC name, zone, price, currency) if available.</summary>
    public VendorInfo? VendorInfo { get; init; }

    // --- Mob Drop Information ---

    /// <summary>True if this item is dropped by mobs (sourced from Garland Tools).</summary>
    public bool IsMobDrop { get; set; }

    /// <summary>List of mobs that drop this item (top entries by level).</summary>
    public List<MobDropInfo>? MobDrops { get; set; }

    public override string ToString()
        => $"{ItemName} (x{QuantityRemaining}/{QuantityNeeded})";
}

public enum GatherType
{
    None,
    Botanist,
    Miner,
    Fisher,
    Unknown,
}
