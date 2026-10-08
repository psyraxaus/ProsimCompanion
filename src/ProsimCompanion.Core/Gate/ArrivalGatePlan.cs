using ProsimCompanion.Core.Flight;

namespace ProsimCompanion.Core.Gate;

/// <summary>
/// Pure decision core for the two-stage arrival-gate workflow (Prosim2GSX semantics):
/// Confirm queues a gate, reaching cruise auto-fires it exactly once, Send Now fires
/// immediately, and Cancel / re-Confirm re-arms the auto-fire. Not thread-safe — the
/// coordinator serializes access; kept pure so the queue/trigger rules are testable without
/// timers or phase events.
/// </summary>
public sealed class ArrivalGatePlan
{
    /// <summary>The queued (or most recently sent) gate, normalized; null when idle.</summary>
    public string? PendingGate { get; private set; }

    /// <summary>True once the pending gate has been dispatched (auto or manual) — the
    /// once-per-queued-gate guard so repeated Cruise transitions never refire.</summary>
    public bool Fired { get; private set; }

    /// <summary>Display-token normalization shared by every gate consumer: trim + uppercase
    /// (the GSX search token form, e.g. "B12").</summary>
    public static string Normalize(string? gate) => gate?.Trim().ToUpperInvariant() ?? "";

    /// <summary>Queues a gate and re-arms the auto-fire (a changed gate is a new request).
    /// Returns the normalized gate, or null when the input was blank (nothing queued).</summary>
    public string? Queue(string? gate)
    {
        var normalized = Normalize(gate);
        if (normalized.Length == 0)
        {
            return null;
        }

        PendingGate = normalized;
        Fired = false;
        return normalized;
    }

    /// <summary>Clears the queue and the fired guard (Cancel; re-queue is allowed after).</summary>
    public void Clear()
    {
        PendingGate = null;
        Fired = false;
    }

    /// <summary>
    /// The phases in which a queued, unfired gate goes out: the cruise entry the predecessor
    /// keyed on, and every later phase of the leg. Issue #102 (2026-08-22 EGLL→LGAV): the
    /// auto-fire was Cruise-only, so after an in-flight app restart in the descent — or on a
    /// short leg that never commits Cruise — the cruise edge was already history and a queued
    /// gate waited forever. GSX accepts <c>gate.select</c> in the air and on the ground while
    /// still rolling (docs/integrations/gsx-remote-api.md §6); the GSX dispatcher holds the
    /// armed request until its own preconditions hold, so firing early is safe and firing
    /// late is still useful until the aircraft is parked.
    /// </summary>
    public static bool IsAutoFirePhase(FlightPhase phase)
        => phase is FlightPhase.Cruise or FlightPhase.Descent or FlightPhase.Approach
            or FlightPhase.LandingRollout or FlightPhase.TaxiIn;

    /// <summary>Auto-fire decision for a committed phase (a transition's new phase, or the
    /// current phase at Confirm/Restore time): returns the gate to dispatch when the phase is
    /// at or past the cruise entry (<see cref="IsAutoFirePhase"/>) and the pending gate has
    /// not fired yet; null otherwise. Marks the gate fired so it dispatches at most once per
    /// queued gate — a step-climb re-entry into Cruise, or Cruise → Descent, never
    /// refires.</summary>
    public string? TakeAuto(FlightPhase phase)
    {
        if (!IsAutoFirePhase(phase) || PendingGate is null || Fired)
        {
            return null;
        }

        Fired = true;
        return PendingGate;
    }

    /// <summary>Manual "Send Now": an explicit gate wins over (and replaces) the pending one;
    /// with no explicit gate the pending gate is (re)sent. Returns the gate to dispatch or
    /// null when there is nothing to send. Marks fired so cruise entry will not resend.</summary>
    public string? TakeManual(string? gate = null)
    {
        var explicitGate = Normalize(gate);
        if (explicitGate.Length > 0)
        {
            PendingGate = explicitGate;
        }

        if (PendingGate is null)
        {
            return null;
        }

        Fired = true;
        return PendingGate;
    }
}
