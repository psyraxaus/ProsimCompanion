using ProsimCompanion.Core.Flight;

namespace ProsimCompanion.Core.Gate;

/// <summary>What the rule decided about one <c>assigned_gate</c> reading.</summary>
public enum AtcGateDecision
{
    /// <summary>Nothing new, or nothing worth acting on (same gate as before, blank).</summary>
    None,

    /// <summary>A gate seen on the ground before takeoff — the departure stand. Remembered so
    /// the same text is never taken for the arrival gate later.</summary>
    DepartureGateNoted,

    /// <summary>Airborne or after landing, a gate that differs from the departure one, and the
    /// pilot has queued nothing: queue it for GSX as the arrival gate.</summary>
    QueueArrival,

    /// <summary>A candidate arrival gate, but the pilot already queued one — theirs wins.</summary>
    PilotGateWins,

    /// <summary>The airborne reading equals the departure gate — SayIntentions has not
    /// reassigned yet; ignored.</summary>
    SameAsDeparture,
}

/// <summary>
/// Pure rule for taking SayIntentions' <c>assigned_gate</c> as the GSX arrival gate
/// (2026-10-08, closes the gap noted in <c>docs/integrations/sayintentions.md</c>: the gate
/// used to flow app → SI only). SayIntentions writes ONE <c>assigned_gate</c> field for the
/// whole flight: on the ground before departure it is the departure stand, and ATC's "taxi
/// to gate …" after landing (or an earlier reassignment) replaces it. So the rule remembers
/// what it saw before takeoff and only treats a <em>different</em> gate seen from the climb
/// onward as the arrival gate. The pilot's own queued gate is never overridden. Not
/// thread-safe — the owning module serializes calls.
/// </summary>
public sealed class AtcAssignedGateRule
{
    private string? _departureGate;
    private string? _lastSeen;
    private string? _lastQueued;
    private bool _airborneSeen;

    /// <summary>Phases where <c>assigned_gate</c> means the departure stand.</summary>
    public static bool IsBeforeTakeoff(FlightPhase phase)
        => phase is FlightPhase.ColdAndDark or FlightPhase.Preflight or FlightPhase.Departure
            or FlightPhase.PushbackAndStart or FlightPhase.TaxiOut or FlightPhase.TakeoffRoll;

    /// <summary>Phases where a (changed) <c>assigned_gate</c> is the arrival gate.</summary>
    public static bool IsArrivalCandidatePhase(FlightPhase phase)
        => phase is FlightPhase.InitialClimb or FlightPhase.Climb or FlightPhase.Cruise
            or FlightPhase.Descent or FlightPhase.Approach or FlightPhase.LandingRollout
            or FlightPhase.TaxiIn;

    /// <summary>Feeds one reading. <paramref name="pilotQueuedGate"/> is the coordinator's
    /// pending gate (null when the pilot queued nothing).</summary>
    public AtcGateDecision Observe(string? assignedGate, FlightPhase phase, string? pilotQueuedGate)
    {
        var gate = ArrivalGatePlan.Normalize(assignedGate);
        if (gate.Length == 0)
        {
            _lastSeen = null;
            return AtcGateDecision.None;
        }

        if (IsBeforeTakeoff(phase))
        {
            _departureGate = gate;
            _lastSeen = gate;
            return AtcGateDecision.DepartureGateNoted;
        }

        if (!IsArrivalCandidatePhase(phase))
        {
            _lastSeen = gate;
            return AtcGateDecision.None;
        }

        // Only a change is a new fact — flight.json is polled every second. The first
        // airborne reading is reported once even unchanged, so the log shows what ATC held
        // when the flight became a candidate.
        var changed = !string.Equals(gate, _lastSeen, StringComparison.Ordinal);
        var first = !_airborneSeen;
        _airborneSeen = true;
        if (!changed && !first)
        {
            return AtcGateDecision.None;
        }

        _lastSeen = gate;
        if (string.Equals(gate, _departureGate, StringComparison.Ordinal))
        {
            return AtcGateDecision.SameAsDeparture;
        }

        if (string.Equals(gate, _lastQueued, StringComparison.Ordinal))
        {
            return AtcGateDecision.None;
        }

        var pilot = ArrivalGatePlan.Normalize(pilotQueuedGate);
        if (pilot.Length > 0 && !string.Equals(pilot, _lastQueued, StringComparison.Ordinal))
        {
            return AtcGateDecision.PilotGateWins;
        }

        _lastQueued = gate;
        return AtcGateDecision.QueueArrival;
    }

    /// <summary>New flight (ColdAndDark / Shutdown edge): forget the previous leg.</summary>
    public void Reset()
    {
        _departureGate = null;
        _lastSeen = null;
        _lastQueued = null;
        _airborneSeen = false;
    }
}
