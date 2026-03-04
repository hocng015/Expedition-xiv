namespace Expedition.Fishing;

/// <summary>
/// Categorizes Cosmic Exploration fishing missions by their scoring type.
/// Determines what AutoHook preset strategy should be used.
///
/// Derived from ICE's MissionAttributes flags:
///   Fish, ScoreVariety, ScoreTimeRemaining, ScoreLargestSize, Collectables, LimitedVariety
/// </summary>
public enum CosmicFishingMissionType
{
    /// <summary>
    /// Score by unique fish caught + time remaining.
    /// Strategy: Bait rotation + Surface Slap to maximize variety. Speed matters.
    /// </summary>
    VarietyTimeAttack,

    /// <summary>
    /// Score by time remaining only.
    /// Strategy: Fastest possible catch rate. One bait, no Surface Slap overhead.
    /// </summary>
    TimeAttack,

    /// <summary>
    /// Limited supplies + variety scoring.
    /// Strategy: Conservative bait rotation. Each cast counts — maximize unique fish.
    /// </summary>
    LimitedVariety,

    /// <summary>
    /// Limited supplies + largest size scoring.
    /// Strategy: Target big fish. Use appropriate hooksets for large catches.
    /// </summary>
    LimitedLargestSize,

    /// <summary>
    /// Limited supplies + collectables.
    /// Strategy: Hit collectability thresholds. Use Collector's Glove equivalent.
    /// </summary>
    LimitedCollectables,

    /// <summary>
    /// Score by largest fish size (no limited supplies).
    /// Strategy: Target big fish with strong hooksets. Size over quantity.
    /// </summary>
    LargestSize,

    /// <summary>
    /// Score by total fish count (standard missions).
    /// Strategy: Catch as fast as possible. Any fish counts.
    /// </summary>
    Standard,

    /// <summary>
    /// Fallback for unrecognized mission scoring types.
    /// Uses ICE's default presets (no override).
    /// </summary>
    Unknown,
}
