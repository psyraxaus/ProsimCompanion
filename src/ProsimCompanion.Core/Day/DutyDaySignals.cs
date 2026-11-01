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

    public void RaiseDayEnded(DutyDayEnded ended)
    {
        ArgumentNullException.ThrowIfNull(ended);
        DayEnded?.Invoke(ended);
    }
}
