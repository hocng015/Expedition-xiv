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
    public DependencyMonitor DependencyMonitor { get; }

    public IpcManager()
    {
        GatherBuddy = new GatherBuddyIpc();
        GatherBuddyLists = new GatherBuddyListManager();
        GbrStateTracker = new GbrStateTracker();
        Artisan = new ArtisanIpc();
        Vnavmesh = new VnavmeshIpc();
        DependencyMonitor = new DependencyMonitor(GatherBuddy, Vnavmesh);
    }

    /// <summary>
    /// Re-checks availability of all dependent plugins.
    /// Also attempts to initialize the GBR state tracker if it hasn't been initialized yet
    /// (requires GBR's plugin instance, which is only available after list manager init).
    /// </summary>
    public void RefreshAvailability()
    {
        GatherBuddy.CheckAvailability();
        Artisan.CheckAvailability();
        Vnavmesh.CheckAvailability();

        // Lazy-init: GbrStateTracker needs the GBR plugin instance from the list manager,
        // which is only available after GatherBuddyLists.Initialize() has run (called
        // lazily by SetGatherList). Try to init here if not yet done.
        if (!GbrStateTracker.IsInitialized && GatherBuddyLists.GbrPluginInstance != null)
        {
            GbrStateTracker.Initialize(GatherBuddyLists.GbrPluginInstance);
        }
    }

    /// <summary>
    /// Returns a status summary of plugin availability.
    /// </summary>
    public (bool gatherBuddy, bool artisan, bool vnavmesh) GetAvailability()
    {
        return (GatherBuddy.IsAvailable, Artisan.IsAvailable, Vnavmesh.IsAvailable);
    }

    public void Dispose()
    {
        GatherBuddy.Dispose();
        Artisan.Dispose();
        Vnavmesh.Dispose();
    }
}
