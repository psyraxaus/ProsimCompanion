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

        return ofpStdUtc;
    }
}
