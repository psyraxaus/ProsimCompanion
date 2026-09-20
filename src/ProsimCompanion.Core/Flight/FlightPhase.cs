namespace ProsimCompanion.Core.Flight;

/// <summary>
/// The single flight-phase model every feature consumes (matches the proven Prosim2FO model).
/// Phase is derived centrally by the <see cref="FlightStateEngine"/>; features subscribe to
/// <see cref="FlightStateEngine.PhaseChanged"/> and never re-derive phase themselves.
/// </summary>
public enum FlightPhase
{
    /// <summary>No usable data yet (before the first sample, or source unavailable with no prior phase).</summary>
    Unknown = 0,

    /// <summary>Aircraft unpowered on the ground — nothing running.</summary>
    ColdAndDark,

    /// <summary>Powered on the ground, engines off — cockpit prep, servicing.</summary>
    Preflight,

    /// <summary>Boarding underway or done, still at the gate with engines off — the departure
    /// is in progress (owner decision 2026-09-20, reinstating the strip's old DEPARTURE block
    /// as a real engine phase so the phase text and the progress strip can never disagree,
    /// issue #110). Entered on boarding evidence, held until pushback / engine start / power
    /// off — a progress bar never steps back within one departure.</summary>
    Departure,

    /// <summary>Pushback in progress and/or an engine spooling up on start.</summary>
    PushbackAndStart,

    /// <summary>Engines running, taxiing to the runway.</summary>
    TaxiOut,

    /// <summary>Take-off thrust set, accelerating on the runway, still on the ground.</summary>
    TakeoffRoll,

    /// <summary>Just airborne, below the acceleration/clean-up altitude.</summary>
    InitialClimb,

    /// <summary>Established climb toward cruise.</summary>
    Climb,

    /// <summary>Level at cruise.</summary>
    Cruise,

    /// <summary>Descending toward the destination.</summary>
    Descent,

    /// <summary>On approach, configuring to land.</summary>
    Approach,

    /// <summary>Touched down, rolling out / decelerating on the runway.</summary>
    LandingRollout,

    /// <summary>Taxiing to the gate after landing.</summary>
    TaxiIn,

    /// <summary>At the stand, shutting down.</summary>
    Shutdown,
}

/// <summary>Phase-set predicates shared by every pillar, so a phase added to the model is
/// added to each window in exactly one place (the 2026-09-20 Departure insertion touched
/// a dozen hand-written "Preflight or ColdAndDark" checks — never again).</summary>
public static class FlightPhaseExtensions
{
    /// <summary>Parked at the stand before the push: unpowered, in cockpit prep, or with the
    /// departure (boarding) underway. Ground preparation, departure services, the OFP gate
    /// and the FOB restore all belong to this window.</summary>
    public static bool IsAtGate(this FlightPhase phase)
        => phase is FlightPhase.ColdAndDark or FlightPhase.Preflight or FlightPhase.Departure;

    /// <summary>At the gate or pushing/starting — the pre-taxi ground window the cabin and
    /// company-channel features treat as "before departure".</summary>
    public static bool IsBeforeTaxiOut(this FlightPhase phase)
        => phase.IsAtGate() || phase == FlightPhase.PushbackAndStart;
}
