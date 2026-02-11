namespace Expedition.Scheduling;

/// <summary>
/// Tracks Eorzean Time and provides scheduling for timed gathering nodes.
///
/// FFXIV time conversion:
///   1 Eorzean hour  = 175 real seconds (2 min 55 sec)
///   1 Eorzean day   = 4200 real seconds (70 min)
///   1 Eorzean month = 24 Eorzean days
///
/// Timed/Unspoiled nodes spawn at fixed Eorzean hours and last 2 ET hours (~5:50 real).
/// Ephemeral nodes spawn every 4 ET hours and last 4 ET hours (~11:40 real).
/// </summary>
public static class EorzeanTime
{
    // 1 Eorzean hour in real-world seconds
    private const double SecondsPerEorzeanHour = 175.0;

    // FFXIV epoch: the Unix timestamp that corresponds to ET 0:00
    // This is a well-known constant derived from the game client.
    private const long FfxivEpochUnix = 0;

    // Eorzea runs at 3600/175 = ~20.571x real time speed
    private const double EorzeaMultiplier = 3600.0 / SecondsPerEorzeanHour;

    /// <summary>
    /// Returns the current Eorzean hour (0-23).
    /// </summary>
    public static int CurrentHour
    {
        get
        {
            var totalEorzeaSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() * EorzeaMultiplier;
            return (int)(totalEorzeaSeconds / 3600 % 24);
        }
    }

    /// <summary>
    /// Returns the current Eorzean minute (0-59).
    /// </summary>
    public static int CurrentMinute
    {
        get
        {
            var totalEorzeaSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() * EorzeaMultiplier;
            return (int)(totalEorzeaSeconds / 60 % 60);
        }
    }

    /// <summary>
    /// Returns the total Eorzean hours since epoch (for scheduling math).
    /// </summary>
    public static double TotalEorzeanHours
    {
        get
        {
            var totalEorzeaSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() * EorzeaMultiplier;
            return totalEorzeaSeconds / 3600.0;
        }
    }

    /// <summary>
    /// Calculates how many real-world seconds until a given Eorzean hour next occurs.
    /// </summary>
    public static double SecondsUntilEorzeanHour(int targetHour)
    {
        var currentHour = CurrentHour;
        var currentMinute = CurrentMinute;

        var hoursUntil = targetHour - currentHour;
        if (hoursUntil < 0) hoursUntil += 24;
        if (hoursUntil == 0 && currentMinute > 0) hoursUntil = 24;

        var minuteOffset = currentMinute * (SecondsPerEorzeanHour / 60.0);
        return (hoursUntil * SecondsPerEorzeanHour) - minuteOffset;
    }

    /// <summary>
    /// Returns true if the current Eorzean time is within the given window.
    /// </summary>
    public static bool IsWithinWindow(int startHour, int durationHours)
    {
        var current = CurrentHour;
        var endHour = (startHour + durationHours) % 24;

        if (startHour < endHour)
            return current >= startHour && current < endHour;
        else // Wraps around midnight
            return current >= startHour || current < endHour;
    }

    /// <summary>
    /// Formats Eorzean time as "HH:MM ET".
    /// </summary>
    public static string FormatCurrentTime()
        => $"{CurrentHour:D2}:{CurrentMinute:D2} ET";

    /// <summary>
    /// Formats a real-time duration in a human-readable way.
    /// </summary>
    public static string FormatRealDuration(double seconds)
    {
        if (seconds < 60) return $"{seconds:F0}s";
        if (seconds < 3600) return $"{seconds / 60:F1}m";
        return $"{seconds / 3600:F1}h";
    }
}
