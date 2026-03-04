using Expedition.IPC;

namespace Expedition.Fishing;

/// <summary>
/// State machine that monitors ICE's Cosmic Exploration state and injects
/// Expedition's optimized AutoHook presets when ICE enters a fishing mission.
///
/// Flow:
///   Idle → WaitingToInject → Active → Idle
///
/// Idle:
///   - Polls CosmicIpc.GetCurrentState() every 0.5s
///   - When state == "Fish" AND GetCurrentMission() != 0:
///     - Check if Expedition has preset overrides for this mission
///     - If yes: transition → WaitingToInject
///
/// WaitingToInject:
///   - Wait for configurable delay (default 800ms) to let ICE finish loading its presets
///   - ICE's preset load timing: 150ms clear + 100ms × N presets ≈ 450ms for 2 presets
///   - After delay: clean up anonymous presets, then push each override
///   - Transition → Active
///
/// Active:
///   - Monitor CosmicIpc.GetCurrentState()
///   - When state leaves "Fish": transition → Idle (cleanup anonymous presets)
/// </summary>
public sealed class CosmicFishingMonitor : IDisposable
{
    private readonly CosmicIpc cosmic;
    private readonly AutoHookIpc autoHook;
    private readonly CosmicFishingPresets presets;

    private MonitorState state = MonitorState.Idle;
    private DateTime lastPollTime = DateTime.MinValue;
    private DateTime injectionStartTime;
    private uint activeMissionId;
    private int presetsInjectedCount;

    /// <summary>Polling interval when Idle (checking for fishing state).</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Current monitor state for UI display.</summary>
    public MonitorState CurrentState => state;

    /// <summary>Mission ID currently being monitored (0 when idle).</summary>
    public uint ActiveMissionId => activeMissionId;

    /// <summary>Number of presets injected for the current mission.</summary>
    public int PresetsInjectedCount => presetsInjectedCount;

    /// <summary>
    /// Status text for UI display.
    /// </summary>
    public string StatusText => state switch
    {
        MonitorState.Idle => "Idle",
        MonitorState.WaitingToInject => $"Waiting to inject (mission #{activeMissionId})",
        MonitorState.Active => $"Active — {presetsInjectedCount} preset(s) for mission #{activeMissionId}",
        _ => "Unknown",
    };

    public CosmicFishingMonitor(CosmicIpc cosmic, AutoHookIpc autoHook, CosmicFishingPresets presets)
    {
        this.cosmic = cosmic;
        this.autoHook = autoHook;
        this.presets = presets;
    }

    /// <summary>
    /// Called from the framework update loop. Drives the state machine.
    /// </summary>
    /// <param name="injectionDelayMs">Delay before injecting presets (from config).</param>
    public void Update(double injectionDelayMs)
    {
        switch (state)
        {
            case MonitorState.Idle:
                UpdateIdle();
                break;

            case MonitorState.WaitingToInject:
                UpdateWaitingToInject(injectionDelayMs);
                break;

            case MonitorState.Active:
                UpdateActive();
                break;
        }
    }

    private void UpdateIdle()
    {
        var now = DateTime.UtcNow;
        if (now - lastPollTime < PollInterval) return;
        lastPollTime = now;

        if (!cosmic.IsAvailable || !autoHook.IsAvailable) return;

        var iceState = cosmic.GetCurrentState();
        if (iceState != "Fish") return;

        var missionId = cosmic.GetCurrentMission();
        if (missionId == 0) return;

        // Determine mission type (for type-level fallback)
        var missionType = ClassifyMission(missionId);

        // Check if we have overrides for this mission
        var missionPresets = presets.GetPresetsForMission(missionId, missionType);
        if (missionPresets == null || missionPresets.Count == 0)
        {
            // No overrides — let ICE's defaults handle it
            return;
        }

        // Transition to WaitingToInject
        activeMissionId = missionId;
        presetsInjectedCount = 0;
        injectionStartTime = DateTime.UtcNow;
        state = MonitorState.WaitingToInject;

        DalamudApi.Log.Information(
            $"[CosmicFishing] Detected fishing mission #{missionId} (type: {missionType}). " +
            $"Have {missionPresets.Count} override preset(s). Waiting to inject...");
    }

    private void UpdateWaitingToInject(double injectionDelayMs)
    {
        // Wait for ICE to finish loading its presets
        var elapsed = (DateTime.UtcNow - injectionStartTime).TotalMilliseconds;
        if (elapsed < injectionDelayMs) return;

        // Verify we're still in a fishing state
        var iceState = cosmic.GetCurrentState();
        if (iceState != "Fish")
        {
            DalamudApi.Log.Warning("[CosmicFishing] ICE left fishing state before injection. Returning to Idle.");
            Reset();
            return;
        }

        // Inject our presets
        var missionType = ClassifyMission(activeMissionId);
        var missionPresets = presets.GetPresetsForMission(activeMissionId, missionType);

        if (missionPresets == null || missionPresets.Count == 0)
        {
            DalamudApi.Log.Warning("[CosmicFishing] Presets disappeared before injection. Returning to Idle.");
            Reset();
            return;
        }

        // Clean up ICE's anonymous presets first, then inject ours
        autoHook.CleanupPresets();

        var injected = autoHook.ActivateCustomPresets(missionPresets);
        presetsInjectedCount = injected;

        if (injected > 0)
        {
            DalamudApi.Log.Information(
                $"[CosmicFishing] Injected {injected}/{missionPresets.Count} preset(s) for mission #{activeMissionId}.");
            state = MonitorState.Active;
        }
        else
        {
            DalamudApi.Log.Warning("[CosmicFishing] Failed to inject any presets. Returning to Idle.");
            Reset();
        }
    }

    private void UpdateActive()
    {
        var now = DateTime.UtcNow;
        if (now - lastPollTime < PollInterval) return;
        lastPollTime = now;

        // Check if ICE has left the fishing state
        var iceState = cosmic.GetCurrentState();
        if (iceState != "Fish")
        {
            DalamudApi.Log.Information(
                $"[CosmicFishing] Fishing ended (ICE state: {iceState}). " +
                $"Cleaning up {presetsInjectedCount} injected preset(s).");
            autoHook.CleanupPresets();
            Reset();
        }
    }

    /// <summary>
    /// Classifies a mission by its scoring type. Currently returns Unknown for all missions
    /// since we don't have mission attribute data in Expedition. Mission-specific presets
    /// (by ID) are the primary mechanism; type-based fallback is for future expansion.
    ///
    /// TODO: Read mission attributes from Lumina (MissionAttributes flags) or ICE IPC
    /// to auto-classify missions.
    /// </summary>
    private static CosmicFishingMissionType ClassifyMission(uint missionId)
    {
        // Without mission attribute data, we can't auto-classify.
        // Users configure per-mission overrides by ID, which is the primary path.
        // Type-level defaults serve as future infrastructure.
        _ = missionId;
        return CosmicFishingMissionType.Unknown;
    }

    /// <summary>
    /// Forces a reset to Idle state (e.g., when session stops).
    /// </summary>
    public void ForceReset()
    {
        if (state == MonitorState.Active)
        {
            autoHook.CleanupPresets();
        }
        Reset();
    }

    private void Reset()
    {
        state = MonitorState.Idle;
        activeMissionId = 0;
        presetsInjectedCount = 0;
    }

    public void Dispose()
    {
        ForceReset();
    }
}

/// <summary>
/// States for the Cosmic fishing preset injection monitor.
/// </summary>
public enum MonitorState
{
    /// <summary>Watching for ICE to enter a fishing mission.</summary>
    Idle,

    /// <summary>ICE is in a fishing mission; waiting for ICE's preset load to finish.</summary>
    WaitingToInject,

    /// <summary>Presets have been injected and fishing is active.</summary>
    Active,
}
