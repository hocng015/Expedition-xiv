namespace Expedition.RecipeResolver;

/// <summary>
/// Vendor purchase information for a material item.
/// Contains the best available vendor (preferring gil shops over special currency).
/// </summary>
public sealed class VendorInfo
{
    /// <summary>NPC name that sells this item (from ENpcResident).</summary>
    public string NpcName { get; init; } = string.Empty;

    /// <summary>Zone/map name where the NPC is located.</summary>
    public string ZoneName { get; init; } = string.Empty;

    /// <summary>Price per unit in the given currency.</summary>
    public uint PricePerUnit { get; init; }

    /// <summary>Display name of the currency ("Gil", "Allagan Tomestone of Poetics", etc.).</summary>
    public string CurrencyName { get; init; } = "Gil";

    /// <summary>Item ID of the currency (1 = Gil, or the tomestone/scrip item ID).</summary>
    public uint CurrencyItemId { get; init; } = 1;

    /// <summary>True for standard gil vendors, false for special currency shops.</summary>
    public bool IsGilShop { get; init; } = true;

    /// <summary>ENpcBase row ID for reference.</summary>
    public uint NpcId { get; init; }

    /// <summary>TerritoryType row ID for the NPC's zone.</summary>
    public uint TerritoryTypeId { get; init; }

    /// <summary>Map sheet row ID for constructing MapLinkPayload.</summary>
    public uint MapId { get; init; }

    /// <summary>X coordinate in map-coordinate space (suitable for MapLinkPayload).</summary>
    public float MapX { get; init; }

    /// <summary>Y coordinate in map-coordinate space (suitable for MapLinkPayload).</summary>
    public float MapY { get; init; }

    /// <summary>True if map coordinates are available for showing a map pin.</summary>
    public bool HasMapCoords => MapId != 0 && TerritoryTypeId != 0;
}
