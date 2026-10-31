namespace ProsimCompanion.Core.Aircraft;

/// <summary>
/// The one rule for "what is the scheduled departure right now", shared by the loadsheet
/// STD trigger and the gate monitor so the two can never disagree about the same STD.
/// </summary>
public static class ScheduledDeparture
{
    /// <summary>Manual override wins over the OFP. A manual time-of-day is anchored to the
    /// SIMULATED day (issue #95: a pilot flying an overnight sim on a real-world afternoon
    /// enters the sim's departure time); one that already passed by more than 12 h is read as
    /// tomorrow's departure so an evening entry for an after-midnight flight doesn't fire
    /// instantly.</summary>
    /// The OFP's STD is anchored to the simulated day the same way (issue #139, EKCH
    /// 2026-09-27: the sim flew a morning departure on another date while SimBrief's
    /// <c>sched_out</c> sat on the real-world date — "STD in −1412 min", final call the instant
    /// boarding started): when the OFP date and the sim clock are more than 12 h apart the
    /// time-of-day is kept and the date moved by whole days to the one nearest the sim clock.
    /// An OFP within 12 h of the sim clock is used as written.</summary>
    /// <param name="manualUtc">The Loadsheet page's typed STD, if any.</param>
    /// <param name="ofpStdUtc">The OFP's <c>sched_out</c>, if any.</param>
    /// <param name="nowUtc">The sim clock (or the real one when the sim is not live).</param>
    public static DateTimeOffset? Effective(TimeOnly? manualUtc, DateTimeOffset? ofpStdUtc, DateTimeOffset nowUtc)
    {
        if (manualUtc is { } manual)
        {
            var today = new DateTimeOffset(nowUtc.UtcDateTime.Date.Add(manual.ToTimeSpan()), TimeSpan.Zero);
            return nowUtc - today > TimeSpan.FromHours(12) ? today.AddDays(1) : today;
        }

        return ofpStdUtc is { } ofp ? AlignToSimDay(ofp, nowUtc) : null;
    }

    /// <summary>The same time-of-day on the day that puts it within ±12 h of
    /// <paramref name="nowUtc"/> (pure; the OFP date is only ever off by whole days).</summary>
    public static DateTimeOffset AlignToSimDay(DateTimeOffset stdUtc, DateTimeOffset nowUtc)
    {
        var half = TimeSpan.FromHours(12);
        var aligned = stdUtc;
        while (aligned - nowUtc > half)
        {
            aligned = aligned.AddDays(-1);
        }

        while (nowUtc - aligned > half)
        {
            aligned = aligned.AddDays(1);
        }

        return aligned;
    }
}
