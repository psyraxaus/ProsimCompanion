namespace ProsimCompanion.Core.Day;

/// <summary>What one end of duty was: the day that closed, who closed it ("voice", "web",
/// "offer", "idle") and when.</summary>
public sealed record DutyDayEnded(string DayId, string Source, DateTimeOffset AtUtc);

/// <summary>
/// Cross-feature duty-day edges (ADR-0019 §8, issue #218). The Speech pillar owns the duty day
/// and raises <see cref="DayEnded"/> from its end-of-duty path; the Flight pillar (the
/// flight-state snapshot), the GSX pillar (the departure cycle, the turnaround auto-start) and
/// the Persona pillar subscribe. Feature projects never reference each other, so the seam
/// lives here beside <see cref="State.GroundOpsSignals"/>. Events fire on the raiser's thread;
/// subscribers must not block.
/// </summary>
public sealed class DutyDaySignals
{
    /// <summary>The pilot ended the duty day (voice, web button, the FO's offer accepted) or
    /// the idle backstop closed it. Nothing of the old leg may survive this: the restart memory
    /// is cleared, the departure cycle starts over and ground preparation waits for a new OFP.</summary>
    public event Action<DutyDayEnded>? DayEnded;

    /// <summary>Raises <see cref="DayEnded"/> for every subscriber, each behind its own guard:
    /// the snapshot service does file IO in its handler, and one throwing subscriber must not
    /// stop the leg-boundary or the GSX reset behind it (review 2026-11-05). A fault is
    /// swallowed here — the subscriber logs its own; this hub has no logger by design.</summary>
    public void RaiseDayEnded(DutyDayEnded ended)
    {
        ArgumentNullException.ThrowIfNull(ended);
        var handlers = DayEnded;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Action<DutyDayEnded>>())
        {
            try
            {
                handler(ended);
            }
            catch (Exception)
            {
                // The subscriber's own catch logs it; the day still ends for everyone else.
            }
        }
    }
}
