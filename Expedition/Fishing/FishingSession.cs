using System;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;

using Expedition.IPC;
using Expedition.PlayerState;

namespace Expedition.Fishing;

public enum FishingState
{
    Idle,
    ValidatingPrereqs,
    NavigatingToSpot,
    PreFishing,
    Fishing,
    WaitingForGp,
    Stopped,
    Error,
}

/// <summary>
/// Core fishing state machine. Ticked from OnFrameworkUpdate.
/// Handles: finding spots, navigating, buff management, GP tracking, and session stats.
/// Delegates hookset selection and auto-re-cast to AutoHook.
/// </summary>
public sealed class FishingSession : IDisposable
{
    private readonly VnavmeshIpc _vnavmesh;
    private readonly AutoHookIpc _autoHook;
    private readonly GpTracker _gpTracker = new();

    public FishingState State { get; private set; } = FishingState.Idle;
    public string StatusMessage { get; private set; } = string.Empty;
    public DateTime? StartTime { get; private set; }
    public int TotalCatches { get; private set; }
    public FishingSpotResult? TargetSpot { get; private set; }

    // Throttles
    private DateTime _lastUpdate;
    private DateTime _lastBuffCheck;
    private const double UpdateIntervalSec = 0.5;
    private const double BuffCheckIntervalSec = 5.0;

    // Navigation
    private DateTime? _navStartTime;
    private const double NavTimeoutSec = 60.0;
    private const float SpotArrivalDistance = 5f;

    // Fishing tracking
    private bool _wasFishing;
    private DateTime? _lastCastTime;

    // Pre-fishing action queue
    private DateTime? _lastActionTime;
    private const double ActionDelaySec = 1.5;
    private int _preFishingStep;

    // GP waiting
    private int _gpNeededForBuffs;

    public FishingSession(VnavmeshIpc vnavmesh, AutoHookIpc autoHook)
    {
        _vnavmesh = vnavmesh;
        _autoHook = autoHook;
    }

    public bool IsActive => State != FishingState.Idle
                         && State != FishingState.Stopped
                         && State != FishingState.Error;

    public void Start()
    {
        if (IsActive)
        {
            DalamudApi.Log.Warning("[Fishing] Session already active.");
            return;
        }

        TotalCatches = 0;
        StartTime = DateTime.UtcNow;
        _wasFishing = false;
        _preFishingStep = 0;
        _lastActionTime = null;
        _lastCastTime = null;

        TransitionTo(FishingState.ValidatingPrereqs, "Validating prerequisites...");
    }

    public void Stop()
    {
        if (State == FishingState.Idle || State == FishingState.Stopped) return;

        _vnavmesh.Stop();
        TransitionTo(FishingState.Stopped, $"Stopped. {TotalCatches} catches in {GetDurationString()}.");
        DalamudApi.Log.Information($"[Fishing] Session stopped. {TotalCatches} catches.");
    }

    public void Update()
    {
        if (!IsActive) return;

        var now = DateTime.UtcNow;

        // Navigation doesn't need throttling — vnavmesh handles it
        if (State == FishingState.NavigatingToSpot)
        {
            TickNavigating();
            return;
        }

        // Throttle other updates
        if ((now - _lastUpdate).TotalSeconds < UpdateIntervalSec) return;
        _lastUpdate = now;

        try
        {
            switch (State)
            {
                case FishingState.ValidatingPrereqs:
                    TickValidatingPrereqs();
                    break;
                case FishingState.PreFishing:
                    TickPreFishing();
                    break;
                case FishingState.Fishing:
                    TickFishing();
                    break;
                case FishingState.WaitingForGp:
                    TickWaitingForGp();
                    break;
            }
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Error($"[Fishing] Error: {ex.Message}");
            TransitionTo(FishingState.Error, $"Error: {ex.Message}");
        }
    }

    // ─── State Ticks ──────────────────────────────

    private void TickValidatingPrereqs()
    {
        var player = DalamudApi.ObjectTable.LocalPlayer;
        if (player == null)
        {
            TransitionTo(FishingState.Error, "Player not found.");
            return;
        }

        // Fisher = ClassJob 18
        if (player.ClassJob.RowId != 18)
        {
            TransitionTo(FishingState.Error, "Must be on Fisher (FSH) class.");
            return;
        }

        // Check AutoHook
        _autoHook.CheckAvailability();
        if (!_autoHook.IsAvailable)
        {
            TransitionTo(FishingState.Error, "AutoHook plugin required but not loaded.");
            return;
        }

        // Find nearest fishing spot
        var spot = FishingSpotFinder.FindNearest();
        if (spot == null)
        {
            TransitionTo(FishingState.Error, "No fishing spot found nearby (150y range).");
            return;
        }

        TargetSpot = spot;
        DalamudApi.Log.Information($"[Fishing] Found spot: {spot.Name} ({spot.Distance:F1}y away)");

        if (spot.Distance <= SpotArrivalDistance)
        {
            _preFishingStep = 0;
            _lastActionTime = null;
            TransitionTo(FishingState.PreFishing, "Preparing to fish...");
        }
        else
        {
            if (!_vnavmesh.IsAvailable)
            {
                TransitionTo(FishingState.Error, "vnavmesh required for navigation but not available.");
                return;
            }

            TransitionTo(FishingState.NavigatingToSpot, $"Moving to {spot.Name}...");
            _navStartTime = DateTime.UtcNow;
            _vnavmesh.PathfindAndMoveTo(spot.Position, false);
        }
    }

    private void TickNavigating()
    {
        if (TargetSpot == null)
        {
            TransitionTo(FishingState.Error, "Lost target fishing spot.");
            return;
        }

        var player = DalamudApi.ObjectTable.LocalPlayer;
        if (player == null) return;

        var dist = Vector3.Distance(player.Position, TargetSpot.Position);
        StatusMessage = $"Moving to {TargetSpot.Name} ({dist:F0}y)...";

        if (dist <= SpotArrivalDistance)
        {
            _vnavmesh.Stop();
            _preFishingStep = 0;
            _lastActionTime = null;
            TransitionTo(FishingState.PreFishing, "Arrived. Preparing to fish...");
            return;
        }

        // Timeout
        if (_navStartTime.HasValue && (DateTime.UtcNow - _navStartTime.Value).TotalSeconds > NavTimeoutSec)
        {
            _vnavmesh.Stop();
            TransitionTo(FishingState.Error, "Navigation timed out (60s).");
            return;
        }

        // Re-attempt if vnavmesh stopped
        if (!_vnavmesh.IsPathRunning() && !_vnavmesh.IsPathfindInProgress())
        {
            _vnavmesh.PathfindAndMoveTo(TargetSpot.Position, false);
        }
    }

    private void TickPreFishing()
    {
        var condition = DalamudApi.Condition;
        var config = Expedition.Config;

        // Wait if busy
        if (condition[ConditionFlag.Casting] || condition[ConditionFlag.Occupied])
            return;

        // Action cooldown
        if (_lastActionTime.HasValue && (DateTime.UtcNow - _lastActionTime.Value).TotalSeconds < ActionDelaySec)
            return;

        var gp = _gpTracker.GetCurrentGp();

        switch (_preFishingStep)
        {
            case 0:
                // Dismount if mounted
                if (condition[ConditionFlag.Mounted])
                {
                    unsafe
                    {
                        var am = FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Instance();
                        am->UseAction(FFXIVClientStructs.FFXIV.Client.Game.ActionType.GeneralAction, 23);
                    }
                    _lastActionTime = DateTime.UtcNow;
                    return;
                }
                _preFishingStep = 1;
                goto case 1;

            case 1:
                // Patience II (buff ID 850)
                if (config.FishingUsePatienceII && gp >= 560 && !HasBuff(850))
                {
                    if (FishingActionManager.CanUseAction(FishingActionManager.PatienceII))
                    {
                        FishingActionManager.UseAction(FishingActionManager.PatienceII);
                        DalamudApi.Log.Information("[Fishing] Applied Patience II.");
                        _lastActionTime = DateTime.UtcNow;
                        return;
                    }
                }
                _preFishingStep = 2;
                goto case 2;

            case 2:
                // Chum (buff ID 763)
                if (config.FishingUseChum && gp >= 100 && !HasBuff(763))
                {
                    if (FishingActionManager.CanUseAction(FishingActionManager.Chum))
                    {
                        FishingActionManager.UseAction(FishingActionManager.Chum);
                        DalamudApi.Log.Information("[Fishing] Applied Chum.");
                        _lastActionTime = DateTime.UtcNow;
                        return;
                    }
                }
                _preFishingStep = 3;
                goto case 3;

            case 3:
                // Cast
                if (condition[ConditionFlag.Fishing])
                {
                    _wasFishing = true;
                    TransitionTo(FishingState.Fishing, "Fishing...");
                    return;
                }

                if (FishingActionManager.CanUseAction(FishingActionManager.Cast))
                {
                    FishingActionManager.UseAction(FishingActionManager.Cast);
                    _lastCastTime = DateTime.UtcNow;
                    _wasFishing = false;
                    DalamudApi.Log.Information("[Fishing] Cast line.");
                    TransitionTo(FishingState.Fishing, "Fishing...");
                }
                else
                {
                    StatusMessage = "Waiting to cast...";
                }
                break;
        }
    }

    private void TickFishing()
    {
        var condition = DalamudApi.Condition;
        var config = Expedition.Config;
        var isFishingNow = condition[ConditionFlag.Fishing];

        // Transition: was fishing -> not fishing = catch
        if (_wasFishing && !isFishingNow)
        {
            TotalCatches++;
            _wasFishing = false;
            DalamudApi.Log.Information($"[Fishing] Catch #{TotalCatches}");

            // Check if buffs need reapplication
            var now = DateTime.UtcNow;
            if ((now - _lastBuffCheck).TotalSeconds >= BuffCheckIntervalSec)
            {
                _lastBuffCheck = now;
                var gp = _gpTracker.GetCurrentGp();
                var needPatienceII = config.FishingUsePatienceII && !HasBuff(850) && gp >= 560;
                var needChum = config.FishingUseChum && !HasBuff(763) && gp >= 100;

                if (needPatienceII || needChum)
                {
                    _preFishingStep = needPatienceII ? 1 : 2;
                    _lastActionTime = null;
                    TransitionTo(FishingState.PreFishing, "Reapplying buffs...");
                    return;
                }

                // GP management
                if (config.FishingUseThaliaksFavor && gp < 200)
                {
                    _gpNeededForBuffs = CalculateGpNeeded(config);
                    if (_gpNeededForBuffs > gp)
                    {
                        TransitionTo(FishingState.WaitingForGp, $"Waiting for GP ({gp}/{_gpNeededForBuffs})...");
                        return;
                    }
                }
            }

            StatusMessage = $"Fishing... ({TotalCatches} caught, {GetCatchRate():F1}/hr)";
            return;
        }

        // Transition: not fishing -> fishing
        if (!_wasFishing && isFishingNow)
        {
            _wasFishing = true;
            StatusMessage = $"Fishing... ({TotalCatches} caught)";
        }

        // Stall detection: if not fishing for >10s, try re-casting
        if (!isFishingNow && !_wasFishing)
        {
            if (_lastCastTime.HasValue && (DateTime.UtcNow - _lastCastTime.Value).TotalSeconds > 10)
            {
                if (!condition[ConditionFlag.Casting] && !condition[ConditionFlag.Occupied])
                {
                    _preFishingStep = 1;
                    _lastActionTime = null;
                    TransitionTo(FishingState.PreFishing, "Re-casting...");
                }
            }
        }
    }

    private void TickWaitingForGp()
    {
        var gp = _gpTracker.GetCurrentGp();
        var config = Expedition.Config;

        // Try Thaliak's Favor
        if (config.FishingUseThaliaksFavor && FishingActionManager.CanUseAction(FishingActionManager.ThaliaksFavor))
        {
            FishingActionManager.UseAction(FishingActionManager.ThaliaksFavor);
            DalamudApi.Log.Information("[Fishing] Used Thaliak's Favor.");
            _lastActionTime = DateTime.UtcNow;
        }

        StatusMessage = $"Waiting for GP ({gp}/{_gpNeededForBuffs})...";

        if (gp >= _gpNeededForBuffs)
        {
            _preFishingStep = 1;
            _lastActionTime = null;
            TransitionTo(FishingState.PreFishing, "GP recovered. Preparing to fish...");
        }
    }

    // ─── Helpers ──────────────────────────────────

    private void TransitionTo(FishingState newState, string message)
    {
        DalamudApi.Log.Information($"[Fishing] {State} -> {newState}: {message}");
        State = newState;
        StatusMessage = message;
    }

    private static bool HasBuff(uint statusId)
    {
        var player = DalamudApi.ObjectTable.LocalPlayer;
        if (player == null) return false;

        foreach (var status in player.StatusList)
        {
            if (status.StatusId == statusId)
                return true;
        }
        return false;
    }

    private static int CalculateGpNeeded(Configuration config)
    {
        var needed = 0;
        if (config.FishingUsePatienceII) needed += 560;
        if (config.FishingUseChum) needed += 100;
        return needed > 0 ? needed : 100;
    }

    public string GetDurationString()
    {
        if (!StartTime.HasValue) return "0:00";
        var elapsed = DateTime.UtcNow - StartTime.Value;
        return $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:D2}";
    }

    public float GetCatchRate()
    {
        if (!StartTime.HasValue || TotalCatches == 0) return 0f;
        var hours = (DateTime.UtcNow - StartTime.Value).TotalHours;
        return hours > 0 ? (float)(TotalCatches / hours) : 0f;
    }

    public void Dispose()
    {
        if (IsActive) Stop();
    }
}
