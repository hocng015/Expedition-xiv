namespace Expedition.PlayerState;

/// <summary>
/// Tracks GP (Gathering Points) and manages cordial usage pacing.
///
/// Pain points addressed:
/// - GP regenerates at ~5 GP per tick (~3 seconds) regardless of max GP
/// - GP stops regenerating while actively at a gathering node (except Fisher)
/// - Cordials restore 300 GP (Hi-Cordial 400 GP) with cooldown timers
/// - Efficient gathering requires arriving at nodes with sufficient GP
/// - Timed nodes have narrow windows — wasting time on GP regen means fewer items
/// - Collectables require high GP spend per attempt
/// </summary>
public sealed class GpTracker
{
    private const double GpRegenPerTick = 5.0;
    private const double TickIntervalSeconds = 3.0;
    private const double GpPerSecond = GpRegenPerTick / TickIntervalSeconds;

    private const int CordialGpRestore = 300;
    private const int HiCordialGpRestore = 400;
    private const double CordialCooldownSeconds = 300.0; // 5 minutes

    private DateTime lastCordialUsedAt = DateTime.MinValue;

    /// <summary>
    /// Gets the current player GP from the game client.
    /// </summary>
    public uint GetCurrentGp()
    {
        var player = DalamudApi.ObjectTable.LocalPlayer;
        if (player == null) return 0;
        return player.CurrentGp;
    }

    /// <summary>
    /// Gets the player's max GP.
    /// </summary>
    public uint GetMaxGp()
    {
        var player = DalamudApi.ObjectTable.LocalPlayer;
        if (player == null) return 0;
        return player.MaxGp;
    }

    /// <summary>
    /// Estimates how many real-world seconds to regen from current GP to target GP.
    /// </summary>
    public double EstimateRegenTime(uint currentGp, uint targetGp)
    {
        if (currentGp >= targetGp) return 0;
        var deficit = targetGp - currentGp;
        return deficit / GpPerSecond;
    }

    /// <summary>
    /// Returns true if a cordial can be used (cooldown has elapsed).
    /// </summary>
    public bool CanUseCordial()
    {
        return (DateTime.Now - lastCordialUsedAt).TotalSeconds >= CordialCooldownSeconds;
    }

    /// <summary>
    /// Remaining cooldown in seconds before a cordial can be used.
    /// </summary>
    public double CordialCooldownRemaining()
    {
        var elapsed = (DateTime.Now - lastCordialUsedAt).TotalSeconds;
        return Math.Max(0, CordialCooldownSeconds - elapsed);
    }

    /// <summary>
    /// Records that a cordial was used (for cooldown tracking).
    /// </summary>
    public void OnCordialUsed()
    {
        lastCordialUsedAt = DateTime.Now;
    }

    /// <summary>
    /// Determines whether the player should wait for GP regen or use a cordial
    /// before starting the next gather. Returns a recommendation.
    /// </summary>
    public GpRecommendation GetRecommendation(uint currentGp, uint maxGp, uint gpNeededForNode, bool isTimedNodeActive)
    {
        if (currentGp >= gpNeededForNode)
            return new GpRecommendation { Action = GpAction.Ready };

        var deficit = gpNeededForNode - currentGp;
        var regenSeconds = deficit / GpPerSecond;

        // If a timed node is active, prefer cordial to avoid wasting the window
        if (isTimedNodeActive && CanUseCordial() && deficit <= HiCordialGpRestore)
        {
            return new GpRecommendation
            {
                Action = GpAction.UseCordial,
                WaitSeconds = 0,
                Message = $"Timed node active! Use cordial to restore {HiCordialGpRestore} GP.",
            };
        }

        if (CanUseCordial() && deficit <= HiCordialGpRestore)
        {
            return new GpRecommendation
            {
                Action = GpAction.UseCordial,
                WaitSeconds = 0,
                Message = $"Use cordial ({HiCordialGpRestore} GP). Deficit: {deficit} GP.",
            };
        }

        // Wait for regen
        return new GpRecommendation
        {
            Action = GpAction.WaitForRegen,
            WaitSeconds = regenSeconds,
            Message = $"Wait {regenSeconds:F0}s for GP regen ({deficit} GP needed).",
        };
    }
}

public sealed class GpRecommendation
{
    public GpAction Action { get; init; }
    public double WaitSeconds { get; init; }
    public string Message { get; init; } = string.Empty;
}

public enum GpAction
{
    Ready,
    UseCordial,
    WaitForRegen,
}
