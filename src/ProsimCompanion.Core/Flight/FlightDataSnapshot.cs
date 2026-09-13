namespace ProsimCompanion.Core.Flight;

/// <summary>
/// One sampled view of the aircraft, taken every engine tick. This record is the single seam
/// between data sources and phase derivation — a replay source can substitute the live one.
/// The "valid or hold previous decision" rule applies: when <see cref="IsValid"/> is false the
/// engine keeps its current phase rather than reacting to garbage.
/// </summary>
public sealed record FlightDataSnapshot
{
    /// <summary>False while the source is disconnected or values are stale.</summary>
    public bool IsValid { get; init; }

    /// <summary>True only once every phase-critical dataref has received a first value and the
    /// connection is fresh. Distinct from <see cref="IsValid"/>, which only proves the IAS ref
    /// is alive: ProSim registers subscriptions over several seconds at startup, and a
    /// half-registered set reads as airborne-with-gear-down garbage (flight test 2026-08-16,
    /// issue #59 — Unknown→Approach with ias=0/gs=0 six seconds before the gear refs
    /// registered). The flight state engine refuses to classify while this is false.</summary>
    public bool IsReady { get; init; }

    public bool OnGround { get; init; }
    public double IndicatedAirspeedKt { get; init; }
    public double GroundSpeedKt { get; init; }
    public double AltitudeFt { get; init; }
    public double RadioAltitudeFt { get; init; }
    public double VerticalSpeedFpm { get; init; }

    /// <summary>Electrical power available (battery/external) — cold-and-dark discriminator.</summary>
    public bool AircraftPowered { get; init; }

    public bool AnyEngineRunning { get; init; }
    public bool EngineStarting { get; init; }

    /// <summary>Pushback service session in progress. Semantics settled on the 2026-08-23
    /// instrumented flight (issue #104): groundservice.pushback reads 3 when idle (parked,
    /// taxi, airborne) and 0 during the actual push — the pre-#104 "&gt;0 = active" mapping
    /// was inverted and latched this flag true for entire flights, which blocked the
    /// PushbackAndStart→TaxiOut exit. Phase logic still only trusts it behind the beacon
    /// gate (issue #100).</summary>
    public bool PushbackActive { get; init; }
    public bool ParkBrakeSet { get; init; }
    public bool GearDown { get; init; }

    /// <summary>APU running (system.gates.B_APU_RUNNING). With <see cref="BeaconOn"/> this is
    /// the predecessor-parity push/start cue (issue #100): during a GSX-driven push the
    /// pushback dataref reads 0, but beacon-on + APU-running marks the real push window.</summary>
    public bool ApuRunning { get; init; }

    /// <summary>Take-off thrust (FLEX/TOGA) commanded.</summary>
    public bool TakeoffThrustSet { get; init; }

    /// <summary>The ground-ops layer has finished the arrival (deboarding completed) since
    /// this Shutdown began. Not a dataref: the engine stamps it from
    /// <see cref="State.GroundOpsSignals.ArrivalCompleted"/> before evaluation, and the
    /// turnaround rule refuses to leave Shutdown without it (2026-08-29 ESSA).</summary>
    public bool ArrivalComplete { get; init; }

    // ---- Raw diagnostics fields (shown on the Status page; never used for phase logic) ----

    /// <summary>Raw groundservice.pushback value — multi-state; semantics under live verification.</summary>
    public int RawPushbackState { get; init; }

    /// <summary>Raw aircraft.systems.engines.1.state string.</summary>
    public string? RawEngine1State { get; init; }

    /// <summary>Raw aircraft.systems.engines.2.state string.</summary>
    public string? RawEngine2State { get; init; }

    /// <summary>Per-engine raw running booleans (aircraft.engines.N.running — true once the
    /// engine is at idle, ProSim's ECAM "AVAIL" moment) and N2 (%). The engine-start
    /// monitor (issue #131) needs the PER-ENGINE view: an upward N2 crossing is "starting",
    /// running-with-N2-at-idle is "avail" — the state string alone is noisy mid-start (#59).</summary>
    public bool Engine1Running { get; init; }
    public bool Engine2Running { get; init; }
    public double Engine1N2Percent { get; init; }
    public double Engine2N2Percent { get; init; }

    /// <summary>Max engine N1 (%) — feeds the takeoff-thrust heuristic.</summary>
    public double MaxN1Percent { get; init; }

    // ---- Callout/monitoring fields (Phase 5; datarefs confirmed in Prosim2FO) ----

    /// <summary>Average of both engines' N1 (%) — thrust-set callout and stabilized-thrust gate.</summary>
    public double AverageN1Percent { get; init; }

    /// <summary>FLEX N1 target (%) from aircraft.engines.limits.flex; 0 when no FLEX set.</summary>
    public double FlexN1Target { get; init; }

    /// <summary>TOGA N1 target (%) from aircraft.engines.limits.toga.</summary>
    public double TogaN1Target { get; init; }

    /// <summary>FMS V1/VR/V2 in knots — ProSim types these Int32; 0 means "not entered".</summary>
    public int V1Kt { get; init; }
    public int VrKt { get; init; }
    public int V2Kt { get; init; }

    /// <summary>VLS from aircraft.FAC1.VLS — the approach-speed reference (there is no VAPP
    /// dataref); 0/absent means unknown.</summary>
    public double VlsKt { get; init; }

    /// <summary>Flap handle position from aircraft.flap.positionHandle:
    /// 0=Up 1=F1 2=F1+F 3=F2 4=F3 5=F4. NOT the S_FC_FLAPS scale — handle 2 is 1+F, not 2.</summary>
    public int FlapHandle { get; init; }

    /// <summary>FCU selected altitude (ft) from system.analog.A_FCU_ALTITUDE.</summary>
    public double FcuAltitudeFt { get; init; }

    /// <summary>FMS cruise altitude (ft) from aircraft.fms.cruiseAlt; 0 = not entered.
    /// Gates Cruise-phase entry (issue #105): a level-off well below the planned cruise
    /// level is a climb/descent constraint, not the cruise.</summary>
    public double FmsCruiseAltFt { get; init; }

    /// <summary>Ground spoilers deployed — only readable via debug.groundSpoilersDeployd
    /// (the trailing typo is ProSim's, not ours).</summary>
    public bool GroundSpoilersDeployed { get; init; }

    /// <summary>Both thrust levers in the max-reverse detent
    /// (S_FC_THROTTLE_{LEFT,RIGHT}_MAX_REVERSE) — the "reverse green" trigger.</summary>
    public bool ReversersMaxBoth { get; init; }

    // ---- Flow-monitor fields. RadioAltitudeFt saturates at the radio altimeter ceiling;
    //      this AGL (aircraft.altitude.aboveGround) does not, so flow thresholds use it. ----

    /// <summary>Non-saturating height above ground, ft.</summary>
    public double AltitudeAglFt { get; init; }

    /// <summary>Either landing light on (S_OH_EXT_LT_LANDING_L/R).</summary>
    public bool AnyLandingLightOn { get; init; }

    /// <summary>Seatbelt signs selector (S_OH_SIGNS): 0=Auto 1=On 2=Off.</summary>
    public int SeatbeltSignsMode { get; init; }

    /// <summary>Beacon switch on (S_OH_EXT_LT_BEACON).</summary>
    public bool BeaconOn { get; init; }

    /// <summary>Speedbrake lever armed (S_FC_SPEEDBRAKE_ARMED).</summary>
    public bool SpeedbrakeArmed { get; init; }

    /// <summary>Transponder mode (S_XPDR_MODE): 0=Standby 1=TA 2=TA/RA.</summary>
    public int XpdrMode { get; init; }

    // Benign weather defaults — an unconstructed/unread sample must never look like icing
    // conditions (TAT 0 °C in 0 m visibility). The live source overwrites all three.

    /// <summary>Total air temperature, °C.</summary>
    public double TatC { get; init; } = 15;

    /// <summary>Outside air temperature, °C.</summary>
    public double OatC { get; init; } = 15;

    /// <summary>Aircraft is inside cloud (environment.ambientInCloud).</summary>
    public bool InCloud { get; init; }

    /// <summary>Ambient visibility, metres (environment.ambientVisibility).</summary>
    public double VisibilityM { get; init; } = double.MaxValue;

    /// <summary>Engine anti-ice switches (S_OH_PNEUMATIC_ENG{1,2}_ANTI_ICE).</summary>
    public bool EngineAntiIce1On { get; init; }
    public bool EngineAntiIce2On { get; init; }

    /// <summary>Wing anti-ice switch (S_OH_PNEUMATIC_WING_ANTI_ICE).</summary>
    public bool WingAntiIceOn { get; init; }

    /// <summary>Either aircraft.engines.N.running LITERAL boolean — can disagree with the
    /// state-string-derived engine state during a start; the beacon flow check deliberately
    /// uses this so a spooling engine counts as running. Since 2026-08-16 (issue #59) the
    /// live source also ORs this into <see cref="AnyEngineRunning"/>, so a transient
    /// unexpected state string (which maps to Off) cannot read as both-engines-off mid-taxi.</summary>
    public bool AnyEngineRunningRaw { get; init; }
}

/// <summary>
/// Supplies <see cref="FlightDataSnapshot"/>s to the flight state engine. Implementations:
/// the live ProSim dataref source; later a replay source over the JSONL event log.
/// </summary>
public interface IFlightDataSource
{
    /// <summary>Returns the current sample. Must be cheap (cached reads, never a network call).</summary>
    FlightDataSnapshot Sample();
}
