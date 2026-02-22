using Expedition.PlayerState;

using InventoryManager_Game = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager;

namespace Expedition.Diadem;

/// <summary>
/// Tracks a Diadem gathering session: duration, items gathered (inventory delta),
/// XP gains, and derived rate metrics (items/hour, XP/hour, time-to-level).
/// </summary>
public sealed class DiademSession
{
    // --- Session lifecycle ---
    public bool IsActive { get; private set; }
    public DateTime StartTime { get; private set; }
    private TimeSpan stoppedDuration;

    public TimeSpan Elapsed => IsActive ? DateTime.Now - StartTime : stoppedDuration;

    // --- Tracked items ---
    private readonly List<uint> trackedItemIds = new();
    private readonly Dictionary<uint, int> baselineInventory = new();

    // --- XP tracking ---
    private int baselineMinXp;
    private int baselineBtnXp;
    private int baselineMinLevel;
    private int baselineBtnLevel;
    private int baselineMinTotalXp; // Cumulative XP up to baseline level
    private int baselineBtnTotalXp;

    // --- Current state (updated each poll) ---
    public int CurrentMinLevel { get; private set; }
    public int CurrentBtnLevel { get; private set; }
    public int CurrentMinXp { get; private set; }
    public int CurrentBtnXp { get; private set; }
    public int MinXpToNextLevel { get; private set; }
    public int BtnXpToNextLevel { get; private set; }

    // --- Computed metrics ---
    public int TotalItemsGathered { get; private set; }
    public int MinXpGained { get; private set; }
    public int BtnXpGained { get; private set; }

    public double ItemsPerHour => Elapsed.TotalHours > 0.01 ? TotalItemsGathered / Elapsed.TotalHours : 0;
    public double MinXpPerHour => Elapsed.TotalHours > 0.01 ? MinXpGained / Elapsed.TotalHours : 0;
    public double BtnXpPerHour => Elapsed.TotalHours > 0.01 ? BtnXpGained / Elapsed.TotalHours : 0;

    public TimeSpan? EstMinTimeToLevel
    {
        get
        {
            if (MinXpPerHour <= 0 || MinXpToNextLevel <= 0) return null;
            var remaining = MinXpToNextLevel - CurrentMinXp;
            if (remaining <= 0) return TimeSpan.Zero;
            return TimeSpan.FromHours(remaining / MinXpPerHour);
        }
    }

    public TimeSpan? EstBtnTimeToLevel
    {
        get
        {
            if (BtnXpPerHour <= 0 || BtnXpToNextLevel <= 0) return null;
            var remaining = BtnXpToNextLevel - CurrentBtnXp;
            if (remaining <= 0) return TimeSpan.Zero;
            return TimeSpan.FromHours(remaining / BtnXpPerHour);
        }
    }

    // --- Throttle ---
    private DateTime lastUpdate = DateTime.MinValue;
    private const double UpdateIntervalSeconds = 1.0;

    /// <summary>
    /// Starts a new session, capturing baseline inventory and XP.
    /// </summary>
    public void Start(IReadOnlyList<uint> itemIds)
    {
        IsActive = true;
        StartTime = DateTime.Now;
        stoppedDuration = TimeSpan.Zero;
        TotalItemsGathered = 0;
        MinXpGained = 0;
        BtnXpGained = 0;

        trackedItemIds.Clear();
        trackedItemIds.AddRange(itemIds);

        // Capture baseline inventory
        baselineInventory.Clear();
        foreach (var id in itemIds)
        {
            baselineInventory[id] = GetInventoryCount(id);
        }

        // Capture baseline XP
        baselineMinLevel = JobSwitchManager.GetPlayerJobLevel(JobSwitchManager.MIN);
        baselineBtnLevel = JobSwitchManager.GetPlayerJobLevel(JobSwitchManager.BTN);
        baselineMinXp = JobSwitchManager.GetPlayerJobExperience(JobSwitchManager.MIN);
        baselineBtnXp = JobSwitchManager.GetPlayerJobExperience(JobSwitchManager.BTN);

        // Compute cumulative XP up to baseline level
        baselineMinTotalXp = ComputeCumulativeXp(baselineMinLevel, baselineMinXp);
        baselineBtnTotalXp = ComputeCumulativeXp(baselineBtnLevel, baselineBtnXp);

        // Set initial current state
        CurrentMinLevel = baselineMinLevel;
        CurrentBtnLevel = baselineBtnLevel;
        CurrentMinXp = baselineMinXp;
        CurrentBtnXp = baselineBtnXp;
        MinXpToNextLevel = JobSwitchManager.GetXpToNextLevel(baselineMinLevel);
        BtnXpToNextLevel = JobSwitchManager.GetXpToNextLevel(baselineBtnLevel);

        lastUpdate = DateTime.Now;
        DalamudApi.Log.Information(
            $"[Diadem] Session started. Tracking {itemIds.Count} items. " +
            $"MIN Lv{baselineMinLevel} ({baselineMinXp} XP), BTN Lv{baselineBtnLevel} ({baselineBtnXp} XP)");
    }

    /// <summary>
    /// Stops the session, freezing the elapsed time.
    /// </summary>
    public void Stop()
    {
        if (!IsActive) return;
        stoppedDuration = DateTime.Now - StartTime;
        IsActive = false;
        DalamudApi.Log.Information(
            $"[Diadem] Session stopped. Duration: {stoppedDuration:hh\\:mm\\:ss}. " +
            $"Items gathered: {TotalItemsGathered}. MIN XP gained: {MinXpGained}. BTN XP gained: {BtnXpGained}.");
    }

    /// <summary>
    /// Polls inventory and player state. Should be called from Draw() — self-throttled to ~1s.
    /// </summary>
    public void Update()
    {
        if (!IsActive) return;

        var now = DateTime.Now;
        if ((now - lastUpdate).TotalSeconds < UpdateIntervalSeconds) return;
        lastUpdate = now;

        // Update inventory deltas
        var total = 0;
        foreach (var id in trackedItemIds)
        {
            var current = GetInventoryCount(id);
            var baseline = baselineInventory.GetValueOrDefault(id, 0);
            var delta = current - baseline;
            if (delta > 0) total += delta;
        }
        TotalItemsGathered = total;

        // Update XP
        CurrentMinLevel = JobSwitchManager.GetPlayerJobLevel(JobSwitchManager.MIN);
        CurrentBtnLevel = JobSwitchManager.GetPlayerJobLevel(JobSwitchManager.BTN);
        CurrentMinXp = JobSwitchManager.GetPlayerJobExperience(JobSwitchManager.MIN);
        CurrentBtnXp = JobSwitchManager.GetPlayerJobExperience(JobSwitchManager.BTN);
        MinXpToNextLevel = JobSwitchManager.GetXpToNextLevel(CurrentMinLevel);
        BtnXpToNextLevel = JobSwitchManager.GetXpToNextLevel(CurrentBtnLevel);

        // Compute XP gained (handles level-ups)
        var currentMinTotalXp = ComputeCumulativeXp(CurrentMinLevel, CurrentMinXp);
        var currentBtnTotalXp = ComputeCumulativeXp(CurrentBtnLevel, CurrentBtnXp);
        MinXpGained = Math.Max(0, currentMinTotalXp - baselineMinTotalXp);
        BtnXpGained = Math.Max(0, currentBtnTotalXp - baselineBtnTotalXp);
    }

    /// <summary>
    /// Gets the inventory delta for a specific item since session start.
    /// </summary>
    public int GetItemDelta(uint itemId)
    {
        var current = GetInventoryCount(itemId);
        var baseline = baselineInventory.GetValueOrDefault(itemId, 0);
        return Math.Max(0, current - baseline);
    }

    /// <summary>
    /// Computes cumulative XP = sum of XpToNextLevel for levels 1..(level-1) + currentXpInLevel.
    /// This handles level-up tracking correctly.
    /// </summary>
    private static int ComputeCumulativeXp(int level, int currentXp)
    {
        if (level <= 0 || currentXp < 0) return 0;

        var cumulative = 0;
        for (var lv = 1; lv < level; lv++)
        {
            cumulative += JobSwitchManager.GetXpToNextLevel(lv);
        }
        return cumulative + Math.Max(0, currentXp);
    }

    /// <summary>
    /// Gets the count of an item across all inventory containers.
    /// </summary>
    private static unsafe int GetInventoryCount(uint itemId)
    {
        try
        {
            var manager = InventoryManager_Game.Instance();
            if (manager == null) return 0;
            return manager->GetInventoryItemCount(itemId);
        }
        catch
        {
            return 0;
        }
    }
}
