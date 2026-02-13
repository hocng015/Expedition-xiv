namespace Expedition.RecipeResolver;

/// <summary>
/// Information about a mob that drops a specific item.
/// Sourced from the Garland Tools API.
/// </summary>
public sealed class MobDropInfo
{
    /// <summary>Mob display name.</summary>
    public string MobName { get; init; } = string.Empty;

    /// <summary>Mob level or level range (e.g. "24" or "2 - 4").</summary>
    public string Level { get; init; } = string.Empty;

    /// <summary>Zone name where the mob spawns (if available).</summary>
    public string ZoneName { get; init; } = string.Empty;

    /// <summary>Garland Tools mob ID (for coordinate lookup via mob endpoint).</summary>
    public long MobId { get; init; }

    /// <summary>TerritoryType row ID for the mob's zone.</summary>
    public uint TerritoryTypeId { get; init; }

    /// <summary>Map sheet row ID for constructing MapLinkPayload.</summary>
    public uint MapId { get; init; }

    /// <summary>X coordinate in map-coordinate space (populated from mob endpoint).</summary>
    public float MapX { get; set; }

    /// <summary>Y coordinate in map-coordinate space (populated from mob endpoint).</summary>
    public float MapY { get; set; }

    /// <summary>True if enough data is available to open the map (zone + optional precise pin).</summary>
    public bool HasMapCoords => MapId != 0 && TerritoryTypeId != 0;
}
