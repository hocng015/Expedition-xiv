namespace Expedition.IPC;

/// <summary>
/// Central dependency readiness monitor. Maintains cached readiness
/// state for GatherBuddy Reborn and vnavmesh, refreshed periodically.
///
/// Consumers poll ReadinessState rather than making direct IPC calls,
/// reducing IPC traffic and providing a unified diagnosis of why
/// gathering cannot proceed.
/// </summary>
public sealed class DependencyMonitor
{
    private readonly GatherBuddyIpc gbr;
    private readonly VnavmeshIpc vnavmesh;
    private ReadinessSnapshot cachedSnapshot;
    private DateTime lastPollTime = DateTime.MinValue;

    public DependencyMonitor(GatherBuddyIpc gbr, VnavmeshIpc vnavmesh)
    {
        this.gbr = gbr;
        this.vnavmesh = vnavmesh;

        // Initialize with a safe default snapshot
        cachedSnapshot = new ReadinessSnapshot
        {
            GbrAvailable = gbr.IsAvailable,
            GbrStatusText = string.Empty,
            VnavmeshAvailable = vnavmesh.IsAvailable,
            NavReady = true,
            NavBuildProgress = 1.0f,
            PathfindInProgress = false,
            PathRunning = false,
            Timestamp = DateTime.Now,
        };
    }

    /// <summary>
    /// Returns the cached readiness snapshot. Cheap to call — no IPC calls.
    /// </summary>
    public ReadinessSnapshot GetSnapshot() => cachedSnapshot;

    /// <summary>
    /// Forces a fresh IPC poll regardless of the timer. Updates the cached snapshot.
    /// </summary>
    public ReadinessSnapshot Refresh()
    {
        // Refresh plugin availability first
        gbr.CheckAvailability();
        vnavmesh.CheckAvailability();

        cachedSnapshot = new ReadinessSnapshot
        {
            GbrAvailable = gbr.IsAvailable,
            GbrStatusText = gbr.IsAvailable ? gbr.GetStatusText() : string.Empty,
            VnavmeshAvailable = vnavmesh.IsAvailable,
            NavReady = vnavmesh.IsNavReady(),
            NavBuildProgress = vnavmesh.GetBuildProgress(),
            PathfindInProgress = vnavmesh.IsPathfindInProgress(),
            PathRunning = vnavmesh.IsPathRunning(),
            Timestamp = DateTime.Now,
        };

        lastPollTime = DateTime.Now;
        return cachedSnapshot;
    }

    /// <summary>
    /// Time-throttled refresh. Only polls IPC if the configured interval has elapsed.
    /// No-ops if dependency monitoring is disabled in config.
    /// </summary>
    public void Poll()
    {
        if (!Expedition.Config.MonitorDependencies) return;

        var interval = Expedition.Config.DependencyPollIntervalSeconds;
        if ((DateTime.Now - lastPollTime).TotalSeconds < interval) return;

        Refresh();
    }

    /// <summary>
    /// Examines the current snapshot and returns the most specific failure category.
    /// </summary>
    public FailureCategory DiagnoseFailure()
    {
        var s = cachedSnapshot;
        if (!s.GbrAvailable) return FailureCategory.GbrUnavailable;
        if (s.VnavmeshAvailable && !s.NavReady) return FailureCategory.VnavmeshNotReady;
        if (!s.VnavmeshAvailable) return FailureCategory.VnavmeshUnavailable;
        return FailureCategory.None;
    }

    /// <summary>
    /// Immutable snapshot of dependency readiness state at a point in time.
    /// </summary>
    public sealed class ReadinessSnapshot
    {
        // GBR status
        public bool GbrAvailable { get; init; }
        public string GbrStatusText { get; init; } = string.Empty;

        // vnavmesh status
        public bool VnavmeshAvailable { get; init; }
        public bool NavReady { get; init; }
        public float NavBuildProgress { get; init; }
        public bool PathfindInProgress { get; init; }
        public bool PathRunning { get; init; }

        // Derived
        public bool IsFullyReady => GbrAvailable && NavReady;
        public DateTime Timestamp { get; init; }

        /// <summary>
        /// Returns a human-readable reason why gathering is blocked, or null if ready.
        /// </summary>
        public string? GetBlockReason()
        {
            if (!GbrAvailable)
                return "GatherBuddy Reborn is not available.";
            if (!NavReady)
            {
                if (NavBuildProgress < 1.0f)
                    return $"vnavmesh is building navmesh ({NavBuildProgress:P0})...";
                return "vnavmesh navmesh is not ready.";
            }
            return null;
        }
    }

    public enum FailureCategory
    {
        None,
        GbrUnavailable,
        GbrInternalError,
        VnavmeshNotReady,
        VnavmeshUnavailable,
    }
}
