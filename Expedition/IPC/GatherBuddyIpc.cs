using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Expedition.IPC;

/// <summary>
/// IPC subscriber for GatherBuddy Reborn.
/// Wraps all known GBR IPC endpoints for gathering automation.
/// </summary>
public sealed class GatherBuddyIpc : IDisposable
{
    private readonly ICallGateSubscriber<int> version;
    private readonly ICallGateSubscriber<string, uint> identify;
    private readonly ICallGateSubscriber<bool> isAutoGatherEnabled;
    private readonly ICallGateSubscriber<string> getAutoGatherStatusText;
    private readonly ICallGateSubscriber<bool, object?> setAutoGatherEnabled;
    private readonly ICallGateSubscriber<bool> isAutoGatherWaiting;

    // Events
    private readonly ICallGateSubscriber<object?> autoGatherWaiting;
    private readonly ICallGateSubscriber<bool, object?> autoGatherEnabledChanged;

    public bool IsAvailable { get; private set; }

    /// <summary>
    /// The GBR status text captured at the moment AutoGather was last disabled.
    /// Read immediately in the OnEnabledChanged callback so it reflects GBR's
    /// AbortAutoGather reason before GBR has a chance to clear it.
    /// </summary>
    public string LastDisableStatusText { get; private set; } = string.Empty;

    /// <summary>When AutoGather was last disabled (by any source).</summary>
    public DateTime LastDisableTime { get; private set; } = DateTime.MinValue;

    public event Action? OnAutoGatherWaiting;
    public event Action<bool>? OnAutoGatherEnabledChanged;

    public GatherBuddyIpc()
    {
        var pi = DalamudApi.PluginInterface;

        version = pi.GetIpcSubscriber<int>("GatherBuddyReborn.Version");
        identify = pi.GetIpcSubscriber<string, uint>("GatherBuddyReborn.Identify");
        isAutoGatherEnabled = pi.GetIpcSubscriber<bool>("GatherBuddyReborn.IsAutoGatherEnabled");
        getAutoGatherStatusText = pi.GetIpcSubscriber<string>("GatherBuddyReborn.GetAutoGatherStatusText");
        setAutoGatherEnabled = pi.GetIpcSubscriber<bool, object?>("GatherBuddyReborn.SetAutoGatherEnabled");
        isAutoGatherWaiting = pi.GetIpcSubscriber<bool>("GatherBuddyReborn.IsAutoGatherWaiting");

        autoGatherWaiting = pi.GetIpcSubscriber<object?>("GatherBuddyReborn.AutoGatherWaiting");
        autoGatherEnabledChanged = pi.GetIpcSubscriber<bool, object?>("GatherBuddyReborn.AutoGatherEnabledChanged");

        autoGatherWaiting.Subscribe(OnWaiting);
        autoGatherEnabledChanged.Subscribe(OnEnabledChanged);

        CheckAvailability();
    }

    public void Dispose()
    {
        try { autoGatherWaiting.Unsubscribe(OnWaiting); } catch { }
        try { autoGatherEnabledChanged.Unsubscribe(OnEnabledChanged); } catch { }
    }

    public void CheckAvailability()
    {
        try
        {
            var v = version.InvokeFunc();
            IsAvailable = v >= 2;
        }
        catch
        {
            IsAvailable = false;
        }
    }

    /// <summary>
    /// Identifies a gatherable item by name, returning its ItemId. Returns 0 if not found.
    /// </summary>
    public uint Identify(string itemName)
    {
        try { return identify.InvokeFunc(itemName); }
        catch { return 0; }
    }

    /// <summary>
    /// Returns whether GBR AutoGather is currently running.
    /// </summary>
    public bool GetAutoGatherEnabled()
    {
        try { return isAutoGatherEnabled.InvokeFunc(); }
        catch { return false; }
    }

    /// <summary>
    /// Returns the current GBR status text.
    /// </summary>
    public string GetStatusText()
    {
        try { return getAutoGatherStatusText.InvokeFunc(); }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Enables or disables GBR AutoGather.
    /// </summary>
    public void SetAutoGatherEnabled(bool enabled)
    {
        try { setAutoGatherEnabled.InvokeAction(enabled); }
        catch (Exception ex) { DalamudApi.Log.Error(ex, "Failed to set GBR AutoGather enabled"); }
    }

    /// <summary>
    /// Returns whether GBR AutoGather is in a waiting/idle state.
    /// </summary>
    public bool GetAutoGatherWaiting()
    {
        try { return isAutoGatherWaiting.InvokeFunc(); }
        catch { return false; }
    }

    private void OnWaiting()
    {
        OnAutoGatherWaiting?.Invoke();
    }

    private void OnEnabledChanged(bool enabled)
    {
        if (!enabled)
        {
            // Capture the status text NOW — GBR sets AutoStatus in AbortAutoGather()
            // and may clear it before our next 1s poll. This is the only reliable
            // moment to read the disable reason.
            LastDisableStatusText = GetStatusText();
            LastDisableTime = DateTime.Now;
        }

        OnAutoGatherEnabledChanged?.Invoke(enabled);
    }
}
