using ProsimCompanion.Core.Configuration;
using static ProsimCompanion.Core.Flight.FlightPhase;

namespace ProsimCompanion.Core.Flight;

/// <summary>
/// The flight-phase graph as an ordered rule table — one list for on-ground samples, one for
/// airborne. Order is evaluation priority: the first rule whose <c>From</c> contains the
/// current phase and whose <c>When</c> holds decides the tick. Every dated remark below is
/// field archaeology from a flight test; keep them with the rule they justify.
/// </summary>
public static class FlightPhaseRules
{
    /// <summary>A bounce after touchdown: airborne again but still within this radio
    /// altitude is the same landing, not a go-around and not a new approach.</summary>
    private const double BounceRaFt = 50;

    /// <summary>Spawn/restore recovery: below both of these the aircraft is stationary.</summary>
    private const double StationaryGroundSpeedKt = 5;
    private const double StationaryIasKt = 30;

    /// <summary>Engines running at the stand right after start: below this the aircraft has
    /// not begun to move.</summary>
    private const double StartCompleteGroundSpeedKt = 2;

    private static readonly IReadOnlySet<FlightPhase> AirbornePhases =
        new HashSet<FlightPhase> { InitialClimb, Climb, Cruise, Descent, Approach };

    private static readonly IReadOnlySet<FlightPhase> AtGatePhases =
        new HashSet<FlightPhase> { Unknown, ColdAndDark, Preflight, PushbackAndStart };

    private static readonly IReadOnlySet<FlightPhase> DepartureRegressionSources =
        new HashSet<FlightPhase> { PushbackAndStart, TaxiOut, TakeoffRoll };

    private static readonly IReadOnlySet<FlightPhase> LiftoffSources =
        new HashSet<FlightPhase> { TakeoffRoll, TaxiOut, PushbackAndStart, Preflight, InitialClimb };

    private static readonly IReadOnlySet<FlightPhase> ApproachSources =
        new HashSet<FlightPhase>(Enum.GetValues<FlightPhase>().Except([InitialClimb, TakeoffRoll]));

    /// <summary>Rules for samples with weight on wheels.</summary>
    public static IReadOnlyList<PhaseRule> Ground { get; } =
    [
        Rule("power-off", null, ColdAndDark,
            (_, s, _) => !s.AircraftPowered,
            DefaultDebounce,
            (_, _) => "aircraft unpowered"),

        // Recovery rule (smoke-test find 2026-08-02): on the ground from a *flight* phase
        // with engines off and no motion is a session restore / spawn at the gate — go to
        // Preflight, never into pushback/rollout logic. Without this, spawn-time misreads
        // (ProSim reports airborne defaults while MSFS loads) cascade into a stuck
        // PushbackAndStart that blocks the entire ground automation.
        Rule("spawn-recovery", AirbornePhases, Preflight,
            (_, s, _) => !s.AnyEngineRunning
                && s.GroundSpeedKt < StationaryGroundSpeedKt && s.IndicatedAirspeedKt < StationaryIasKt,
            DefaultDebounce,
            (from, _) => $"on the ground from {from}, engines off and stationary — session restore"),

        Rule("touchdown", Set(Approach, Descent), LandingRollout,
            (_, _, _) => true,
            Immediate,
            (_, s) => $"touchdown at {s.IndicatedAirspeedKt:F0} kt"),

        // Ground contact straight from InitialClimb with take-off thrust still set is a
        // bounce on rotation — the roll continues.
        Rule("bounce-on-rotation", Set(InitialClimb), TakeoffRoll,
            (_, s, _) => s.TakeoffThrustSet,
            Immediate,
            (_, s) => $"ground contact with take-off thrust set at {s.IndicatedAirspeedKt:F0} kt — bounce"),

        // Prosim2GSX-style intercept: a landing that never met the Approach criteria (visual
        // circuit, engine-out land-ahead) still rolls out — it must not fall into the
        // departure rules and read as a taxi-out.
        Rule("unexpected-touchdown", AirbornePhases, LandingRollout,
            (_, s, o) => !s.TakeoffThrustSet && s.IndicatedAirspeedKt > o.RolloutCompleteKt,
            Immediate,
            (from, s) => $"ground contact from {from} at {s.IndicatedAirspeedKt:F0} kt"),

        Rule("ground-from-flight-taxiing", Set(Climb, Cruise), TaxiIn,
            (_, s, _) => s.AnyEngineRunning,
            DefaultDebounce,
            (from, s) => $"on the ground from {from} at taxi speed ({s.GroundSpeedKt:F0} kt)"),

        Hold("rollout-hold", Set(LandingRollout),
            (_, s, o) => s.IndicatedAirspeedKt > o.RolloutCompleteKt),

        Rule("rollout-complete", Set(LandingRollout), TaxiIn,
            (_, _, _) => true,
            DefaultDebounce,
            (_, s) => $"decelerated to {s.IndicatedAirspeedKt:F0} kt"),

        // Shutdown = engines cut AND stopped (brake set or below parked speed) — an engine
        // reading off while still rolling is a data blip or a tow, not the stand.
        Rule("shutdown", Set(TaxiIn), Shutdown,
            (_, s, o) => !s.AnyEngineRunning && (s.ParkBrakeSet || s.GroundSpeedKt < o.ParkedGroundSpeedKt),
            DefaultDebounce,
            (_, s) => $"engines shut down at the stand (brake {(s.ParkBrakeSet ? "set" : "released")})"),

        Hold("taxi-in-hold", Set(TaxiIn), (_, _, _) => true),

        // Turnaround without a cold-and-dark reset (review 2026-08-29): the beacon
        // corroborates every start-side cue, so a shut-down aircraft never reads a state
        // flicker as the next leg.
        Rule("turnaround-start", Set(Shutdown), PushbackAndStart,
            (_, s, o) => s.BeaconOn
                && (s.EngineStarting || s.AnyEngineRunning
                    || ((s.ApuRunning || s.PushbackActive) && !s.ParkBrakeSet
                        && s.GroundSpeedKt <= o.PushbackMaxGroundSpeedKt)),
            DefaultDebounce,
            (_, s) => s.EngineStarting || s.AnyEngineRunning
                ? "beacon on, engine start at the stand — turnaround"
                : "beacon on, brake released after shutdown — turnaround push"),

        // The next leg's Preflight begins only once the arrival is DONE with the aircraft
        // (deboarding completed — stamped by the engine from GroundOpsSignals) and it has
        // sat parked with the beacon off for the hold. A time-only version of this rule
        // (2026-08-29 ESSA) fired 30 s after shutdown mid-deboarding: ground prep
        // repositioned the aircraft under the passengers and departure services ran.
        // Without a ground-ops layer the rule never fires; the beacon rule above still
        // starts the next leg on real departure evidence.
        Rule("turnaround-arrival-complete", Set(Shutdown), Preflight,
            (_, s, o) => s.ArrivalComplete && !s.AnyEngineRunning && s.ParkBrakeSet && !s.BeaconOn
                && s.GroundSpeedKt < o.ParkedGroundSpeedKt,
            (_, o) => TimeSpan.FromSeconds(o.TurnaroundHoldSeconds),
            (_, _) => "arrival complete (deboarded), parked with beacon off — turnaround"),

        Hold("shutdown-hold", Set(Shutdown), (_, _, _) => true),

        // ---- Departure side (any phase not held above) ----

        // Take-off thrust with the park brake released. The brake gate rejects a static
        // run-up (or a data artifact) at the gate reading as a roll.
        Rule("takeoff-thrust", null, TakeoffRoll,
            (_, s, _) => s.TakeoffThrustSet && !s.ParkBrakeSet,
            Immediate,
            (_, s) => $"take-off thrust set (N1 {s.MaxN1Percent:F0}%) at {s.IndicatedAirspeedKt:F0} kt"),

        // Thrust momentarily reads unset mid-roll (FLEX detent bounce) — stay in the roll.
        Hold("takeoff-roll-hold", Set(TakeoffRoll),
            (_, s, o) => s.IndicatedAirspeedKt > o.TaxiSpeedMaxKt),

        // A genuine engine start (e.g. cross-bleed after a stop) regresses from anywhere.
        Rule("engine-start", null, PushbackAndStart,
            (_, s, _) => s.EngineStarting,
            DefaultDebounce,
            (_, _) => "engine start"),

        // Pushback evidence (issues #100/#104): the beacon stays a NECESSARY gate, the park
        // brake must be released (a parked aircraft with a noisy flag stays Preflight), and
        // the aircraft must be at walking pace. Only at-gate phases enter on this evidence —
        // once the taxi has begun, pushback evidence must never walk the phase back.
        Rule("push-evidence", AtGatePhases, PushbackAndStart,
            (_, s, o) => s.BeaconOn && (s.ApuRunning || s.PushbackActive) && !s.ParkBrakeSet
                && s.GroundSpeedKt <= o.PushbackMaxGroundSpeedKt,
            DefaultDebounce,
            (_, s) => s.PushbackActive
                ? "beacon on, pushback in progress, brake released"
                : "beacon on, APU running, brake released — ready to push/start"),

        // Engines running at the stand right after start still counts as PushbackAndStart
        // until the aircraft begins to move.
        Hold("start-complete-hold", Set(PushbackAndStart),
            (_, s, _) => s.AnyEngineRunning && s.GroundSpeedKt < StartCompleteGroundSpeedKt && s.ParkBrakeSet),

        Rule("taxi-out", null, TaxiOut,
            (_, s, _) => s.AnyEngineRunning,
            DefaultDebounce,
            (_, s) => $"engines running, rolling at {s.GroundSpeedKt:F0} kt"),

        // Departure regressions to Preflight must be deliberate, not a data blip (issue #59).
        Rule("preflight", null, Preflight,
            (_, _, _) => true,
            (from, o) => DepartureRegressionSources.Contains(from)
                ? TimeSpan.FromSeconds(o.DepartureRegressionSeconds)
                : TimeSpan.FromSeconds(o.DefaultDebounceSeconds),
            (from, _) => DepartureRegressionSources.Contains(from)
                ? $"engines off, no start, no push — sustained contradiction of {from}"
                : "powered on the ground, engines off"),
    ];

    /// <summary>Rules for samples without weight on wheels.</summary>
    public static IReadOnlyList<PhaseRule> Airborne { get; } =
    [
        // Go-around lands on InitialClimb, not Climb: the missed-approach re-brief, the
        // callouts engine and the stabilised-approach monitor all key on
        // Approach/LandingRollout → InitialClimb (review 2026-08-29 — the previous Climb
        // target left all three blind). The VS gate (issue #99) and the settle keep a
        // level-off blip from reading as one.
        Rule("go-around", Set(Approach, LandingRollout), InitialClimb,
            (_, s, o) => s.VerticalSpeedFpm > o.GoAroundVsFpm,
            (_, o) => TimeSpan.FromSeconds(o.GoAroundSettleSeconds),
            (from, s) => $"go-around from {from}: climbing {s.VerticalSpeedFpm:F0} fpm"),

        Hold("bounce-hold", Set(LandingRollout),
            (_, s, _) => s.RadioAltitudeFt < BounceRaFt),

        Rule("liftoff", LiftoffSources, InitialClimb,
            (_, s, o) => s.RadioAltitudeFt < o.InitialClimbCeilingRaFt && s.VerticalSpeedFpm > -o.ClimbDescentVsFpm,
            Immediate,
            (_, s) => $"airborne at {s.IndicatedAirspeedKt:F0} kt, RA {s.RadioAltitudeFt:F0} ft"),

        // Low and descending (or configured) near the ground: approach. Gear down alone must
        // never outvote a positive climb — a gear lever stuck down (dead hardware panel,
        // 2026-08-09 flight, issue #48) flipped Climb→Approach at +VS and the cabin announced
        // "secure for landing" on climb-out.
        Rule("approach", ApproachSources, Approach,
            (_, s, o) => s.RadioAltitudeFt < o.ApproachRaCeilingFt
                && (s.VerticalSpeedFpm < -o.ClimbDescentVsFpm
                    || (s.GearDown && s.VerticalSpeedFpm <= o.ClimbDescentVsFpm)),
            FirstClassificationOrDefault,
            (_, s) => s.VerticalSpeedFpm < 0
                ? $"RA {s.RadioAltitudeFt:F0} ft, descending {(-s.VerticalSpeedFpm):F0} fpm"
                : $"RA {s.RadioAltitudeFt:F0} ft, gear down, level"),

        // From Approach a climb below go-around grade is a level-off blip (issue #99).
        Hold("approach-level-off-hold", Set(Approach),
            (_, s, o) => s.VerticalSpeedFpm > o.ClimbDescentVsFpm),

        Rule("climb", null, Climb,
            (_, s, o) => s.VerticalSpeedFpm > o.ClimbDescentVsFpm,
            (from, o) => from switch
            {
                // A VS blip at cruise (turbulence, altimetry) must not flip to Climb; a real
                // step climb sustains its rate far past the settle (issue #105).
                Cruise => TimeSpan.FromSeconds(o.CruiseToClimbSettleSeconds),
                Unknown => TimeSpan.FromSeconds(o.FirstAirborneClassificationSeconds),
                _ => TimeSpan.FromSeconds(o.DefaultDebounceSeconds),
            },
            (_, s) => $"climbing {s.VerticalSpeedFpm:F0} fpm"),

        // Asymmetric descent gate: once established in Descent, a shallow segment still
        // counts as descending; from anywhere else the descent must be decisive.
        Hold("descent-hold", Set(Descent),
            (_, s, o) => s.VerticalSpeedFpm < -o.DescentExitVsFpm),

        Rule("descent", null, Descent,
            (_, s, o) => s.VerticalSpeedFpm < -o.DescentEntryVsFpm,
            FirstClassificationOrDefault,
            (_, s) => $"descending {(-s.VerticalSpeedFpm):F0} fpm"),

        // Level flight: level segments during approach stay approach; an established cruise
        // stays cruise.
        Hold("level-hold", Set(Approach, Cruise), (_, _, _) => true),

        // ENTERING cruise additionally needs a cruise-plausible altitude (issue #105): a
        // level-off at a climb/descent constraint holds the current phase instead of
        // committing a bogus Cruise.
        Rule("cruise", null, Cruise,
            (_, s, o) => IsCruisePlausibleAltitude(s, o),
            (from, o) => from switch
            {
                Descent => TimeSpan.FromSeconds(o.DescentToCruiseSettleSeconds),
                Unknown => TimeSpan.FromSeconds(o.FirstAirborneClassificationSeconds),
                _ => TimeSpan.FromSeconds(o.CruiseSettleSeconds),
            },
            (_, s) => s.FmsCruiseAltFt >= 1000
                ? $"level at {s.AltitudeFt:F0} ft near FMS cruise {s.FmsCruiseAltFt:F0} ft"
                : $"level at {s.AltitudeFt:F0} ft (no FMS cruise altitude)"),
    ];

    /// <summary>Every rule in evaluation order, ground first — for documentation surfaces.</summary>
    public static IEnumerable<PhaseRule> All => Ground.Concat(Airborne);

    /// <summary>Level flight only reads as cruise near the FMS cruise level; when the FMS has
    /// none entered (0 / garbage), above a conservative floor. The tolerance sits below any
    /// real step-climb increment, so intermediate levels stay climb.</summary>
    internal static bool IsCruisePlausibleAltitude(FlightDataSnapshot s, FlightStateOptions o)
        => s.FmsCruiseAltFt >= 1000
            ? s.AltitudeFt >= s.FmsCruiseAltFt - o.CruiseLevelToleranceFt
            : s.AltitudeFt >= o.CruiseFloorNoFmsFt;

    private static TimeSpan Immediate(FlightPhase from, FlightStateOptions o) => TimeSpan.Zero;

    private static TimeSpan DefaultDebounce(FlightPhase from, FlightStateOptions o)
        => TimeSpan.FromSeconds(o.DefaultDebounceSeconds);

    /// <summary>The very first classification of a session leaping straight to a flight
    /// phase is either a mid-flight app restart (5 s costs nothing) or connection warm-up
    /// garbage (5 s outlives it) — issue #59, 2026-08-17 recurrence.</summary>
    private static TimeSpan FirstClassificationOrDefault(FlightPhase from, FlightStateOptions o)
        => from == Unknown
            ? TimeSpan.FromSeconds(o.FirstAirborneClassificationSeconds)
            : TimeSpan.FromSeconds(o.DefaultDebounceSeconds);

    private static IReadOnlySet<FlightPhase> Set(params FlightPhase[] phases) => new HashSet<FlightPhase>(phases);

    private static PhaseRule Rule(
        string id,
        IReadOnlySet<FlightPhase>? from,
        FlightPhase to,
        Func<FlightPhase, FlightDataSnapshot, FlightStateOptions, bool> when,
        Func<FlightPhase, FlightStateOptions, TimeSpan> debounce,
        Func<FlightPhase, FlightDataSnapshot, string> reason)
        => new(id, from, to, when, debounce, reason);

    private static PhaseRule Hold(
        string id,
        IReadOnlySet<FlightPhase> from,
        Func<FlightPhase, FlightDataSnapshot, FlightStateOptions, bool> when)
        => new(id, from, null, when, Immediate, (_, _) => "hold");
}
