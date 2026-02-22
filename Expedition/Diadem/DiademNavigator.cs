using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Expedition.IPC;

namespace Expedition.Diadem;

/// <summary>
/// Navigation state for the Diadem Windmire navigator.
/// </summary>
public enum NavState
{
    /// <summary>Not navigating.</summary>
    Idle,

    /// <summary>Flying to a Windmire entry point.</summary>
    FlyingToWindmire,

    /// <summary>Player has been catapulted by a Windmire (ConditionFlag.Jumping61).</summary>
    InWindmireJump,

    /// <summary>Flying to the final destination after a Windmire jump.</summary>
    FlyingToDestination,

    /// <summary>Flying directly to destination (no Windmire advantage).</summary>
    FlyingDirect,
}

/// <summary>
/// Handles Windmire-assisted navigation in the Diadem.
/// Evaluates whether using a Windmire tornado shortcut is faster than flying direct,
/// and manages the multi-step navigation: fly to Windmire → catapult → fly to destination.
///
/// Monitors ConditionFlag.Jumping61 to detect when the game is catapulting the player.
/// Uses vnavmesh IPC for actual pathfinding and movement.
/// </summary>
public sealed class DiademNavigator
{
    private readonly VnavmeshIpc vnavmesh;

    private Vector3 finalDestination;
    private Vector3 windmireEntry;
    private Vector3 windmireLanding;
    private DateTime stateStartTime;
    private DateTime lastJumping61Seen;

    /// <summary>Current navigation state.</summary>
    public NavState State { get; private set; } = NavState.Idle;

    /// <summary>Human-readable status message for the UI.</summary>
    public string StatusMessage { get; private set; } = string.Empty;

    /// <summary>True if a Windmire shortcut is being used for the current navigation.</summary>
    public bool UsingWindmire => State == NavState.FlyingToWindmire
                               || State == NavState.InWindmireJump
                               || State == NavState.FlyingToDestination;

    /// <summary>True if any navigation is in progress.</summary>
    public bool IsNavigating => State != NavState.Idle;

    /// <summary>The Windmire entry point being navigated to (if using Windmire).</summary>
    public Vector3 CurrentWindmireEntry => windmireEntry;

    /// <summary>The final destination of the current navigation.</summary>
    public Vector3 CurrentDestination => finalDestination;

    public DiademNavigator(VnavmeshIpc vnavmesh)
    {
        this.vnavmesh = vnavmesh;
    }

    /// <summary>
    /// Starts navigation to the given destination, automatically evaluating
    /// whether a Windmire shortcut should be used.
    /// </summary>
    public void NavigateTo(Vector3 destination)
    {
        if (!DiademWindmires.IsInDiadem())
        {
            StatusMessage = "Not in the Diadem.";
            return;
        }

        var playerPos = GetPlayerPosition();
        if (playerPos == Vector3.Zero)
        {
            StatusMessage = "Cannot determine player position.";
            return;
        }

        if (!vnavmesh.IsAvailable)
        {
            StatusMessage = "vnavmesh not available.";
            return;
        }

        finalDestination = destination;

        // Check if a Windmire shortcut is worthwhile
        var bestWindmire = DiademWindmires.FindBestWindmire(playerPos, destination);

        if (bestWindmire.HasValue)
        {
            windmireEntry = bestWindmire.Value.From;
            windmireLanding = bestWindmire.Value.To;

            DalamudApi.Log.Information(
                $"[DiademNav] Using Windmire shortcut: fly to ({windmireEntry.X:F0}, {windmireEntry.Z:F0}) " +
                $"→ land at ({windmireLanding.X:F0}, {windmireLanding.Z:F0}) → fly to destination");

            TransitionTo(NavState.FlyingToWindmire);
            vnavmesh.PathfindAndMoveTo(windmireEntry, fly: true);
        }
        else
        {
            DalamudApi.Log.Information("[DiademNav] Flying direct — no Windmire advantage.");
            TransitionTo(NavState.FlyingDirect);
            vnavmesh.PathfindAndMoveTo(destination, fly: true);
        }
    }

    /// <summary>
    /// Navigates to the nearest Windmire entry point from the player's current position.
    /// </summary>
    public void FlyToNearestWindmire()
    {
        if (!DiademWindmires.IsInDiadem())
        {
            StatusMessage = "Not in the Diadem.";
            return;
        }

        var playerPos = GetPlayerPosition();
        if (playerPos == Vector3.Zero) return;

        var nearest = DiademWindmires.FindNearestWindmire(playerPos);
        if (!nearest.HasValue) return;

        windmireEntry = nearest.Value.From;
        windmireLanding = nearest.Value.To;
        finalDestination = nearest.Value.To; // Destination is the landing point

        DalamudApi.Log.Information($"[DiademNav] Flying to nearest Windmire at ({windmireEntry.X:F0}, {windmireEntry.Z:F0})");
        TransitionTo(NavState.FlyingToWindmire);
        vnavmesh.PathfindAndMoveTo(windmireEntry, fly: true);
    }

    /// <summary>
    /// Stops all navigation immediately.
    /// </summary>
    public void Stop()
    {
        if (State == NavState.Idle) return;

        vnavmesh.Stop();
        TransitionTo(NavState.Idle);
        StatusMessage = "Navigation stopped.";
        DalamudApi.Log.Information("[DiademNav] Navigation stopped by user.");
    }

    /// <summary>
    /// Must be called every framework tick to monitor state transitions.
    /// Detects Windmire catapult (Jumping61), path completion, and timeouts.
    /// </summary>
    public void Update()
    {
        if (State == NavState.Idle) return;

        // Safety: abort if we left the Diadem
        if (!DiademWindmires.IsInDiadem())
        {
            Stop();
            StatusMessage = "Left the Diadem — navigation aborted.";
            return;
        }

        var isJumping = DalamudApi.Condition[ConditionFlag.Jumping61];
        if (isJumping)
            lastJumping61Seen = DateTime.UtcNow;

        switch (State)
        {
            case NavState.FlyingToWindmire:
                UpdateFlyingToWindmire(isJumping);
                break;

            case NavState.InWindmireJump:
                UpdateInWindmireJump(isJumping);
                break;

            case NavState.FlyingToDestination:
            case NavState.FlyingDirect:
                UpdateFlyingToDestination();
                break;
        }
    }

    private void UpdateFlyingToWindmire(bool isJumping)
    {
        // Windmire catapult triggered — stop nav and wait for landing
        if (isJumping)
        {
            vnavmesh.Stop();
            TransitionTo(NavState.InWindmireJump);
            StatusMessage = "Windmire jump in progress...";
            DalamudApi.Log.Information("[DiademNav] Windmire catapult detected (Jumping61).");
            return;
        }

        // Check if we've arrived near the Windmire entry (within 5y)
        var playerPos = GetPlayerPosition();
        if (playerPos != Vector3.Zero && Vector3.Distance(playerPos, windmireEntry) < 5f)
        {
            // We're very close — the catapult should trigger momentarily
            StatusMessage = "Approaching Windmire...";
            return;
        }

        // Check if vnavmesh stopped pathing (arrived or error)
        if (!vnavmesh.IsPathRunning() && !vnavmesh.IsPathfindInProgress())
        {
            // Path finished but we didn't get catapulted — retry or fly to Windmire
            if ((DateTime.UtcNow - stateStartTime).TotalSeconds > 3)
            {
                DalamudApi.Log.Debug("[DiademNav] Path ended without catapult — retrying nav to Windmire.");
                vnavmesh.PathfindAndMoveTo(windmireEntry, fly: true);
                stateStartTime = DateTime.UtcNow;
            }
        }

        // Timeout after 60 seconds
        if ((DateTime.UtcNow - stateStartTime).TotalSeconds > 60)
        {
            DalamudApi.Log.Warning("[DiademNav] Timed out flying to Windmire. Switching to direct flight.");
            TransitionTo(NavState.FlyingDirect);
            vnavmesh.PathfindAndMoveTo(finalDestination, fly: true);
        }

        StatusMessage = $"Flying to Windmire...";
    }

    private void UpdateInWindmireJump(bool isJumping)
    {
        if (isJumping)
        {
            // Still being catapulted
            StatusMessage = "Windmire jump in progress...";
            return;
        }

        // Jumping61 cleared — we've landed. Give a brief delay for position to stabilize.
        if ((DateTime.UtcNow - lastJumping61Seen).TotalMilliseconds < 500)
        {
            StatusMessage = "Landing...";
            return;
        }

        // Check if final destination is near the landing point (within 30y means we're done)
        var playerPos = GetPlayerPosition();
        if (playerPos != Vector3.Zero && Vector3.Distance(playerPos, finalDestination) < 30f)
        {
            TransitionTo(NavState.Idle);
            StatusMessage = "Arrived at destination.";
            DalamudApi.Log.Information("[DiademNav] Arrived at destination after Windmire jump.");
            return;
        }

        // Navigate from landing point to final destination
        DalamudApi.Log.Information("[DiademNav] Windmire jump complete. Flying to final destination.");
        TransitionTo(NavState.FlyingToDestination);
        vnavmesh.PathfindAndMoveTo(finalDestination, fly: true);
    }

    private void UpdateFlyingToDestination()
    {
        var playerPos = GetPlayerPosition();

        // Check if we've arrived (within 10y)
        if (playerPos != Vector3.Zero && Vector3.Distance(playerPos, finalDestination) < 10f)
        {
            TransitionTo(NavState.Idle);
            StatusMessage = "Arrived at destination.";
            DalamudApi.Log.Information("[DiademNav] Arrived at final destination.");
            return;
        }

        // Check if path finished
        if (!vnavmesh.IsPathRunning() && !vnavmesh.IsPathfindInProgress())
        {
            if ((DateTime.UtcNow - stateStartTime).TotalSeconds > 3)
            {
                // Path finished but we're not close — we're done anyway
                TransitionTo(NavState.Idle);
                StatusMessage = "Navigation complete.";
                return;
            }
        }

        // Timeout
        if ((DateTime.UtcNow - stateStartTime).TotalSeconds > 120)
        {
            Stop();
            StatusMessage = "Navigation timed out.";
            return;
        }

        StatusMessage = State == NavState.FlyingDirect
            ? "Flying to destination..."
            : "Flying to destination (post-Windmire)...";
    }

    private void TransitionTo(NavState newState)
    {
        State = newState;
        stateStartTime = DateTime.UtcNow;
    }

    private static Vector3 GetPlayerPosition()
    {
        var player = DalamudApi.ObjectTable.LocalPlayer;
        return player?.Position ?? Vector3.Zero;
    }
}
