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

    /// <summary>Powered on the ground, engines off — cockpit prep / boarding.</summary>
    Preflight,

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
