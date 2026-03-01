namespace Expedition.Gathering;

/// <summary>
/// Defines the GBR config values to apply for optimized regular gathering.
/// These values are set via reflection into GBR's AutoGatherConfig and ConfigPreset,
/// ensuring that gathering skills, cordials, and the rotation solver are active
/// whenever Expedition drives a gathering workflow.
///
/// Unlike <see cref="Diadem.DiademSkillConfig"/>, this config omits Diadem-specific
/// settings (AetherCannon, Windmires) and focuses on universal gathering optimization.
/// </summary>
public static class GatheringSkillConfig
{
    /// <summary>
    /// AutoGatherConfig property names and values to set.
    /// These are global settings on GatherBuddy.Config.AutoGatherConfig.
    /// </summary>
    public static readonly (string Property, object Value)[] AutoGatherConfigSettings =
    [
        // Enable The Giving Land between nodes (free crystals)
        ("UseGivingLandOnCooldown", true),

        // Ensure gathering is enabled
        ("DoGathering", true),
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

        // Gift of the Land I: +1 gatherable attempt on rare node
        ("Gift1", "Enabled", true),

        // Gift of the Land II: +1 gatherable attempt on rare node
        ("Gift2", "Enabled", true),

        // Tidings: bonus yield on unspoiled/legendary nodes
        ("Tidings", "Enabled", true),
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
}
