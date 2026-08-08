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

    /// <summary>Auto-fire decision for a committed phase transition: returns the gate to
    /// dispatch when the new phase is Cruise and the pending gate has not fired yet; null
    /// otherwise. Marks the gate fired so it dispatches at most once per queued gate.</summary>
    public string? TakeAuto(FlightPhase phase)
    {
        if (phase != FlightPhase.Cruise || PendingGate is null || Fired)
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
