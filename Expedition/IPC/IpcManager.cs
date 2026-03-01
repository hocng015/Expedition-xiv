namespace Expedition.IPC;

/// <summary>
/// Manages lifecycle of all IPC connections to external plugins.
/// </summary>
public sealed class IpcManager : IDisposable
{
    public GatherBuddyIpc GatherBuddy { get; }
    public GatherBuddyListManager GatherBuddyLists { get; }
    public GbrStateTracker GbrStateTracker { get; }
    public ArtisanIpc Artisan { get; }
    public VnavmeshIpc Vnavmesh { get; }
    public CosmicIpc Cosmic { get; }
    public AutoHookIpc AutoHook { get; }
    public DependencyMonitor DependencyMonitor { get; }

    /// <summary>
    /// Interval between automatic availability polls for plugins that aren't yet detected.
    /// Only unavailable plugins are re-probed — once a plugin is detected, it's not re-checked
    /// until it fails an IPC call (which sets IsAvailable = false on that specific IPC class).
    /// </summary>
    private const double AutoDetectIntervalSeconds = 5.0;

    /// <summary>When the last automatic availability poll ran.</summary>
    private DateTime lastAutoDetectTime = DateTime.MinValue;

    /// <summary>
    /// Snapshot of each plugin's availability from the last auto-detect poll.
    /// Used to log transitions (unavailable -> available) only once.
    /// </summary>
    private bool prevGbr, prevArtisan, prevVnav, prevCosmic, prevAutoHook;

    public IpcManager()
    {
        GatherBuddy = new GatherBuddyIpc();
        GatherBuddyLists = new GatherBuddyListManager();
        GbrStateTracker = new GbrStateTracker();
        Artisan = new ArtisanIpc();
        Vnavmesh = new VnavmeshIpc();
        Cosmic = new CosmicIpc();
        AutoHook = new AutoHookIpc();
        DependencyMonitor = new DependencyMonitor(GatherBuddy, Vnavmesh);

        // Capture initial state
        prevGbr = GatherBuddy.IsAvailable;
        prevArtisan = Artisan.IsAvailable;
        prevVnav = Vnavmesh.IsAvailable;
        prevCosmic = Cosmic.IsAvailable;
        prevAutoHook = AutoHook.IsAvailable;
    }

    /// <summary>
    /// Called from the framework update loop. Periodically re-checks availability
    /// of any plugins that aren't currently detected, so late-loading or reloaded
    /// plugins are automatically picked up without manual Refresh clicks.
    /// </summary>
    public void PollAutoDetect()
    {
        var now = DateTime.Now;
        if ((now - lastAutoDetectTime).TotalSeconds < AutoDetectIntervalSeconds) return;
        lastAutoDetectTime = now;

        // Only re-probe plugins that are currently unavailable — don't waste IPC
        // calls on plugins that are already working.
        if (!GatherBuddy.IsAvailable)
            GatherBuddy.CheckAvailability();
        if (!Artisan.IsAvailable)
            Artisan.CheckAvailability();
        if (!Vnavmesh.IsAvailable)
            Vnavmesh.CheckAvailability();
        if (!Cosmic.IsAvailable)
            Cosmic.CheckAvailability();
        if (!AutoHook.IsAvailable)
            AutoHook.CheckAvailability();

        // Log transitions so the user knows when a plugin is detected
        LogTransition("GatherBuddy Reborn", ref prevGbr, GatherBuddy.IsAvailable);
        LogTransition("Artisan", ref prevArtisan, Artisan.IsAvailable);
        LogTransition("vnavmesh", ref prevVnav, Vnavmesh.IsAvailable);
        LogTransition("ICE", ref prevCosmic, Cosmic.IsAvailable);
        LogTransition("AutoHook", ref prevAutoHook, AutoHook.IsAvailable);

        // Lazy-init: GbrStateTracker needs the GBR plugin instance from the list manager,
        // which is only available after GatherBuddyLists.Initialize() has run.
        if (!GbrStateTracker.IsInitialized && GatherBuddyLists.GbrPluginInstance != null)
        {
            GbrStateTracker.Initialize(GatherBuddyLists.GbrPluginInstance);
        }
    }

    /// <summary>
    /// Re-checks availability of all dependent plugins (forced, regardless of current state).
    /// Called by the user-facing Refresh button.
    /// </summary>
    public void RefreshAvailability()
    {
        GatherBuddy.CheckAvailability();
        Artisan.CheckAvailability();
        Vnavmesh.CheckAvailability();
        Cosmic.CheckAvailability();
        AutoHook.CheckAvailability();

        // Log any transitions
        LogTransition("GatherBuddy Reborn", ref prevGbr, GatherBuddy.IsAvailable);
        LogTransition("Artisan", ref prevArtisan, Artisan.IsAvailable);
        LogTransition("vnavmesh", ref prevVnav, Vnavmesh.IsAvailable);
        LogTransition("ICE", ref prevCosmic, Cosmic.IsAvailable);
        LogTransition("AutoHook", ref prevAutoHook, AutoHook.IsAvailable);

        // Lazy-init GbrStateTracker
        if (!GbrStateTracker.IsInitialized && GatherBuddyLists.GbrPluginInstance != null)
        {
            GbrStateTracker.Initialize(GatherBuddyLists.GbrPluginInstance);
        }
    }

    /// <summary>
    /// Returns a status summary of plugin availability.
    /// </summary>
    public (bool gatherBuddy, bool artisan, bool vnavmesh, bool cosmic) GetAvailability()
    {
        return (GatherBuddy.IsAvailable, Artisan.IsAvailable, Vnavmesh.IsAvailable, Cosmic.IsAvailable);
    }

    /// <summary>
    /// Logs a plugin availability transition (detected or lost).
    /// Only logs when the state actually changes.
    /// </summary>
    private static void LogTransition(string pluginName, ref bool previous, bool current)
    {
        if (previous == current) return;

        if (current)
            DalamudApi.Log.Information($"[IPC] {pluginName} detected (late load / reload).");
        else
            DalamudApi.Log.Warning($"[IPC] {pluginName} lost (unloaded / crashed).");

        previous = current;
    }

    public void Dispose()
    {
        GatherBuddy.Dispose();
        Artisan.Dispose();
        Vnavmesh.Dispose();
        Cosmic.Dispose();
    }
}
