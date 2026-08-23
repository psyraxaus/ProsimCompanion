namespace ProsimCompanion.Core.Flight;

/// <summary>
/// Pure phase derivation: (snapshot, current phase) → target phase. No timers, no state beyond
/// the passed-in current phase — fully unit-testable. Debounce is applied by the engine, not here.
/// Thresholds are conservative and intentionally centralized as constants.
/// </summary>
public static class FlightPhaseEvaluator
{
    // Ground/speed thresholds (kt)
    private const double TaxiSpeedMaxKt = 45;
    private const double RolloutCompleteKt = 35;

    // Vertical thresholds
    private const double InitialClimbCeilingRaFt = 1500;
    private const double ClimbDescentVsFpm = 300;
    private const double ApproachRaCeilingFt = 3500;

    // Leaving Approach upward needs a go-around-grade climb (issue #99, 2026-08-22 LGAV):
    // a +388 fpm level-off blip at 3,000 ft flipped Approach→Climb→Cruise and the FO called
    // "positive climb" / "flaps still extended" on final. A real go-around blows straight
    // through this threshold.
    private const double GoAroundVsFpm = 800;

    // Descent hysteresis (flight test 2026-08-16, issue #59): a symmetric ±300 fpm gate
    // produced four Descent<->Cruise flip-flops in 23 minutes of step-descent/level segments.
    // Entering Descent now needs a decisive rate; leaving Descent for Cruise needs near-level
    // flight (and the engine adds a long Descent->Cruise debounce on top).
    private const double DescentEntryVsFpm = 500;
    private const double DescentExitVsFpm = 100;

    // Cruise-entry altitude gate (issue #105, 2026-08-23 flight): a SID level-off at
    // 3,989 ft committed Cruise on departure (and spoke the ISA advisory there), and
    // arrival level-offs at 7,300/6,000 ft flipped Descent→Cruise. Level flight only
    // counts as cruise near the FMS cruise level; without one, above a conservative floor.
    private const double CruiseLevelToleranceFt = 2000;
    private const double CruiseFloorNoFmsFt = 10000;

    // A tug pushes at walking pace. Above this ground speed the beacon+APU window is a
    // taxi, not a push — without the guard, taxiing with the APU still running (common
    // after a cross-bleed start) would hold PushbackAndStart indefinitely (issue #104).
    private const double PushbackMaxGroundSpeedKt = 10;

    /// <summary>Derives the target phase for a snapshot. Invalid snapshots hold the current phase.</summary>
    public static FlightPhase Evaluate(FlightDataSnapshot snapshot, FlightPhase current)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!snapshot.IsValid)
        {
            return current;
        }

        return snapshot.OnGround ? EvaluateOnGround(snapshot, current) : EvaluateAirborne(snapshot, current);
    }

    private static FlightPhase EvaluateOnGround(FlightDataSnapshot s, FlightPhase current)
    {
        if (!s.AircraftPowered)
        {
            return FlightPhase.ColdAndDark;
        }

        // Recovery rule (smoke-test find 2026-08-02): landing on the ground from a *flight*
        // phase with engines off and no motion is a session restore / spawn at the gate — go to
        // Preflight, never into pushback/rollout logic. Without this, spawn-time flight-phase
        // misreads (ProSim reports airborne defaults while MSFS loads) cascade into a stuck
        // PushbackAndStart that blocks the entire ground automation.
        var cameFromFlight = current is FlightPhase.InitialClimb or FlightPhase.Climb
            or FlightPhase.Cruise or FlightPhase.Descent or FlightPhase.Approach;
        if (cameFromFlight && !s.AnyEngineRunning && s.GroundSpeedKt < 5 && s.IndicatedAirspeedKt < 30)
        {
            return FlightPhase.Preflight;
        }

        // Arrival context: rolling out after touchdown until decelerated, then taxi in, then
        // shutdown once the engines are cut.
        var arrivalContext = current is FlightPhase.Approach or FlightPhase.Descent
            or FlightPhase.LandingRollout or FlightPhase.TaxiIn or FlightPhase.Shutdown;
        if (arrivalContext)
        {
            if (current is FlightPhase.Approach or FlightPhase.Descent)
            {
                return FlightPhase.LandingRollout;
            }

            if (current == FlightPhase.LandingRollout)
            {
                return s.IndicatedAirspeedKt > RolloutCompleteKt ? FlightPhase.LandingRollout : FlightPhase.TaxiIn;
            }

            if (!s.AnyEngineRunning)
            {
                return FlightPhase.Shutdown;
            }

            return FlightPhase.TaxiIn;
        }

        // Departure context.
        if (s.TakeoffThrustSet)
        {
            return FlightPhase.TakeoffRoll;
        }

        if (current == FlightPhase.TakeoffRoll && s.IndicatedAirspeedKt > TaxiSpeedMaxKt)
        {
            // Thrust momentarily reads unset mid-roll (e.g. FLEX detent bounce) — stay in the roll.
            return FlightPhase.TakeoffRoll;
        }

        // Pushback evidence (issues #100/#104). The pushback flag reads the settled enum
        // (3 = idle, 0 = pushing — 2026-08-23 instrumented flight); the beacon stays a
        // NECESSARY gate (Prosim2FO parity), the park brake must be released (a parked
        // aircraft with a noisy flag stays Preflight), and the aircraft must be at walking
        // pace — beacon+APU while rolling at taxi speed is a taxi, not a push. Only at-gate
        // phases may enter on this evidence — once the taxi has begun, pushback evidence
        // must never walk the phase back; a genuine engine start (e.g. cross-bleed after a
        // stop) still regresses from anywhere.
        var atGate = current is FlightPhase.Unknown or FlightPhase.ColdAndDark
            or FlightPhase.Preflight or FlightPhase.PushbackAndStart;
        if (s.EngineStarting
            || (atGate && s.BeaconOn && (s.ApuRunning || s.PushbackActive) && !s.ParkBrakeSet
                && s.GroundSpeedKt <= PushbackMaxGroundSpeedKt))
        {
            return FlightPhase.PushbackAndStart;
        }

        if (s.AnyEngineRunning)
        {
            // Engines running at the stand right after start still counts as PushbackAndStart
            // until the aircraft begins to move.
            if (current == FlightPhase.PushbackAndStart && s.GroundSpeedKt < 2 && s.ParkBrakeSet)
            {
                return FlightPhase.PushbackAndStart;
            }

            return FlightPhase.TaxiOut;
        }

        return FlightPhase.Preflight;
    }

    private static FlightPhase EvaluateAirborne(FlightDataSnapshot s, FlightPhase current)
    {
        // Fresh into the air from the runway.
        var departureContext = current is FlightPhase.TakeoffRoll or FlightPhase.TaxiOut
            or FlightPhase.PushbackAndStart or FlightPhase.Preflight or FlightPhase.InitialClimb;
        if (departureContext && s.RadioAltitudeFt < InitialClimbCeilingRaFt && s.VerticalSpeedFpm > -ClimbDescentVsFpm)
        {
            return FlightPhase.InitialClimb;
        }

        // Low and descending (or configured) near the ground: approach. Gear down alone must
        // never outvote a positive climb — a gear lever stuck down (dead hardware panel,
        // 2026-08-09 flight, issue #48) flipped Climb→Approach at +VS and the cabin announced
        // "secure for landing" on climb-out.
        if (s.RadioAltitudeFt < ApproachRaCeilingFt
            && (s.VerticalSpeedFpm < -ClimbDescentVsFpm
                || (s.GearDown && s.VerticalSpeedFpm <= ClimbDescentVsFpm))
            && current is not (FlightPhase.InitialClimb or FlightPhase.TakeoffRoll))
        {
            return FlightPhase.Approach;
        }

        if (s.VerticalSpeedFpm > ClimbDescentVsFpm)
        {
            // From Approach, only a go-around-grade climb exits upward (issue #99) — a
            // level-off blip stays Approach. Departure climbs are unaffected, and #48's rule
            // (gear down never outvotes a positive climb) applies to Climb context, not here.
            if (current == FlightPhase.Approach && s.VerticalSpeedFpm < GoAroundVsFpm)
            {
                return FlightPhase.Approach;
            }

            return FlightPhase.Climb;
        }

        // Asymmetric descent gate: once established in Descent, a shallow segment (down to
        // -DescentExitVsFpm) still counts as descending; from anywhere else the descent must
        // be decisive before we leave Cruise/Climb context.
        var descentThresholdFpm = current == FlightPhase.Descent ? DescentExitVsFpm : DescentEntryVsFpm;
        if (s.VerticalSpeedFpm < -descentThresholdFpm)
        {
            return FlightPhase.Descent;
        }

        // Level flight. Level segments during approach stay approach; an established cruise
        // stays cruise. ENTERING cruise additionally needs a cruise-plausible altitude
        // (issue #105): a level-off at a climb/descent constraint holds the current phase
        // instead of committing a bogus Cruise (which fired the ISA advisory at 4,000 ft
        // on departure and flip-flopped the arrival at 7,300/6,000 ft on 2026-08-23).
        if (current is FlightPhase.Approach or FlightPhase.Cruise)
        {
            return current;
        }

        return IsCruisePlausibleAltitude(s) ? FlightPhase.Cruise : current;
    }

    /// <summary>Level flight only reads as cruise near the FMS cruise level; when the FMS
    /// has none entered (0 / garbage), above a conservative floor. The tolerance sits below
    /// any real step-climb increment (2,000 ft), so intermediate levels stay climb.</summary>
    private static bool IsCruisePlausibleAltitude(FlightDataSnapshot s)
        => s.FmsCruiseAltFt >= 1000
            ? s.AltitudeFt >= s.FmsCruiseAltFt - CruiseLevelToleranceFt
            : s.AltitudeFt >= CruiseFloorNoFmsFt;
}
