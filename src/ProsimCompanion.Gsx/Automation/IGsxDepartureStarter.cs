namespace ProsimCompanion.Gsx.Automation;

/// <summary>Why an on-demand service call could not start the departure cycle.</summary>
public sealed record DepartureStartForCallOutcome(bool Started, string? Refusal)
{
    public static DepartureStartForCallOutcome Ok { get; } = new(true, null);

    public static DepartureStartForCallOutcome Refused(string reason) => new(false, reason);
}

/// <summary>
/// The on-demand path's way into the departure cycle (issue #169): a hail or command that
/// calls Boarding or Refueling BEFORE the sequence was started must first arm GSX's pax
/// target from the current plan — the same code the sequencer runs — and mark the cycle
/// started with its source, or refuse when no plan is loaded. On 2026-10-11 the hail boarded
/// the previous leg's 122 passengers (GSX's stale LVAR) against a 128-pax OFP because the
/// sequencer, still waiting for "commence ground services", had never armed the target.
/// </summary>
public interface IGsxDepartureStarter
{
    /// <summary>True once the departure sequence has been started this cycle.</summary>
    bool Started { get; }

    /// <summary>Arms the pax target (when <paramref name="requirePaxTarget"/>, the call is
    /// refused unless the booked manifest is loaded and the LVAR write landed) and marks the
    /// cycle started with <paramref name="source"/> (<c>hail</c>, <c>voice</c>, <c>command</c>,
    /// <c>web</c>, …). Idempotent once started.</summary>
    Task<DepartureStartForCallOutcome> StartForServiceCallAsync(
        string source, bool requirePaxTarget, CancellationToken cancellationToken = default);
}
