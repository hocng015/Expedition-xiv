namespace Expedition.Gathering;

/// <summary>
/// Manages the Aetherial Reduction pipeline: collectables gathered from
/// ephemeral nodes are reduced into crystals, clusters, and aethersands.
///
/// Pain points addressed:
/// - Multi-step process: gather collectable → store → reduce → hope for drops
/// - Collectability thresholds determine drop quality (higher = rarer sands)
/// - RNG-based output means we may need multiple reduction batches
/// - Items must be in inventory (not collectable turn-in) to reduce
/// - Each reduction consumes the source item
/// </summary>
public sealed class AetherialReductionManager
{
    /// <summary>
    /// Collectability thresholds for Aetherial Reduction rewards.
    /// Higher collectability = better chance of rare aethersands.
    /// </summary>
    public static readonly (int Threshold, string Reward)[] ReductionTiers =
    {
        (345, "Crystals/Clusters only"),
        (405, "Duskborne Aethersand chance"),
        (460, "Dawnborne Aethersand chance"),
        (525, "Leafborne Aethersand (rare)"),
        (1000, "Bonus aethersand (up to 10)"),
    };

    /// <summary>
    /// Determines if a material requirement can only be obtained via Aetherial Reduction.
    /// Aethersands are the primary example.
    /// </summary>
    public static bool IsAethersandItem(string itemName)
    {
        return itemName.Contains("Aethersand", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// For an aethersand material, identifies the source gatherable item
    /// and the reduction step needed. Returns the source item name and
    /// target collectability for the desired sand tier.
    /// </summary>
    public static AetherialReductionStep? GetReductionStep(uint aethersandItemId, string aethersandName)
    {
        // Aethersand-to-source mappings vary by expansion/patch.
        // In a full implementation this would be a lookup table keyed by ItemId
        // populated from game data. For now we return a placeholder that the
        // gathering orchestrator can use to gather the appropriate ephemeral collectable.
        return new AetherialReductionStep
        {
            AethersandItemId = aethersandItemId,
            AethersandName = aethersandName,
            TargetCollectability = 525,
            EstimatedYieldPerReduction = 1.5,
        };
    }

    /// <summary>
    /// Given a needed quantity of aethersand and the estimated yield per reduction,
    /// calculates how many source collectables need to be gathered.
    /// Adds a safety margin due to RNG.
    /// </summary>
    public static int EstimateSourcesNeeded(int aethersandNeeded, double yieldPerReduction, double safetyMultiplier = 1.5)
    {
        var raw = aethersandNeeded / yieldPerReduction;
        return (int)Math.Ceiling(raw * safetyMultiplier);
    }
}

public sealed class AetherialReductionStep
{
    public uint AethersandItemId { get; init; }
    public string AethersandName { get; init; } = string.Empty;

    /// <summary>The source gatherable item ID (ephemeral node collectable).</summary>
    public uint SourceItemId { get; set; }

    /// <summary>The source item name for GBR to target.</summary>
    public string SourceItemName { get; set; } = string.Empty;

    /// <summary>Target collectability to aim for during gathering.</summary>
    public int TargetCollectability { get; init; }

    /// <summary>Average yield of aethersand per source item reduced.</summary>
    public double EstimatedYieldPerReduction { get; init; }

    /// <summary>Number of source collectables to gather.</summary>
    public int SourceQuantityNeeded { get; set; }

    /// <summary>Number of reductions completed so far.</summary>
    public int ReductionsCompleted { get; set; }

    /// <summary>Aethersand obtained so far.</summary>
    public int AethersandObtained { get; set; }
}
