namespace ProsimCompanion.Gsx;

/// <summary>
/// Exponential reconnect backoff for the Remote API client (issue #76): the first retry waits
/// the configured interval, each further consecutive failure doubles it up to the ceiling.
/// Pure, so the schedule is testable without a socket.
/// </summary>
public static class GsxReconnectBackoff
{
    /// <summary>The delay before the next attempt after <paramref name="consecutiveFailures"/>
    /// failures in a row (0 = last attempt succeeded → the base interval).</summary>
    public static TimeSpan Delay(int baseMs, int maxMs, long consecutiveFailures)
    {
        var floor = Math.Max(500, baseMs);
        var ceiling = Math.Max(floor, maxMs);
        if (consecutiveFailures <= 1)
        {
            return TimeSpan.FromMilliseconds(floor);
        }

        // Doubling saturates long before the shift could overflow.
        var exponent = (int)Math.Min(20, consecutiveFailures - 1);
        var ms = Math.Min((double)ceiling, floor * Math.Pow(2, exponent));
        return TimeSpan.FromMilliseconds(ms);
    }
}
