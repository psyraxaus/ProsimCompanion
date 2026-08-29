namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Flight-phase engine tuning (section <c>flightState</c>). Every threshold the phase rule
/// table reads lives here so a field-found edge case is a settings change (and a replayed
/// recording) rather than a code patch. Defaults are the values proven on the 2026-08
/// flight tests; change them only with a recording that shows why.
/// </summary>
public sealed class FlightStateOptions : IOptionSection
{
    public static string SectionName => "flightState";

    /// <summary>The compiled-in defaults, used wherever no configuration is bound (the pure
    /// evaluator facade, replay, tests).</summary>
    public static FlightStateOptions Default { get; } = new();

    // ---- Speeds (kt) ----

    /// <summary>Above this IAS a take-off roll is committed even if take-off thrust momentarily
    /// reads unset (FLEX detent bounce).</summary>
    public double TaxiSpeedMaxKt { get; set; } = 45;

    /// <summary>Rollout ends (TaxiIn) once IAS drops below this.</summary>
    public double RolloutCompleteKt { get; set; } = 35;

    /// <summary>A tug pushes at walking pace; above this ground speed beacon+APU is a taxi,
    /// not a push (issue #104).</summary>
    public double PushbackMaxGroundSpeedKt { get; set; } = 10;

    /// <summary>Below this ground speed the aircraft counts as parked (ProSim's ground speed
    /// can sit at 1–2 kt fully stopped on a hardware cockpit).</summary>
    public double ParkedGroundSpeedKt { get; set; } = 2;

    // ---- Vertical (ft / fpm) ----

    /// <summary>Radio altitude below which a departure stays InitialClimb.</summary>
    public double InitialClimbCeilingRaFt { get; set; } = 1500;

    /// <summary>|VS| below this is level flight; above it a climb.</summary>
    public double ClimbDescentVsFpm { get; set; } = 300;

    /// <summary>Radio altitude below which a descent (or gear-down level flight) is Approach.</summary>
    public double ApproachRaCeilingFt { get; set; } = 3500;

    /// <summary>Leaving Approach upward needs a go-around-grade climb (issue #99): a level-off
    /// blip must never read as a go-around.</summary>
    public double GoAroundVsFpm { get; set; } = 800;

    /// <summary>Entering Descent needs a decisive rate (issue #59 hysteresis).</summary>
    public double DescentEntryVsFpm { get; set; } = 500;

    /// <summary>Once in Descent, a segment shallower than this still counts as descending.</summary>
    public double DescentExitVsFpm { get; set; } = 100;

    // ---- Cruise gate (issue #105) ----

    /// <summary>Level flight only reads as Cruise within this of the FMS cruise altitude.</summary>
    public double CruiseLevelToleranceFt { get; set; } = 2000;

    /// <summary>Without an FMS cruise altitude, level flight reads as Cruise only above this.</summary>
    public double CruiseFloorNoFmsFt { get; set; } = 10000;

    // ---- Engine behaviour ----

    /// <summary>Consecutive samples the raw on-ground flag must agree before the committed
    /// ground/air state flips (Prosim2GSX <c>GroundTicks</c> parity). 1 disables the filter.</summary>
    public int GroundContactAgreeSamples { get; set; } = 2;

    /// <summary>Once the arrival is complete (deboarding done) AND the aircraft has sat parked
    /// (engines off, brake set, beacon off, stopped) for this long, Shutdown → Preflight: the
    /// turnaround begins without a cold-and-dark reset. Counted from whichever came last.</summary>
    public double TurnaroundHoldSeconds { get; set; } = 30;

    /// <summary>Interval of the "still thinking" log line while live (0 disables).</summary>
    public double HeartbeatSeconds { get; set; } = 60;

    // ---- Debounce (seconds) ----

    /// <summary>Hold applied to any transition without a specific settle below.</summary>
    public double DefaultDebounceSeconds { get; set; } = 1;

    /// <summary>Entering Cruise needs sustained level flight.</summary>
    public double CruiseSettleSeconds { get; set; } = 5;

    /// <summary>Descent → Cruise: the level segments of a step descent must not flip-flop
    /// (four flips in 23 min on 2026-08-16).</summary>
    public double DescentToCruiseSettleSeconds { get; set; } = 15;

    /// <summary>Cruise → Climb: a VS blip at cruise is turbulence; a step climb sustains.</summary>
    public double CruiseToClimbSettleSeconds { get; set; } = 10;

    /// <summary>Approach → go-around: second layer behind the VS gate (issue #99).</summary>
    public double GoAroundSettleSeconds { get; set; } = 5;

    /// <summary>Walking a departure phase back to Preflight needs sustained contradictory
    /// evidence (issue #59) — a transient engines-off read must not reset the ground automation.</summary>
    public double DepartureRegressionSeconds { get; set; } = 5;

    /// <summary>The first classification of a session that lands straight on a flight phase
    /// (mid-flight restart or connection warm-up garbage) must sustain this long.</summary>
    public double FirstAirborneClassificationSeconds { get; set; } = 5;

    // ---- Flight sample recorder ----

    /// <summary>Write a compact <c>flight-sample</c> event to the session log while live, so
    /// the flight can be replayed through the phase engine offline.</summary>
    public bool RecordSamples { get; set; } = true;

    /// <summary>Recorder cadence. Unchanged samples are skipped, with a keep-alive every
    /// <see cref="SampleKeepAliveSeconds"/>.</summary>
    public double SampleIntervalSeconds { get; set; } = 1;

    /// <summary>Maximum silence between two identical samples in the recording.</summary>
    public double SampleKeepAliveSeconds { get; set; } = 10;
}
