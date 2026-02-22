namespace Expedition.Diadem;

/// <summary>
/// Defines the GBR config values to apply for optimized Diadem gathering.
/// These values are set via reflection into GBR's AutoGatherConfig and ConfigPreset.
///
/// GBR already has a full skill system — we just need to enable/configure it:
///   - AutoGatherConfig: global settings (Windmires, AetherCannon, GivingLand)
///   - ConfigPreset: per-skill settings (rotation solver, GP thresholds, cordials)
///
/// GBR's ConfigPreset defaults already enable Bountiful, Yield2, SolidAge, GivingLand,
/// and TwelvesBounty. The main things we need to enable are:
///   1. ChooseBestActionsAutomatically (RotationSolver) — picks optimal skill order
///   2. UseGivingLandOnCooldown — uses The Giving Land between nodes for crystals
///   3. DiademAutoAetherCannon — kills Diadem mobs for Skybuilders' points
///   4. Cordial usage — GP recovery between nodes
/// </summary>
public static class DiademSkillConfig
{
    /// <summary>
    /// AutoGatherConfig property names and values to set.
    /// These are global settings on GatherBuddy.Config.AutoGatherConfig.
    /// </summary>
    public static readonly (string Property, object Value)[] AutoGatherConfigSettings =
    [
        // Enable The Giving Land between nodes (free crystals)
        ("UseGivingLandOnCooldown", true),

        // Auto-use aethercannon on Diadem enemies for Skybuilders' points
        ("DiademAutoAetherCannon", true),

        // Ensure gathering is enabled
        ("DoGathering", true),

        // Windmire jumps (already handled separately, but ensure it's on)
        ("DiademWindmireJumps", true),
    ];

    /// <summary>
    /// ConfigPreset property names and values to set on the active/default preset.
    /// The ConfigPreset controls per-node skill rotation behavior.
    /// </summary>
    public static readonly (string Property, object Value)[] PresetSettings =
    [
        // Enable automatic rotation solving — GBR picks optimal skill sequence
        ("ChooseBestActionsAutomatically", true),

        // Enable The Giving Land on this preset too
        ("UseGivingLandOnCooldown", true),
    ];

    /// <summary>
    /// Nested property paths for enabling skills within GatherableActions.
    /// Format: (ParentProperty, ChildProperty, Value)
    /// GatherableActions contains: Bountiful, Yield1, Yield2, SolidAge, TwelvesBounty,
    ///                             GivingLand, Gift1, Gift2, Tidings
    /// </summary>
    public static readonly (string ActionName, string Property, object Value)[] GatheringSkillSettings =
    [
        // Bountiful Yield/Harvest: yield +1-3 based on Gathering stat
        ("Bountiful", "Enabled", true),

        // King's Yield II / Blessed Harvest II: yield +1
        ("Yield2", "Enabled", true),

        // Solid Age: restores node integrity for more gather attempts
        ("SolidAge", "Enabled", true),

        // The Giving Land: gathers random crystals
        ("GivingLand", "Enabled", true),

        // Twelve's Bounty: crystal generation
        ("TwelvesBounty", "Enabled", true),
    ];

    /// <summary>
    /// Consumable settings to enable on the preset.
    /// Consumables contains: Cordial, Food, Potion, Manual, SquadronManual, SquadronPass
    /// </summary>
    public static readonly (string ConsumableName, string Property, object Value)[] ConsumableSettings =
    [
        // Enable cordial usage for GP recovery between nodes
        ("Cordial", "Enabled", true),
    ];

    /// <summary>
    /// Returns a human-readable summary of what the preset does.
    /// </summary>
    public static string GetPresetSummary()
    {
        return "Enables GBR's automatic rotation solver, The Giving Land (crystals), " +
               "Bountiful Yield, King's Yield II, Solid Age, Twelve's Bounty, " +
               "cordial usage, and Aether Cannon for Diadem mobs.";
    }
}
