namespace ProsimCompanion.Core.Diagnostics.Metrics;

/// <summary>When the process started, so the memo strip's UP cell and the STATUS page's
/// "SESSION START" line agree. Constructed once at composition time; the start instant is
/// the construction instant unless a test supplies one.</summary>
public sealed class UptimeTracker
{
    public UptimeTracker(DateTimeOffset? startedUtc = null)
    {
        StartedUtc = startedUtc ?? DateTimeOffset.UtcNow;
    }

    /// <summary>The process start instant.</summary>
    public DateTimeOffset StartedUtc { get; }

    /// <summary>Time since start as of <paramref name="now"/>; never negative.</summary>
    public TimeSpan Elapsed(DateTimeOffset now)
    {
        var elapsed = now - StartedUtc;
        return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
    }
}
