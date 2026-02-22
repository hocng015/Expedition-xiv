using System.Collections.Immutable;
using System.Numerics;

namespace Expedition.Diadem;

/// <summary>
/// Static database of all Diadem Windmire (tornado fast-travel) positions.
/// Windmires are proximity-triggered invisible zones that catapult the player
/// between island platforms. Each entry is a one-way (From → To) pair.
/// The 18 entries form 9 bidirectional connections.
///
/// Data sourced from GatherBuddyReborn's Diadem.cs.
/// Territory ID 939 = The Diadem (gathering instance).
/// </summary>
public static class DiademWindmires
{
    public const ushort DiademTerritoryId = 939;

    /// <summary>
    /// All 18 Windmire jump pairs. Each entry is (From, To) where:
    /// - From = the position the player must fly to in order to trigger the catapult
    /// - To = the position the player lands at after the catapult
    /// </summary>
    public static ImmutableArray<(Vector3 From, Vector3 To)> All { get; } =
    [
        // Pair 0-1: Northwest islands ↔ North-central island
        (new Vector3(-724.649f, 270.846f, -27.428f), new Vector3(-540.327f, 317.677f, 323.471f)),
        (new Vector3(-558.688f, 318.068f, 308.976f), new Vector3(-723.915f, 271.008f, -49.867f)),

        // Pair 2-3: North-central ↔ Central island
        (new Vector3(-287.557f, 318.234f, 558.728f), new Vector3(-118.239f, 114.119f, 537.373f)),
        (new Vector3(-140.415f, 113.127f, 519.463f), new Vector3(-311.086f, 317.677f, 560.815f)),

        // Pair 4-12: Central ↔ East-central (one-way pair split)
        (new Vector3(274.797f, 85.048f, 470.526f), new Vector3(359.416f, -4.959f, 450.553f)),

        // 5-17: East-central ↔ East island
        (new Vector3(467.156f, -25.674f, 187.769f), new Vector3(661.631f, 223.636f, -57.878f)),

        // 6-16: East ↔ East-southeast
        (new Vector3(713.130f, 218.108f, -334.355f), new Vector3(640.518f, 251.972f, -401.531f)),

        // 7-15: East-southeast ↔ Southeast
        (new Vector3(603.883f, 251.803f, -411.563f), new Vector3(546.746f, 192.386f, -516.838f)),

        // 8-14: Southeast ↔ South
        (new Vector3(469.004f, 191.349f, -667.111f), new Vector3(388.159f, 290.028f, -713.512f)),

        // 9-13: South ↔ Southwest
        (new Vector3(177.985f, 292.541f, -737.720f), new Vector3(129.320f, -49.587f, -512.261f)),

        // 10-11: West lower ↔ West upper
        (new Vector3(-590.303f, 33.309f, -230.903f), new Vector3(-623.508f, 282.778f, -175.324f)),
        (new Vector3(-629.557f, 280.432f, -187.958f), new Vector3(-575.189f, 34.246f, -236.601f)),

        // Reverse of pair 4: East-central → Central
        (new Vector3(340.000f, -4.986f, 471.190f), new Vector3(257.618f, 86.381f, 487.877f)),

        // Reverse of pair 9: Southwest → South
        (new Vector3(137.765f, -50.296f, -481.785f), new Vector3(203.764f, 293.528f, -736.416f)),

        // Reverse of pair 8: South → Southeast
        (new Vector3(434.434f, 289.603f, -735.568f), new Vector3(473.259f, 200.712f, -636.561f)),

        // Reverse of pair 7: Southeast → East-southeast
        (new Vector3(528.247f, 189.653f, -494.262f), new Vector3(624.109f, 251.972f, -423.270f)),

        // Reverse of pair 6: East-southeast → East
        (new Vector3(636.711f, 251.792f, -389.120f), new Vector3(713.730f, 219.938f, -311.411f)),

        // Reverse of pair 5: East → East-central
        (new Vector3(657.981f, 222.430f, -33.609f), new Vector3(466.911f, -23.926f, 202.930f)),
    ];

    /// <summary>
    /// Returns true if the player is currently in the Diadem (Territory 939).
    /// </summary>
    public static bool IsInDiadem()
        => DalamudApi.ClientState.TerritoryType == DiademTerritoryId;

    /// <summary>
    /// Finds the optimal Windmire for traveling from <paramref name="playerPos"/>
    /// to <paramref name="destination"/>. Returns null if flying direct is faster
    /// (uses the 2x distance advantage heuristic from GBR).
    /// </summary>
    /// <returns>
    /// The best Windmire (From, To) pair, or null if direct flight is shorter.
    /// </returns>
    public static (Vector3 From, Vector3 To)? FindBestWindmire(Vector3 playerPos, Vector3 destination)
    {
        var directDistance = Vector3.Distance(playerPos, destination);

        (Vector3 From, Vector3 To)? best = null;
        var bestTotalDist = float.MaxValue;

        foreach (var windmire in All)
        {
            var totalDist = Vector3.Distance(playerPos, windmire.From)
                          + Vector3.Distance(windmire.To, destination);

            if (totalDist < bestTotalDist)
            {
                bestTotalDist = totalDist;
                best = windmire;
            }
        }

        // Only use the Windmire if the route is at least 2x shorter than flying direct
        if (best.HasValue && bestTotalDist * 2f < directDistance)
            return best;

        return null;
    }

    /// <summary>
    /// Gets the nearest Windmire entry point to the player's current position.
    /// Useful for "fly to nearest tornado" quick-nav.
    /// </summary>
    public static (Vector3 From, Vector3 To)? FindNearestWindmire(Vector3 playerPos)
    {
        (Vector3 From, Vector3 To)? nearest = null;
        var nearestDist = float.MaxValue;

        foreach (var windmire in All)
        {
            var dist = Vector3.Distance(playerPos, windmire.From);
            if (dist < nearestDist)
            {
                nearestDist = dist;
                nearest = windmire;
            }
        }

        return nearest;
    }

    /// <summary>
    /// Returns the distance from a position to the nearest Windmire entry point.
    /// </summary>
    public static float DistanceToNearestWindmire(Vector3 pos)
    {
        var min = float.MaxValue;
        foreach (var windmire in All)
        {
            var dist = Vector3.Distance(pos, windmire.From);
            if (dist < min) min = dist;
        }
        return min;
    }
}
