namespace Expedition.RecipeResolver;

/// <summary>
/// Represents a single material needed to craft something.
/// </summary>
public sealed class MaterialRequirement
{
    /// <summary>Item ID of the material.</summary>
    public uint ItemId { get; init; }

    /// <summary>Display name of the material.</summary>
    public string ItemName { get; init; } = string.Empty;

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
