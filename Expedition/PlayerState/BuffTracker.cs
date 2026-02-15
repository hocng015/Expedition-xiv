using Dalamud.Game.ClientState.Statuses;

namespace Expedition.PlayerState;

/// <summary>
/// Tracks food/potion/medicine buffs and warns when they expire or are missing.
///
/// Pain points addressed:
/// - Food buffs last 30-60 minutes; if they expire mid-session, stats drop
///   and crafts may fail quality thresholds or collectability targets
/// - HQ food gives significantly better stats than NQ
/// - Only one food buff active at a time (eating different food overwrites)
/// - No buff carry-over between job switches
/// - Medicine buffs are separate from food, both important for endgame
/// </summary>
public sealed class BuffTracker
{
    // Well Fed status effect ID
    private const uint WellFedStatusId = 48;

    // Common crafting food buff status IDs
    // (these vary; WellFed is the universal food buff)
    private const uint MedicineStatusId = 49;

    /// <summary>
    /// Checks if the player currently has the "Well Fed" (food) buff active.
    /// </summary>
    public bool HasFoodBuff()
    {
        return HasStatus(WellFedStatusId);
    }

    /// <summary>
    /// Gets the remaining duration of the food buff in seconds. Returns 0 if no buff.
    /// </summary>
    public float GetFoodBuffRemainingSeconds()
    {
        return GetStatusRemainingTime(WellFedStatusId);
    }

    /// <summary>
    /// Returns true if food buff is active but about to expire (within threshold).
    /// </summary>
    public bool IsFoodBuffExpiringSoon(float thresholdSeconds = 120f)
    {
        var remaining = GetFoodBuffRemainingSeconds();
        return remaining > 0 && remaining < thresholdSeconds;
    }

    /// <summary>
    /// Checks if the player has a medicine buff active.
    /// </summary>
    public bool HasMedicineBuff()
    {
        return HasStatus(MedicineStatusId);
    }

    /// <summary>
    /// Generates a diagnostic report of the player's current buff state
    /// relevant to gathering/crafting.
    /// </summary>
    public BuffDiagnostic GetDiagnostic()
    {
        var foodRemaining = GetFoodBuffRemainingSeconds();

        return new BuffDiagnostic
        {
            HasFood = foodRemaining > 0,
            FoodRemainingSeconds = foodRemaining,
            FoodExpiringSoon = foodRemaining > 0 && foodRemaining < 120,
            HasMedicine = HasMedicineBuff(),
        };
    }

    private bool HasStatus(uint statusId)
    {
        var player = DalamudApi.ClientState.LocalPlayer;
        if (player == null) return false;

        foreach (var status in player.StatusList)
        {
            if (status.StatusId == statusId && status.RemainingTime > 0)
                return true;
        }
        return false;
    }

    private float GetStatusRemainingTime(uint statusId)
    {
        var player = DalamudApi.ClientState.LocalPlayer;
        if (player == null) return 0;

        foreach (var status in player.StatusList)
        {
            if (status.StatusId == statusId)
                return status.RemainingTime;
        }
        return 0;
    }
}

public sealed class BuffDiagnostic
{
    public bool HasFood { get; init; }
    public float FoodRemainingSeconds { get; init; }
    public bool FoodExpiringSoon { get; init; }
    public bool HasMedicine { get; init; }

    /// <summary>
    /// Returns warning messages if any buffs are missing or expiring.
    /// </summary>
    public List<string> GetWarnings()
    {
        var warnings = new List<string>();

        if (!HasFood)
            warnings.Add("No food buff active. Crafting/gathering stats may be suboptimal.");

        if (FoodExpiringSoon)
            warnings.Add($"Food buff expiring in {FoodRemainingSeconds:F0}s. Consider re-eating.");

        return warnings;
    }

    public string FoodStatusText => HasFood
        ? $"Food: {FoodRemainingSeconds / 60:F1}m remaining{(FoodExpiringSoon ? " (EXPIRING)" : "")}"
        : "Food: NONE";
}
