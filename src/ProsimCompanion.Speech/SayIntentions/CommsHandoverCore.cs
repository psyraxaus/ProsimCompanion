using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;

namespace ProsimCompanion.Speech.SayIntentions;

/// <summary>Who works the radios: the captain (pilot) or the SayIntentions copilot.</summary>
public enum CommsOwner
{
    /// <summary>Not set yet this leg (the app started, or SayIntentions has no flight).</summary>
    Unknown,

    /// <summary><c>L:SIAI_COPILOT</c> = 0 — the pilot talks to ATC.</summary>
    Captain,

    /// <summary><c>L:SIAI_COPILOT</c> = 1 — the SayIntentions copilot talks and reads back.</summary>
    Copilot,
}

/// <summary>One hand-over the shell has to carry out: the setVar write, the FO line and the
/// <c>sayintentions.comms</c> event.</summary>
/// <param name="Owner">Who has the radios from now on.</param>
/// <param name="Trigger">Why: <c>flight-active</c>, <c>holding-point</c>, <c>climb</c>,
/// <c>cruise</c>, <c>tower-handoff</c>, <c>approach</c>, <c>taxi-in</c> or <c>voice</c>.</param>
/// <param name="Phase">The committed phase at the time.</param>
public sealed record CommsHandoverDecision(CommsOwner Owner, string Trigger, FlightPhase Phase);

/// <summary>The switches the core reads (a snapshot of <see cref="CommsHandoverOptions"/>, so a
/// test needs no options monitor).</summary>
public sealed record CommsHandoverRules(
    bool GateToCopilot,
    bool HoldingPointToCaptain,
    double HoldingPointDistanceNm,
    bool ClimbToCopilot,
    bool ApproachToCaptain,
    bool TaxiInToCopilot)
{
    public static CommsHandoverRules From(CommsHandoverOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new CommsHandoverRules(
            options.GateToCopilot,
            options.HoldingPointToCaptain,
            Math.Max(0.05, options.HoldingPointDistanceNm),
            options.ClimbToCopilot,
            options.ApproachToCaptain,
            options.TaxiInToCopilot);
    }
}

/// <summary>
/// Pure state machine for the SayIntentions comms hand-over by flight phase (issue #171,
/// 2026-10-11). Inputs are the app's own facts — the committed phase edges, the distance to
/// the departure runway threshold from the app's runway geometry, ATC's hand-offs from the
/// comms history, and the pilot's voice — never flight.json's <c>distance_to_runway</c>, which
/// was absent on both 2026-10-10 departures and left the legacy gate dead. Each call returns
/// at most one decision; at most one automatic hand-over happens per phase edge, and a step
/// that would hand the radios to whoever already has them is silent. Not thread-safe — the
/// owning service serialises calls under its lock.
/// </summary>
public sealed class CommsHandoverCore
{
    private FlightPhase _phase = FlightPhase.Unknown;
    private int _edge;
    private int _lastHandoverEdge = -1;
    private bool _holdingPointDone;
    private bool _climbSeen;

    /// <summary>Who has the radios as far as the app knows.</summary>
    public CommsOwner Owner { get; private set; } = CommsOwner.Unknown;

    /// <summary>The phase the core last saw.</summary>
    public FlightPhase Phase => _phase;

    /// <summary>(a) SayIntentions reports a new active flight while the aircraft is still on the
    /// ground before the takeoff roll: the copilot owns clearance, push and start.</summary>
    public CommsHandoverDecision? OnFlightActive(FlightPhase phase, CommsHandoverRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _phase = phase;
        var onGroundBeforeTakeoff = phase.IsBeforeTaxiOut() || phase is FlightPhase.TaxiOut or FlightPhase.Unknown;
        if (!rules.GateToCopilot || !onGroundBeforeTakeoff || Owner == CommsOwner.Copilot)
        {
            return null;
        }

        return Take(CommsOwner.Copilot, "flight-active");
    }

    /// <summary>A committed phase edge: (d) Climb → copilot, (f) Approach → captain, (g) TaxiIn
    /// → copilot; Cruise is the copilot's safety net when the Climb edge was skipped. A new
    /// leg (ColdAndDark / Shutdown) forgets the owner.</summary>
    public CommsHandoverDecision? OnPhaseChanged(FlightPhase current, CommsHandoverRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _phase = current;
        _edge++;

        switch (current)
        {
            case FlightPhase.ColdAndDark:
            case FlightPhase.Shutdown:
                Owner = CommsOwner.Unknown;
                _holdingPointDone = false;
                _climbSeen = false;
                return null;

            case FlightPhase.TaxiOut:
                _holdingPointDone = false;
                return null;

            case FlightPhase.Climb:
                _climbSeen = true;
                return rules.ClimbToCopilot && Owner != CommsOwner.Copilot ? Take(CommsOwner.Copilot, "climb") : null;

            case FlightPhase.Cruise:
                // Climb edge skipped (a short hop that levelled off from InitialClimb): the
                // copilot still owns the cruise.
                return !_climbSeen && rules.ClimbToCopilot && Owner != CommsOwner.Copilot ? Take(CommsOwner.Copilot, "cruise") : null;

            case FlightPhase.Approach:
                return rules.ApproachToCaptain && Owner != CommsOwner.Captain ? Take(CommsOwner.Captain, "approach") : null;

            case FlightPhase.TaxiIn:
                return rules.TaxiInToCopilot && Owner != CommsOwner.Copilot ? Take(CommsOwner.Copilot, "taxi-in") : null;

            default:
                return null;
        }
    }

    /// <summary>(c) The distance to the departure runway threshold (nm; null = unknown) on a
    /// position tick. Within the holding-point distance while taxiing out, once per taxi:
    /// the captain takes the radios.</summary>
    public CommsHandoverDecision? OnDistanceToRunway(double? distanceNm, CommsHandoverRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (_holdingPointDone || _phase != FlightPhase.TaxiOut || !rules.HoldingPointToCaptain
            || distanceNm is not { } nm || double.IsNaN(nm) || nm > rules.HoldingPointDistanceNm)
        {
            return null;
        }

        _holdingPointDone = true;
        if (Owner == CommsOwner.Captain || _lastHandoverEdge == _edge)
        {
            return null;
        }

        return Take(CommsOwner.Captain, "holding-point");
    }

    /// <summary>(f) ATC handed the flight to a Tower station. In the descent that is the
    /// approach controller's "contact tower" — the captain takes the radios for the landing
    /// clearance before the Approach edge arrives.</summary>
    public CommsHandoverDecision? OnTowerHandoff(CommsHandoverRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (_phase != FlightPhase.Descent || !rules.ApproachToCaptain
            || Owner == CommsOwner.Captain || _lastHandoverEdge == _edge)
        {
            return null;
        }

        return Take(CommsOwner.Captain, "tower-handoff");
    }

    /// <summary>The pilot's voice override ("I have the radios" / "you have the radios"). Null
    /// when that owner already has them.</summary>
    public CommsHandoverDecision? Override(CommsOwner owner)
    {
        if (owner == CommsOwner.Unknown || owner == Owner)
        {
            return null;
        }

        Owner = owner;
        return new CommsHandoverDecision(owner, "voice", _phase);
    }

    /// <summary>SayIntentions lost its flight: nothing is known about the radios any more.</summary>
    public void Reset()
    {
        Owner = CommsOwner.Unknown;
        _holdingPointDone = false;
        _climbSeen = false;
        _lastHandoverEdge = -1;
    }

    private CommsHandoverDecision Take(CommsOwner owner, string trigger)
    {
        Owner = owner;
        _lastHandoverEdge = _edge;
        return new CommsHandoverDecision(owner, trigger, _phase);
    }
}
