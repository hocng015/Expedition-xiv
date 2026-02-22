using System.Numerics;
using Dalamud.Plugin.Ipc;

namespace Expedition.IPC;

/// <summary>
/// IPC subscriber for vnavmesh (navmesh pathfinding plugin).
/// Wraps readiness and movement endpoints for pathfinding-assisted navigation.
///
/// vnavmesh may not be installed as a standalone plugin — GBR bundles
/// its own copy. If the IPC endpoints are not available, this class
/// gracefully reports IsAvailable = false and all queries return
/// safe defaults (IsReady = true, BuildProgress = 1.0).
/// This ensures Expedition never blocks gathering due to missing vnavmesh IPC.
/// </summary>
public sealed class VnavmeshIpc : IDisposable
{
    // --- Query endpoints ---
    private readonly ICallGateSubscriber<bool> navIsReady;
    private readonly ICallGateSubscriber<float> navBuildProgress;
    private readonly ICallGateSubscriber<bool> navPathfindInProgress;
    private readonly ICallGateSubscriber<bool> pathIsRunning;

    // --- Movement endpoints ---
    private readonly ICallGateSubscriber<Vector3, bool, bool> pathfindAndMoveTo;
    private readonly ICallGateSubscriber<bool> pathStop;
    private readonly ICallGateSubscriber<Vector3, bool, bool> pathMoveTo;

    /// <summary>
    /// True if the vnavmesh IPC endpoints responded during the last availability check.
    /// When false, all query methods return safe "everything is fine" defaults.
    /// </summary>
    public bool IsAvailable { get; private set; }

    public VnavmeshIpc()
    {
        var pi = DalamudApi.PluginInterface;

        // Query
        navIsReady = pi.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        navBuildProgress = pi.GetIpcSubscriber<float>("vnavmesh.Nav.BuildProgress");
        navPathfindInProgress = pi.GetIpcSubscriber<bool>("vnavmesh.Nav.PathfindInProgress");
        pathIsRunning = pi.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");

        // Movement
        pathfindAndMoveTo = pi.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo");
        pathStop = pi.GetIpcSubscriber<bool>("vnavmesh.Path.Stop");
        pathMoveTo = pi.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.Path.MoveTo");

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

    // ─── Query Methods ───────────────────────────

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

    // ─── Movement Methods ────────────────────────

    /// <summary>
    /// Pathfinds to the destination and starts moving. Uses flying if <paramref name="fly"/> is true.
    /// Returns false if vnavmesh IPC is unavailable or the call fails.
    /// </summary>
    public bool PathfindAndMoveTo(Vector3 destination, bool fly = true)
    {
        if (!IsAvailable) return false;
        try
        {
            pathfindAndMoveTo.InvokeFunc(destination, fly);
            return true;
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Warning($"[Vnavmesh] PathfindAndMoveTo failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Moves directly to a position without pathfinding (straight-line movement).
    /// Useful when the path is already known (e.g., after a Windmire jump).
    /// Returns false if vnavmesh IPC is unavailable or the call fails.
    /// </summary>
    public bool MoveTo(Vector3 destination, bool fly = true)
    {
        if (!IsAvailable) return false;
        try
        {
            pathMoveTo.InvokeFunc(destination, fly);
            return true;
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Warning($"[Vnavmesh] MoveTo failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Stops all current pathfinding and movement.
    /// </summary>
    public void Stop()
    {
        try { pathStop.InvokeFunc(); }
        catch { /* vnavmesh not available */ }
    }

    public void Dispose()
    {
        // No event subscriptions to unsubscribe
    }
}
