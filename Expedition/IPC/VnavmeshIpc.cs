using Dalamud.Plugin.Ipc;

namespace Expedition.IPC;

/// <summary>
/// IPC subscriber for vnavmesh (navmesh pathfinding plugin).
/// Wraps readiness endpoints to determine if pathfinding is available.
///
/// vnavmesh may not be installed as a standalone plugin — GBR bundles
/// its own copy. If the IPC endpoints are not available, this class
/// gracefully reports IsAvailable = false and all queries return
/// safe defaults (IsReady = true, BuildProgress = 1.0).
/// This ensures Expedition never blocks gathering due to missing vnavmesh IPC.
/// </summary>
public sealed class VnavmeshIpc : IDisposable
{
    private readonly ICallGateSubscriber<bool> navIsReady;
    private readonly ICallGateSubscriber<float> navBuildProgress;
    private readonly ICallGateSubscriber<bool> navPathfindInProgress;
    private readonly ICallGateSubscriber<bool> pathIsRunning;

    /// <summary>
    /// True if the vnavmesh IPC endpoints responded during the last availability check.
    /// When false, all query methods return safe "everything is fine" defaults.
    /// </summary>
    public bool IsAvailable { get; private set; }

    public VnavmeshIpc()
    {
        var pi = DalamudApi.PluginInterface;

        navIsReady = pi.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        navBuildProgress = pi.GetIpcSubscriber<float>("vnavmesh.Nav.BuildProgress");
        navPathfindInProgress = pi.GetIpcSubscriber<bool>("vnavmesh.Nav.PathfindInProgress");
        pathIsRunning = pi.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");

        CheckAvailability();
    }

    /// <summary>
    /// Probes the vnavmesh IPC to see if it's responding.
    /// </summary>
    public void CheckAvailability()
    {
        try
        {
            // If this call succeeds, vnavmesh IPC is available
            navIsReady.InvokeFunc();
            IsAvailable = true;
        }
        catch
        {
            IsAvailable = false;
        }
    }

    /// <summary>
    /// Returns true if the navmesh is loaded and ready for pathfinding.
    /// Returns true (safe default) if vnavmesh IPC is unavailable.
    /// </summary>
    public bool IsNavReady()
    {
        try { return navIsReady.InvokeFunc(); }
        catch { return true; }
    }

    /// <summary>
    /// Returns the navmesh build progress (0.0 to 1.0).
    /// Returns 1.0 (fully built) if vnavmesh IPC is unavailable.
    /// </summary>
    public float GetBuildProgress()
    {
        try { return navBuildProgress.InvokeFunc(); }
        catch { return 1.0f; }
    }

    /// <summary>
    /// Returns true if a pathfinding operation is currently in progress.
    /// Returns false if vnavmesh IPC is unavailable.
    /// </summary>
    public bool IsPathfindInProgress()
    {
        try { return navPathfindInProgress.InvokeFunc(); }
        catch { return false; }
    }

    /// <summary>
    /// Returns true if vnavmesh is actively following a path.
    /// Returns false if vnavmesh IPC is unavailable.
    /// </summary>
    public bool IsPathRunning()
    {
        try { return pathIsRunning.InvokeFunc(); }
        catch { return false; }
    }

    public void Dispose()
    {
        // No event subscriptions to unsubscribe
    }
}
