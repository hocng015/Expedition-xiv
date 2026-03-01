using Dalamud.Plugin.Ipc;

namespace Expedition.IPC;

/// <summary>
/// IPC subscriber for ICE (Ice's Cosmic Exploration) plugin.
/// Wraps ICE's CallGate endpoints to control Cosmic Exploration automation
/// from Expedition. All IPC names follow the pattern "ICE.{Method}".
///
/// ICE handles the full Cosmic Exploration workflow: mission selection, gathering,
/// crafting (via Artisan), fishing (via AutoHook), navigation (via VNavmesh),
/// turn-in, and re-rolling. Expedition orchestrates when/how ICE runs.
/// </summary>
public sealed class CosmicIpc : IDisposable
{
    // --- Query endpoints ---
    private readonly ICallGateSubscriber<bool> isRunning;
    private readonly ICallGateSubscriber<string> currentState;
    private readonly ICallGateSubscriber<uint> currentMission;

    // --- Control endpoints ---
    private readonly ICallGateSubscriber<object> enable;
    private readonly ICallGateSubscriber<object> disable;

    // --- Mission management ---
    private readonly ICallGateSubscriber<HashSet<uint>, object> addMissions;
    private readonly ICallGateSubscriber<HashSet<uint>, object> removeMissions;
    private readonly ICallGateSubscriber<HashSet<uint>, object> toggleMissions;
    private readonly ICallGateSubscriber<HashSet<uint>, object> onlyMissions;
    private readonly ICallGateSubscriber<object> clearAllMissions;
    private readonly ICallGateSubscriber<uint, object> flagMissionArea;

    // --- Settings ---
    private readonly ICallGateSubscriber<string, bool, object> changeSetting;
    private readonly ICallGateSubscriber<string, int, object> changeSettingAmount;

    // --- Mode (requires modified ICE with ChangeMode/GetMode IPC) ---
    private readonly ICallGateSubscriber<int, object> changeMode;
    private readonly ICallGateSubscriber<int> getMode;

    /// <summary>
    /// True if the ICE IPC endpoints responded during the last availability check.
    /// </summary>
    public bool IsAvailable { get; private set; }

    public CosmicIpc()
    {
        var pi = DalamudApi.PluginInterface;

        // Query
        isRunning = pi.GetIpcSubscriber<bool>("ICE.IsRunning");
        currentState = pi.GetIpcSubscriber<string>("ICE.CurrentState");
        currentMission = pi.GetIpcSubscriber<uint>("ICE.CurrentMission");

        // Control
        enable = pi.GetIpcSubscriber<object>("ICE.Enable");
        disable = pi.GetIpcSubscriber<object>("ICE.Disable");

        // Mission management
        addMissions = pi.GetIpcSubscriber<HashSet<uint>, object>("ICE.AddMissions");
        removeMissions = pi.GetIpcSubscriber<HashSet<uint>, object>("ICE.RemoveMissions");
        toggleMissions = pi.GetIpcSubscriber<HashSet<uint>, object>("ICE.ToggleMissions");
        onlyMissions = pi.GetIpcSubscriber<HashSet<uint>, object>("ICE.OnlyMissions");
        clearAllMissions = pi.GetIpcSubscriber<object>("ICE.ClearAllMissions");
        flagMissionArea = pi.GetIpcSubscriber<uint, object>("ICE.FlagMissionArea");

        // Settings
        changeSetting = pi.GetIpcSubscriber<string, bool, object>("ICE.ChangeSetting");
        changeSettingAmount = pi.GetIpcSubscriber<string, int, object>("ICE.ChangeSettingAmount");

        // Mode (custom IPC added to our forked ICE)
        changeMode = pi.GetIpcSubscriber<int, object>("ICE.ChangeMode");
        getMode = pi.GetIpcSubscriber<int>("ICE.GetMode");

        CheckAvailability();
    }

    /// <summary>
    /// Probes ICE IPC by calling IsRunning. Updates <see cref="IsAvailable"/>.
    /// </summary>
    public void CheckAvailability()
    {
        try
        {
            isRunning.InvokeFunc();
            IsAvailable = true;
        }
        catch
        {
            IsAvailable = false;
        }
    }

    // ─── Query Methods ───────────────────────────

    /// <summary>
    /// Returns true if ICE is actively running (state != Idle).
    /// </summary>
    public bool GetIsRunning()
    {
        if (!IsAvailable) return false;
        try { return isRunning.InvokeFunc(); }
        catch { return false; }
    }

    /// <summary>
    /// Returns the current ICE state as a string (e.g., "Idle", "Gather", "Craft", "GrabMission").
    /// </summary>
    public string GetCurrentState()
    {
        if (!IsAvailable) return "Unavailable";
        try { return currentState.InvokeFunc(); }
        catch { return "Error"; }
    }

    /// <summary>
    /// Returns the mission ID currently being executed, or 0 if none.
    /// </summary>
    public uint GetCurrentMission()
    {
        if (!IsAvailable) return 0;
        try { return currentMission.InvokeFunc(); }
        catch { return 0; }
    }

    // ─── Control Methods ─────────────────────────

    /// <summary>
    /// Starts ICE's automation loop. Equivalent to /ice start.
    /// </summary>
    public bool Enable()
    {
        if (!IsAvailable) return false;
        try { enable.InvokeAction(); return true; }
        catch (Exception ex)
        {
            DalamudApi.Log.Warning($"[Cosmic] ICE Enable failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Stops ICE's automation loop. Equivalent to /ice stop.
    /// </summary>
    public bool Disable()
    {
        if (!IsAvailable) return false;
        try { disable.InvokeAction(); return true; }
        catch (Exception ex)
        {
            DalamudApi.Log.Warning($"[Cosmic] ICE Disable failed: {ex.Message}");
            return false;
        }
    }

    // ─── Mission Management ──────────────────────

    /// <summary>
    /// Enables the given missions (by mission ID) in ICE's mission config.
    /// </summary>
    public bool AddMissions(HashSet<uint> missionIds)
    {
        if (!IsAvailable || missionIds.Count == 0) return false;
        try { addMissions.InvokeAction(missionIds); return true; }
        catch (Exception ex)
        {
            DalamudApi.Log.Warning($"[Cosmic] AddMissions failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Disables the given missions in ICE's config.
    /// </summary>
    public bool RemoveMissions(HashSet<uint> missionIds)
    {
        if (!IsAvailable || missionIds.Count == 0) return false;
        try { removeMissions.InvokeAction(missionIds); return true; }
        catch (Exception ex)
        {
            DalamudApi.Log.Warning($"[Cosmic] RemoveMissions failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Disables ALL missions, then enables ONLY the given ones.
    /// Best for configuring a clean mission set from Expedition.
    /// </summary>
    public bool SetOnlyMissions(HashSet<uint> missionIds)
    {
        if (!IsAvailable) return false;
        try { onlyMissions.InvokeAction(missionIds); return true; }
        catch (Exception ex)
        {
            DalamudApi.Log.Warning($"[Cosmic] OnlyMissions failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Disables all missions in ICE.
    /// </summary>
    public bool ClearAllMissions()
    {
        if (!IsAvailable) return false;
        try { clearAllMissions.InvokeAction(); return true; }
        catch (Exception ex)
        {
            DalamudApi.Log.Warning($"[Cosmic] ClearAllMissions failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Opens the map and flags the gathering area for the given mission ID.
    /// </summary>
    public bool FlagMissionArea(uint missionId)
    {
        if (!IsAvailable) return false;
        try { flagMissionArea.InvokeAction(missionId); return true; }
        catch (Exception ex)
        {
            DalamudApi.Log.Warning($"[Cosmic] FlagMissionArea failed: {ex.Message}");
            return false;
        }
    }

    // ─── Settings ────────────────────────────────

    /// <summary>
    /// Changes a boolean setting in ICE.
    /// Valid keys: "OnlyGrabMission", "StopAfterCurrent", "StopOnceHitCosmoCredits",
    ///             "StopOnceHitLunarCredits", "XPRelicGrind"
    /// NOTE: For mode switching, prefer <see cref="SetMode"/> which uses the custom ChangeMode IPC.
    /// </summary>
    public bool ChangeSetting(string key, bool value)
    {
        if (!IsAvailable) return false;
        try { changeSetting.InvokeAction(key, value); return true; }
        catch (Exception ex)
        {
            DalamudApi.Log.Warning($"[Cosmic] ChangeSetting({key}, {value}) failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Changes an integer setting in ICE.
    /// Valid keys: "CosmoCreditsCap", "LunarCreditsCap"
    /// </summary>
    public bool ChangeSettingAmount(string key, int value)
    {
        if (!IsAvailable) return false;
        try { changeSettingAmount.InvokeAction(key, value); return true; }
        catch (Exception ex)
        {
            DalamudApi.Log.Warning($"[Cosmic] ChangeSettingAmount({key}, {value}) failed: {ex.Message}");
            return false;
        }
    }

    // ─── Mode Control (custom IPC) ─────────────

    /// <summary>
    /// ICE mode values: 0=Standard, 1=RelicMode, 2=LevelMode, 10=AgendaMode.
    /// Requires our forked ICE with ChangeMode/GetMode IPC endpoints.
    /// </summary>
    public const int ModeStandard = 0;
    public const int ModeRelic = 1;
    public const int ModeLevel = 2;
    public const int ModeAgenda = 10;

    /// <summary>
    /// Sets ICE's active mode. Requires forked ICE with ChangeMode IPC.
    /// </summary>
    public bool SetMode(int mode)
    {
        if (!IsAvailable) return false;
        try { changeMode.InvokeAction(mode); return true; }
        catch (Exception ex)
        {
            DalamudApi.Log.Warning($"[Cosmic] ChangeMode({mode}) failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Gets ICE's current active mode as int.
    /// Returns -1 if unavailable.
    /// </summary>
    public int GetMode()
    {
        if (!IsAvailable) return -1;
        try { return getMode.InvokeFunc(); }
        catch { return -1; }
    }

    /// <summary>
    /// Returns a display name for the given ICE mode value.
    /// </summary>
    public static string GetModeName(int mode) => mode switch
    {
        ModeStandard => "Standard",
        ModeRelic => "Relic XP Grind",
        ModeLevel => "Level Mode",
        ModeAgenda => "Agenda Mode",
        _ => $"Unknown ({mode})",
    };

    // ─── Legacy Convenience ─────────────────────

    /// <summary>
    /// Enables Relic XP grind mode. Prefer <see cref="SetMode"/> instead.
    /// </summary>
    public bool EnableRelicMode() => SetMode(ModeRelic);

    public void Dispose() { }
}
