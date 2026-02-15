namespace Expedition.IPC;

/// <summary>
/// Manages lifecycle of all IPC connections to external plugins.
/// </summary>
public sealed class IpcManager : IDisposable
{
    public GatherBuddyIpc GatherBuddy { get; }
    public GatherBuddyListManager GatherBuddyLists { get; }
    public ArtisanIpc Artisan { get; }
    public VnavmeshIpc Vnavmesh { get; }
    public DependencyMonitor DependencyMonitor { get; }

    public IpcManager()
    {
        GatherBuddy = new GatherBuddyIpc();
        GatherBuddyLists = new GatherBuddyListManager();
        Artisan = new ArtisanIpc();
        Vnavmesh = new VnavmeshIpc();
        DependencyMonitor = new DependencyMonitor(GatherBuddy, Vnavmesh);
    }

    /// <summary>
    /// Re-checks availability of all dependent plugins.
    /// </summary>
    public void RefreshAvailability()
    {
        GatherBuddy.CheckAvailability();
        Artisan.CheckAvailability();
        Vnavmesh.CheckAvailability();
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
