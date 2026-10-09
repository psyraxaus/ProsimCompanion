namespace ProsimCompanion.Audio.Mixer;

/// <summary>
/// Doubling reconnect backoff for the mixer client: the first retry waits the configured
/// delay, each further consecutive failure doubles it up to the ceiling. Same schedule as the
/// GSX client's (that helper lives in the Gsx project, which this pillar must not reference).
/// </summary>
public static class MixerReconnectBackoff
{
    public static TimeSpan Delay(int baseMs, int maxMs, long consecutiveFailures)
    {
        var floor = Math.Max(500, baseMs);
        var ceiling = Math.Max(floor, maxMs);
        if (consecutiveFailures <= 1)
        {
            return TimeSpan.FromMilliseconds(floor);
        }

        var exponent = (int)Math.Min(20, consecutiveFailures - 1);
        var ms = Math.Min((double)ceiling, floor * Math.Pow(2, exponent));
        return TimeSpan.FromMilliseconds(ms);
    }
}
