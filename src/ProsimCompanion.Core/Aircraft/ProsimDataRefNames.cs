namespace ProsimCompanion.Core.Aircraft;

/// <summary>
/// Canonical catalog of ProSim SDK datarefs. Every ref the app SUBSCRIBES is a typed
/// <see cref="DataRef{T}"/> descriptor declaring wire name, tier and fallback policy in one
/// place (#83); names used only as write/press targets, or kept as wire vocabulary, remain
/// plain string constants. The full reference is <c>Prosim_A322_Dataref.csv</c> at the repo
/// root; a guard test checks every declared wire against it.
/// <para>
/// The string values are wire-protocol identifiers consumed verbatim by the ProSim SDK
/// and SimConnect. They must NEVER be "corrected" — several are deliberately misspelled
/// upstream (marked <c>// sic</c>) and a typo here breaks integration silently.
/// </para>
/// <para>
/// Polling cadence used by ProsimInterface (for reference): 100 ms for safety-critical
/// flight parameters, 250 ms for actively monitored systems (fuel amounts, switches,
/// doors, ground equipment, ACP audio), 500 ms for operational data (pax/cargo, lights,
/// hydraulics, batteries), 2000 ms for configuration and capacities.
/// </para>
/// </summary>
public static class ProsimDataRefNames
{
    #region FlightDynamics

    /// <summary>Also the flight-data validity sentinel — consumers probe RawValue/IsStale.</summary>
    public static readonly DataRef<double> IndicatedAirspeed = new("aircraft.speed.ias", DataRefTier.Critical, 0.0);
    public static readonly DataRef<double> Altitude = new("aircraft.altitude", DataRefTier.Critical, 0.0);
    public static readonly DataRef<double> RadioAltitude = new("aircraft.altitude.radio", DataRefTier.Critical, 0.0);
    /// <summary>Non-saturating AGL — deliberately distinct from <see cref="RadioAltitude"/>,
    /// which tops out like the real RA; keep both.</summary>
    public static readonly DataRef<double> AltitudeAboveGround = new("aircraft.altitude.aboveGround", DataRefTier.Normal, 0.0);
    public static readonly DataRef<double> VerticalSpeed = new("aircraft.verticalspeed", DataRefTier.Critical, 0.0);
    public static readonly DataRef<double> GroundSpeed = new("aircraft.speed.ground", DataRefTier.Frequent, 0.0);
    /// <summary>On-ground gate. Fallback true: with no data we assume parked, never airborne —
    /// ground-only automation stays quiet either way, air-only callouts cannot fire on a cold start.</summary>
    public static readonly DataRef<bool> OnGroundGate = new("system.gates.B_GROUND", DataRefTier.Frequent, true);

    public static readonly DataRef<bool> EngineRunning1 = new("aircraft.engines.1.running", DataRefTier.Normal, false);
    public static readonly DataRef<bool> EngineRunning2 = new("aircraft.engines.2.running", DataRefTier.Normal, false);
    /// <summary>Engine state string (issue #59: transiently misreports during start — consumers
    /// corroborate with N1/running before acting).</summary>
    public static readonly DataRef<string> Engine1State = new("aircraft.systems.engines.1.state", DataRefTier.Normal, "");
    public static readonly DataRef<string> Engine2State = new("aircraft.systems.engines.2.state", DataRefTier.Normal, "");
    /// <summary>N1 in percent. A distinct <c>aircraft.engine1.raw</c> ("Engine 1 N1 raw") ref
    /// exists in ProSim and once shared the symbol name Engine1N1 with this one (#83); it is
    /// deliberately NOT declared — every consumer audited in 2026-08 wants percent semantics.</summary>
    public static readonly DataRef<double> Engine1N1Percent = new("aircraft.engines.1.n1", DataRefTier.Frequent, 0.0);
    public static readonly DataRef<double> Engine2N1Percent = new("aircraft.engines.2.n1", DataRefTier.Frequent, 0.0);
    /// <summary>Fallback 0 is load-bearing: 0 means "limit not set", which disables FLEX/TOGA
    /// threshold callouts instead of firing them against a fictitious limit.</summary>
    public static readonly DataRef<double> EnginesLimitFlex = new("aircraft.engines.limits.flex", DataRefTier.Infrequent, 0.0);
    public static readonly DataRef<double> EnginesLimitToga = new("aircraft.engines.limits.toga", DataRefTier.Infrequent, 0.0);
    public static readonly DataRef<int> ThrottleLeftMaxReverse = new("system.switches.S_FC_THROTTLE_LEFT_MAX_REVERSE", DataRefTier.Frequent, 0);
    public static readonly DataRef<int> ThrottleRightMaxReverse = new("system.switches.S_FC_THROTTLE_RIGHT_MAX_REVERSE", DataRefTier.Frequent, 0);
    public static readonly DataRef<double> Fac1Vls = new("aircraft.FAC1.VLS", DataRefTier.Frequent, 0.0);
    public static readonly DataRef<bool> GroundSpoilersDeployed = new("debug.groundSpoilersDeployd", DataRefTier.Frequent, false); // sic — ProSim's typo, never correct
    /// <summary>Fallback true: no data reads as "gear is down", so gear-up warnings stay silent
    /// rather than crying wolf on a dead link.</summary>
    public static readonly DataRef<bool> GearDown = new("aircraft.gearDown", DataRefTier.Normal, true); // also referenced by lg-gear-not-downlocked.json
    public static readonly DataRef<bool> ElecBusPowerDcBat = new("system.gates.B_ELEC_BUS_POWER_DC_BAT", DataRefTier.Normal, false);
    /// <summary>Fallback 15 °C: a plausible temperate default so anti-icing advisories never
    /// false-positive off a generic 0 °C when the ref is dead.</summary>
    public static readonly DataRef<double> TemperatureTat = new("aircraft.temperature.tat", DataRefTier.Infrequent, 15.0);
    public static readonly DataRef<double> TemperatureOat = new("aircraft.temperature.oat", DataRefTier.Infrequent, 15.0);
    public static readonly DataRef<bool> AmbientInCloud = new("environment.ambientInCloud", DataRefTier.Infrequent, false);
    /// <summary>Fallback MaxValue = unlimited visibility; a generic 0 would read as dense fog.</summary>
    public static readonly DataRef<double> AmbientVisibility = new("environment.ambientVisibility", DataRefTier.Infrequent, double.MaxValue);

    // Wire vocabulary only (no subscriber today) — kept as reference names.
    public const string GroundContact = "aircraft.ground";
    public const string BankAngle = "aircraft.bank";
    public const string PitchAngle = "aircraft.pitch";
    public const string HeadingMagnetic = "aircraft.heading.magnetic";
    public const string HeadingTrue = "aircraft.heading.true";
    public const string AircraftTime = "aircraft.time";

    #endregion

    #region Fuel

    public static readonly DataRef<double> FuelTotal = new("aircraft.fuel.total.amount.kg", DataRefTier.Normal, 0.0);
    public static readonly DataRef<double> FuelLeft = new("aircraft.fuel.left.amount.kg", DataRefTier.Normal, 0.0);
    public static readonly DataRef<double> FuelRight = new("aircraft.fuel.right.amount.kg", DataRefTier.Normal, 0.0);
    public static readonly DataRef<double> FuelCenter = new("aircraft.fuel.center.amount.kg", DataRefTier.Normal, 0.0);
    public const string FuelTotalCapacity = "aircraft.fuel.total.capacity";
    public const string FuelAct1 = "aircraft.fuel.ACT1.amount.kg";
    public const string FuelAct2 = "aircraft.fuel.ACT2.amount.kg";
    public const string FuelLeftCapacity = "aircraft.fuel.left.capacity";
    public const string FuelRightCapacity = "aircraft.fuel.right.capacity";
    public static readonly DataRef<double> FuelCenterCapacity = new("aircraft.fuel.center.capacity", DataRefTier.Infrequent, 0.0);

    // Per-tank A320 breakdown — `aircraft.fuel.left.amount.kg` is the WING aggregate
    // (inner + outer). The granular `aircraft.systems.fuel.*` refs expose each
    // individual tank, matching the 5-tank picture ProSim's own FUEL EFB page shows.
    // Amounts move during refuel (poll at the same cadence as the wing aggregates);
    // capacities are airframe-fixed, slow polling is plenty.
    public static readonly DataRef<double> FuelLeftInner = new("aircraft.systems.fuel.left.inner.amount.kg", DataRefTier.Normal, 0.0);
    public static readonly DataRef<double> FuelLeftInnerCapacity = new("aircraft.systems.fuel.left.inner.capacity", DataRefTier.Infrequent, 0.0);
    public static readonly DataRef<double> FuelLeftOuter = new("aircraft.systems.fuel.left.outer.amount.kg", DataRefTier.Normal, 0.0);
    public static readonly DataRef<double> FuelLeftOuterCapacity = new("aircraft.systems.fuel.left.outer.capacity", DataRefTier.Infrequent, 0.0);
    public static readonly DataRef<double> FuelRightInner = new("aircraft.systems.fuel.right.inner.amount.kg", DataRefTier.Normal, 0.0);
    public static readonly DataRef<double> FuelRightInnerCapacity = new("aircraft.systems.fuel.right.inner.capacity", DataRefTier.Infrequent, 0.0);
    public static readonly DataRef<double> FuelRightOuter = new("aircraft.systems.fuel.right.outer.amount.kg", DataRefTier.Normal, 0.0);
    public static readonly DataRef<double> FuelRightOuterCapacity = new("aircraft.systems.fuel.right.outer.capacity", DataRefTier.Infrequent, 0.0);

    #endregion

    #region Refuel

    /// <summary>Refuel target in ProSim's configured unit — unit-ambiguous by itself;
    /// consumers pair it with <see cref="RefuelFuelTargetKg"/> and the unit config.</summary>
    public static readonly DataRef<double> RefuelFuelTarget = new("aircraft.refuel.fuelTarget", DataRefTier.Infrequent, 0.0);
    /// <summary>Preferred kg-denominated refuel target. NOT present in Prosim_A322_Dataref.csv
    /// (sole CSV exception, #83) — live verification pending; if it never pushes, consumers'
    /// fallback path via <see cref="RefuelFuelTarget"/> is what has actually been running.</summary>
    public static readonly DataRef<double> RefuelFuelTargetKg = new("aircraft.refuel.fuelTarget.kg", DataRefTier.Infrequent, 0.0);
    public const string RefuelActive = "aircraft.refuel.refuelingActive";
    public const string RefuelPower = "aircraft.refuel.refuelingPower";
    public const string RefuelRate = "aircraft.refuel.refuelingRate";

    #endregion

    #region WeightAndBalance

    public static readonly DataRef<double> WeightGross = new("aircraft.weight.gross", DataRefTier.Normal, 0.0);
    /// <summary>Fallback 0 is load-bearing: consumers treat &lt;= 0 as "unknown, fall through
    /// to the OFP value" — never substitute a plausible airframe number here.</summary>
    public static readonly DataRef<double> WeightGrossMax = new("aircraft.weight.grossMax", DataRefTier.Infrequent, 0.0);
    public static readonly DataRef<double> WeightZfw = new("aircraft.weight.zfw", DataRefTier.Normal, 0.0);
    public static readonly DataRef<double> WeightZfwMax = new("aircraft.weight.zfwMax", DataRefTier.Infrequent, 0.0);
    /// <summary>Readable equivalent of the write-only FMS INIT ZFWCG; drives MACZFW/MACGW.</summary>
    public static readonly DataRef<double> Zfwcg = new("aircraft.zfwcg", DataRefTier.Normal, 0.0);
    public static readonly DataRef<double> CenterOfGravity = new("aircraft.cg", DataRefTier.Normal, 0.0);
    public const string WeightFuel = "aircraft.weight.fuel";
    public const string BalanceMac = "aircraft.balance.MAC";

    #endregion

    #region Passengers

    /// <summary>Zone capacities. Fallback 0 is load-bearing: a zero capacity SUM triggers the
    /// [24,30,36,42] default-cabin substitution downstream — keep 0, never a plausible capacity.</summary>
    public static readonly DataRef<int> PaxZone1Capacity = new("aircraft.passengers.zone1.capacity", DataRefTier.Infrequent, 0);
    public static readonly DataRef<int> PaxZone2Capacity = new("aircraft.passengers.zone2.capacity", DataRefTier.Infrequent, 0);
    public static readonly DataRef<int> PaxZone3Capacity = new("aircraft.passengers.zone3.capacity", DataRefTier.Infrequent, 0);
    public static readonly DataRef<int> PaxZone4Capacity = new("aircraft.passengers.zone4.capacity", DataRefTier.Infrequent, 0);
    public static readonly DataRef<int> PaxZone1Amount = new("aircraft.passengers.zone1.amount", DataRefTier.Normal, 0);
    public static readonly DataRef<int> PaxZone2Amount = new("aircraft.passengers.zone2.amount", DataRefTier.Normal, 0);
    public static readonly DataRef<int> PaxZone3Amount = new("aircraft.passengers.zone3.amount", DataRefTier.Normal, 0);
    public static readonly DataRef<int> PaxZone4Amount = new("aircraft.passengers.zone4.amount", DataRefTier.Normal, 0);
    /// <summary>Null fallback (not ""): consumers use null-vs-value as the "seat map has
    /// actually arrived" liveness probe. Normal tier for the web seat-map's boarding
    /// responsiveness (fastest-requested rule; the GSX consumers only need Infrequent).</summary>
    public static readonly DataRef<string?> PaxSeatOccupationString = new("aircraft.passengers.seatOccupation.string", DataRefTier.Normal, null);
    /// <summary>Booked-passenger string (EFB namespace; also used as the pax-booked readout).</summary>
    public static readonly DataRef<string?> PaxBookedString = new("efb.passengers.booked.string", DataRefTier.Infrequent, null);
    public const string PaxTotalWeight = "aircraft.passengers.total.weight";
    public const string PaxSeatOccupation = "aircraft.passengers.seatOccupation";

    #endregion

    #region Cargo

    public static readonly DataRef<double> CargoForwardAmount = new("aircraft.cargo.forward.amount", DataRefTier.Normal, 0.0);
    public static readonly DataRef<double> CargoForwardCapacity = new("aircraft.cargo.forward.capacity", DataRefTier.Infrequent, 0.0);
    public static readonly DataRef<double> CargoAftAmount = new("aircraft.cargo.aft.amount", DataRefTier.Normal, 0.0);
    public static readonly DataRef<double> CargoAftCapacity = new("aircraft.cargo.aft.capacity", DataRefTier.Infrequent, 0.0);
    public static readonly DataRef<double> CargoBulkCapacity = new("aircraft.cargo.bulk.capacity", DataRefTier.Infrequent, 0.0);
    public const string CargoBulkAmount = "aircraft.cargo.bulk.amount";

    #endregion

    #region Doors

    public static readonly DataRef<bool> Door1L = new("doors.entry.left.fwd", DataRefTier.Normal, false);
    public static readonly DataRef<bool> Door4L = new("doors.entry.left.aft", DataRefTier.Normal, false);
    public static readonly DataRef<bool> Door1R = new("doors.entry.right.fwd", DataRefTier.Normal, false);
    public static readonly DataRef<bool> Door4R = new("doors.entry.right.aft", DataRefTier.Normal, false);
    public static readonly DataRef<bool> DoorCargoForward = new("doors.cargo.forward", DataRefTier.Normal, false);
    public static readonly DataRef<bool> DoorCargoAft = new("doors.cargo.aft", DataRefTier.Normal, false);
    public static readonly DataRef<bool> DoorCargoBulk = new("doors.cargo.bulk", DataRefTier.Normal, false);
    public static readonly DataRef<bool> Door2L = new("doors.wing.left.1", DataRefTier.Normal, false);
    public static readonly DataRef<bool> Door3L = new("doors.wing.left.2", DataRefTier.Normal, false);
    public static readonly DataRef<bool> Door2R = new("doors.wing.right.1", DataRefTier.Normal, false);
    public static readonly DataRef<bool> Door3R = new("doors.wing.right.2", DataRefTier.Normal, false);
    public const string CockpitDoorSwitch = "system.switches.S_PED_COCKPIT_DOOR"; // [0:Normal, 1:Unlock, 2:Lock]
    public const string CockpitDoorState = "system.switches.S_DOORS_COCKPIT";
    public const string CockpitDoorIndicatorUpper = "system.indicators.I_PED_COCKPIT_DOOR_U";

    #endregion

    #region GroundServices

    public static readonly DataRef<bool> GroundPreconditionedAir = new("groundservice.preconditionedAir", DataRefTier.Infrequent, false);
    public static readonly DataRef<bool> GroundPower = new("groundservice.groundpower", DataRefTier.Normal, false);
    public static readonly DataRef<bool> Chocks = new("efb.chocks", DataRefTier.Normal, false);
    public static readonly DataRef<int> PushbackState = new("groundservice.pushback", DataRefTier.Normal, 0);
    public const string GroundPneumatic = "groundservice.pneumatic";
    public const string PushbackWait = "groundservice.pushback.wait";

    #endregion

    #region Efb

    public static readonly DataRef<string?> EfbBoardingStatus = new("efb.efb.boardingStatus", DataRefTier.Infrequent, null); // double "efb." is the real published path
    public static readonly DataRef<double> EfbPlannedCargoKg = new("efb.plannedCargoKg", DataRefTier.Infrequent, 0.0);
    public static readonly DataRef<double> EfbPlannedFuel = new("efb.plannedfuel", DataRefTier.Infrequent, 0.0);
    public static readonly DataRef<string?> EfbFinalLoadsheet = new("efb.finalLoadsheet", DataRefTier.Infrequent, null);
    public static readonly DataRef<string?> EfbSimbriefId = new("efb.simbrief.id", DataRefTier.Infrequent, null);
    /// <summary>Issue #60: can hold a stale true from a PREVIOUS session's import — consumers
    /// corroborate with FMS origin/destination before trusting it after startup.</summary>
    public static readonly DataRef<bool> EfbSimbriefPlanImported = new("efb.simbriefPlanImported", DataRefTier.Infrequent, false);
    /// <summary>CIDS flight number (System.String per the A322 CSV) — drives the web header's
    /// FLT NO split-flap. Fallback "" renders blank flaps until real data arrives.</summary>
    public static readonly DataRef<string> CidsFlightNumber = new("efb.cids.flightNumber", DataRefTier.Infrequent, "");
    public const string EfbLoaded = "efb.loaded";
    public const string EfbReady = "efb.ready";
    public const string EfbTest = "efb.test";
    public const string EfbPrelimLoadsheet = "efb.prelimLoadsheet";
    public const string EfbFlightTimestampJson = "efb.flightTimestampJSON";
    public const string EfbPaxBooked = "efb.passengers.booked";
    public const string EfbPassengerStatistics = "efb.passengerStatistics";
    public const string EfbFwdStairs = "efb.fwdStairs";
    public const string EfbAftStairs = "efb.aftStairs";
    public const string GsxAutoCatering = "efb.gsx.autoCatering";
    public const string GsxAutoPushback = "efb.gsx.autoPushback";
    public const string GsxAutoDisconnectGpu = "efb.gsx.autoDisconnectGpu";
    public const string GsxAutoConnectGpu = "efb.gsx.autoConnectGpu";
    public const string GsxAutoDeboard = "efb.gsx.autoDeboard";
    public const string GsxAutoSelectOperator = "efb.gsx.autoSelectOperator";
    public const string EfbAutoJetway = "efb.autoJetway";
    public const string EfbAutoDoor = "efb.autoDoor";

    #endregion

    #region Acars

    // ProSim's AOC messaging pipeline. Writing a JSON-serialized AcarsMessage to
    // AocMessageUplink hands the message to ProSim's internal ACARS dispatch
    // (Hoppie / SayIntentions / whichever provider is configured in ProSim's
    // config.xml). The cockpit's ATSU then displays it under MCDU -> ATSU ->
    // AOC MENU -> RCVD MSGS. The .copy ref echoes the last accepted payload;
    // .result reports the parser's status string ("Message processed" on success,
    // parse-error text otherwise).
    public const string AocMessageUplink = "efb.aoc.message.uplink";
    public const string AocMessageUplinkCopy = "efb.aoc.message.uplink.copy";
    public const string AocMessageUplinkResult = "efb.aoc.message.uplink.result";
    public const string AocMessageDownlink = "efb.aoc.message.downlink";
    public const string AocMessageDownlinkAll = "efb.aoc.message.downlink.all";

    #endregion

    #region Fms

    /// <summary>Null until real data; ProSim publishes "----"/"Null" sentinels for "no airport
    /// entered" — validate with the ICAO check beside these descriptors, not ad hoc.</summary>
    public static readonly DataRef<string?> FmsOrigin = new("aircraft.fms.origin", DataRefTier.Infrequent, null);
    public static readonly DataRef<string?> FmsDestination = new("aircraft.fms.destination", DataRefTier.Infrequent, null);
    public static readonly DataRef<string?> FmsFlightPlanXml = new("aircraft.fms.flightPlanXml", DataRefTier.Infrequent, null);
    public const string FmsAlternate = "aircraft.fms.alternate";
    public const string FmsCruiseAlt = "aircraft.fms.cruiseAlt";
    public const string FmsFlightPhase = "aircraft.fms.flightPhase";
    public const string FmsRoute = "aircraft.fms.route";
    public const string FmsTimeToDest = "aircraft.fms.TimeToDest";

    // FMS Performance. V-speeds/flex fall back to 0 = "not entered yet" — spoken tokens and
    // briefings treat 0 as absent. The shift/THS/flaps refs are write targets, not reads.
    public static readonly DataRef<int> FmsPerfTakeoffFlexTemp = new("aircraft.fms.perf.takeOff.flexTemp", DataRefTier.Infrequent, 0);
    public static readonly DataRef<int> FmsPerfTakeoffV1 = new("aircraft.fms.perf.takeOff.v1", DataRefTier.Infrequent, 0);
    public static readonly DataRef<int> FmsPerfTakeoffVr = new("aircraft.fms.perf.takeOff.vr", DataRefTier.Infrequent, 0);
    public static readonly DataRef<int> FmsPerfTakeoffV2 = new("aircraft.fms.perf.takeOff.v2", DataRefTier.Infrequent, 0);
    public const string FmsPerfTakeoffFlaps = "aircraft.fms.perf.takeOff.flaps";
    public const string FmsPerfTakeoffThs = "aircraft.fms.perf.takeOff.ths";
    public const string FmsPerfTakeoffShift = "aircraft.fms.perf.takeOff.shift";
    public const string FmsPerfLandingFlaps = "aircraft.fms.perf.landing.flaps";

    // FMS Init
    public const string FmsInitBlock = "aircraft.fms.init.block";
    public const string FmsInitZfw = "aircraft.fms.init.zfw";
    public const string FmsInitZfwcg = "aircraft.fms.init.zfwcg";

    /// <summary>
    /// Engine type from the user's selected aircraft profile; drives takeoff V-speed
    /// lookup tables. Declared values: "CFM" | "IAE" | "CFM-Leap" (treat Leap as "CFM"
    /// on the wire — only two lookup buckets exist).
    /// Fallback "CFM" is a semantic default (like <see cref="ConfigTakeoffShiftUnit"/>):
    /// with no data the V-speed lookup uses the CFM bucket, which is also where the
    /// consumer maps every non-"IAE" value.
    /// </summary>
    public static readonly DataRef<string> ConfigEngineType = new("system.config.Config.EPR", DataRefTier.Infrequent, "CFM");

    /// <summary>ProSim's configured weight unit — "LBS" for pounds, otherwise kilograms
    /// (predecessor's empirically-established rule; drives the "aircraft" unit source).
    /// Null fallback: consumers must distinguish "not read yet" from a real unit.</summary>
    public static readonly DataRef<string?> ConfigWeightUnit = new("system.config.Units.Weight", DataRefTier.Infrequent, null);

    /// <summary>
    /// Display-unit selector for the runway-shift value written to
    /// aircraft.fms.perf.takeOff.shift. Values: "Meters" | "Feet". Read this before
    /// composing the shift integer — ProSim interprets the number in this unit.
    /// Fallback "Feet" is ProSim's own default, a semantic default rather than a zero.
    /// </summary>
    public static readonly DataRef<string> ConfigTakeoffShiftUnit = new("system.config.Units.TakeoffShiftUnit", DataRefTier.Infrequent, "Feet");

    #endregion

    #region SystemState

    public static readonly DataRef<string?> AircraftTitle = new("simulator.aircraft.title", DataRefTier.Infrequent, null);
    public const string SimulatorConnected = "simulator.connected";
    // Sim clock feeds (issue #71). Declared DateTime/TimeSpan per the A322 CSV, but the
    // consumer (MainLayout's header clock) reads RawValue through SimClockFormat, which
    // accepts every shape the transport actually delivers — the typed fallbacks are the
    // "unpopulated" sentinels SimClockFormat already treats as no-data.
    public static readonly DataRef<DateTime> SimulatorTime = new("simulator.time", DataRefTier.Infrequent, default);
    public static readonly DataRef<TimeSpan> ZuluTime = new("simulator.zuluTime", DataRefTier.Infrequent, default);

    #endregion

    #region Audio

    public const string CommIfeInUse = "aircraft.communication.ifeInUse";
    public const string CommPaInUse = "aircraft.communication.paInUse";
    public const string CommVideoInUse = "aircraft.communication.videoInUse";
    /// <summary>Path PREFIX for per-window audio refs — append the window name.</summary>
    public const string AudioWindowsPrefix = "aircraft.communication.windows.";
    public const string RmpPower1Switch = "system.switches.S_PED_RMP1_POWER";
    public const string RmpPower2Switch = "system.switches.S_PED_RMP2_POWER";

    // INT/RAD source switches. Fallback 1 (Off): the repurposed smart button must read as
    // idle, never as a held INT/RAD position, when the ref is dead.
    public static readonly DataRef<int> IntRadCpt = new("system.switches.S_ASP_INTRAD", DataRefTier.Frequent, 1);  // [0:INT, 1:Off, 2:RAD]
    public static readonly DataRef<int> IntRadFo = new("system.switches.S_ASP2_INTRAD", DataRefTier.Frequent, 1); // [0:INT, 1:Off, 2:RAD]

    // Captain ACP transmit selection (issue #72). The interphone transmit gate reads THESE,
    // never S_ASP_INTRAD above: the INT/RAD rocker is repurposed as the GSX "force next
    // service" smart button (GsxAutomationService), so gating dialogue on it would fire
    // ground services every time the pilot keyed the intercom.
    /// <summary>Resolved captain ACP transmit selector
    /// [0:None, 1:VHF1, 2:VHF2, 3:VHF3, 4:HF1, 5:HF2, 6:INT, 7:CAB, 8:PA].
    /// Fallback −1 is deliberately out of range → maps to AcpTransmitTarget.Unknown,
    /// so a dead ref can never read as a valid transmit selection.</summary>
    public static readonly DataRef<int> AcpSendChannel = new("system.switches.S_ASP_SEND_CHANNEL", DataRefTier.Frequent, -1);
    /// <summary>Captain ACP INT transmit key [0:Normal, 1:Pushed].</summary>
    public static readonly DataRef<int> AcpIntSend = new("system.switches.S_ASP_INT_SEND", DataRefTier.Frequent, 0);

    // ACP volume knobs and REC latches. Latch fallback 1 (unmuted / fail-audible, #83): a
    // dead subscription must never silently mute a channel; degraded-mode gating belongs on
    // RawValue/IsStale, not on reading the fallback (see AcpChannel).

    // ACP1 Volume Knobs
    public static readonly DataRef<double> Acp1CabAnalog = new("system.analog.A_ASP_CAB_VOLUME", DataRefTier.Frequent, 0.0);
    public static readonly DataRef<double> Acp1Hf1Analog = new("system.analog.A_ASP_HF_1_VOLUME", DataRefTier.Frequent, 0.0);
    public static readonly DataRef<double> Acp1Hf2Analog = new("system.analog.A_ASP_HF_2_VOLUME", DataRefTier.Frequent, 0.0);
    public static readonly DataRef<double> Acp1IntAnalog = new("system.analog.A_ASP_INT_VOLUME", DataRefTier.Frequent, 0.0);
    public static readonly DataRef<double> Acp1PaAnalog = new("system.analog.A_ASP_PA_VOLUME", DataRefTier.Frequent, 0.0);
    public static readonly DataRef<double> Acp1Vhf1Analog = new("system.analog.A_ASP_VHF_1_VOLUME", DataRefTier.Frequent, 0.0);
    public static readonly DataRef<double> Acp1Vhf2Analog = new("system.analog.A_ASP_VHF_2_VOLUME", DataRefTier.Frequent, 0.0);
    public static readonly DataRef<double> Acp1Vhf3Analog = new("system.analog.A_ASP_VHF_3_VOLUME", DataRefTier.Frequent, 0.0);

    // ACP1 Latch Switches
    public static readonly DataRef<int> Acp1CabLatch = new("system.switches.S_ASP_CAB_REC_LATCH", DataRefTier.Frequent, 1);
    public static readonly DataRef<int> Acp1Hf1Latch = new("system.switches.S_ASP_HF_1_REC_LATCH", DataRefTier.Frequent, 1);
    public static readonly DataRef<int> Acp1Hf2Latch = new("system.switches.S_ASP_HF_2_REC_LATCH", DataRefTier.Frequent, 1);
    public static readonly DataRef<int> Acp1IntLatch = new("system.switches.S_ASP_INT_REC_LATCH", DataRefTier.Frequent, 1);
    public static readonly DataRef<int> Acp1PaLatch = new("system.switches.S_ASP_PA_REC_LATCH", DataRefTier.Frequent, 1);
    public static readonly DataRef<int> Acp1Vhf1Latch = new("system.switches.S_ASP_VHF_1_REC_LATCH", DataRefTier.Frequent, 1);
    public static readonly DataRef<int> Acp1Vhf2Latch = new("system.switches.S_ASP_VHF_2_REC_LATCH", DataRefTier.Frequent, 1);
    public static readonly DataRef<int> Acp1Vhf3Latch = new("system.switches.S_ASP_VHF_3_REC_LATCH", DataRefTier.Frequent, 1);

    // ACP2 Volume Knobs
    public static readonly DataRef<double> Acp2CabAnalog = new("system.analog.A_ASP2_CAB_VOLUME", DataRefTier.Frequent, 0.0);
    public static readonly DataRef<double> Acp2Hf1Analog = new("system.analog.A_ASP2_HF_1_VOLUME", DataRefTier.Frequent, 0.0);
    public static readonly DataRef<double> Acp2Hf2Analog = new("system.analog.A_ASP2_HF_2_VOLUME", DataRefTier.Frequent, 0.0);
    public static readonly DataRef<double> Acp2IntAnalog = new("system.analog.A_ASP2_INT_VOLUME", DataRefTier.Frequent, 0.0);
    public static readonly DataRef<double> Acp2PaAnalog = new("system.analog.A_ASP2_PA_VOLUME", DataRefTier.Frequent, 0.0);
    public static readonly DataRef<double> Acp2Vhf1Analog = new("system.analog.A_ASP2_VHF_1_VOLUME", DataRefTier.Frequent, 0.0);
    public static readonly DataRef<double> Acp2Vhf2Analog = new("system.analog.A_ASP2_VHF_2_VOLUME", DataRefTier.Frequent, 0.0);
    public static readonly DataRef<double> Acp2Vhf3Analog = new("system.analog.A_ASP2_VHF_3_VOLUME", DataRefTier.Frequent, 0.0);

    // ACP2 Latch Switches
    public static readonly DataRef<int> Acp2CabLatch = new("system.switches.S_ASP2_CAB_REC_LATCH", DataRefTier.Frequent, 1);
    public static readonly DataRef<int> Acp2Hf1Latch = new("system.switches.S_ASP2_HF_1_REC_LATCH", DataRefTier.Frequent, 1);
    public static readonly DataRef<int> Acp2Hf2Latch = new("system.switches.S_ASP2_HF_2_REC_LATCH", DataRefTier.Frequent, 1);
    public static readonly DataRef<int> Acp2IntLatch = new("system.switches.S_ASP2_INT_REC_LATCH", DataRefTier.Frequent, 1);
    public static readonly DataRef<int> Acp2PaLatch = new("system.switches.S_ASP2_PA_REC_LATCH", DataRefTier.Frequent, 1);
    public static readonly DataRef<int> Acp2Vhf1Latch = new("system.switches.S_ASP2_VHF_1_REC_LATCH", DataRefTier.Frequent, 1);
    public static readonly DataRef<int> Acp2Vhf2Latch = new("system.switches.S_ASP2_VHF_2_REC_LATCH", DataRefTier.Frequent, 1);
    public static readonly DataRef<int> Acp2Vhf3Latch = new("system.switches.S_ASP2_VHF_3_REC_LATCH", DataRefTier.Frequent, 1);

    // ACP3 (Observer) Volume Knobs
    public static readonly DataRef<double> Acp3CabAnalog = new("system.analog.A_ASP3_CAB_VOLUME", DataRefTier.Frequent, 0.0);
    public static readonly DataRef<double> Acp3Hf1Analog = new("system.analog.A_ASP3_HF_1_VOLUME", DataRefTier.Frequent, 0.0);
    public static readonly DataRef<double> Acp3Hf2Analog = new("system.analog.A_ASP3_HF_2_VOLUME", DataRefTier.Frequent, 0.0);
    public static readonly DataRef<double> Acp3IntAnalog = new("system.analog.A_ASP3_INT_VOLUME", DataRefTier.Frequent, 0.0);
    public static readonly DataRef<double> Acp3PaAnalog = new("system.analog.A_ASP3_PA_VOLUME", DataRefTier.Frequent, 0.0);
    public static readonly DataRef<double> Acp3Vhf1Analog = new("system.analog.A_ASP3_VHF_1_VOLUME", DataRefTier.Frequent, 0.0);
    public static readonly DataRef<double> Acp3Vhf2Analog = new("system.analog.A_ASP3_VHF_2_VOLUME", DataRefTier.Frequent, 0.0);
    public static readonly DataRef<double> Acp3Vhf3Analog = new("system.analog.A_ASP3_VHF_3_VOLUME", DataRefTier.Frequent, 0.0);

    // ACP3 (Observer) Latch Switches
    public static readonly DataRef<int> Acp3CabLatch = new("system.switches.S_ASP3_CAB_REC_LATCH", DataRefTier.Frequent, 1);
    public static readonly DataRef<int> Acp3Hf1Latch = new("system.switches.S_ASP3_HF_1_REC_LATCH", DataRefTier.Frequent, 1);
    public static readonly DataRef<int> Acp3Hf2Latch = new("system.switches.S_ASP3_HF_2_REC_LATCH", DataRefTier.Frequent, 1);
    public static readonly DataRef<int> Acp3IntLatch = new("system.switches.S_ASP3_INT_REC_LATCH", DataRefTier.Frequent, 1);
    public static readonly DataRef<int> Acp3PaLatch = new("system.switches.S_ASP3_PA_REC_LATCH", DataRefTier.Frequent, 1);
    public static readonly DataRef<int> Acp3Vhf1Latch = new("system.switches.S_ASP3_VHF_1_REC_LATCH", DataRefTier.Frequent, 1);
    public static readonly DataRef<int> Acp3Vhf2Latch = new("system.switches.S_ASP3_VHF_2_REC_LATCH", DataRefTier.Frequent, 1);
    public static readonly DataRef<int> Acp3Vhf3Latch = new("system.switches.S_ASP3_VHF_3_REC_LATCH", DataRefTier.Frequent, 1);

    #endregion

    #region Hydraulics

    public const string HydBlueQuantity = "aircraft.hydraulics.blue.quantity";
    public const string HydGreenQuantity = "aircraft.hydraulics.green.quantity";
    public const string HydYellowQuantity = "aircraft.hydraulics.yellow.quantity";
    public const string HydBluePressure = "aircraft.hydraulics.blue.pressure";
    public const string HydGreenPressure = "aircraft.hydraulics.green.pressure";
    public const string HydYellowPressure = "aircraft.hydraulics.yellow.pressure";

    #endregion

    #region Electrical

    public const string Battery1Charge = "aircraft.electrical.battery1.charge";
    public const string Battery2Charge = "aircraft.electrical.battery2.charge";
    public const string Battery1Voltage = "aircraft.electrical.battery1.voltage";
    public const string Battery2Voltage = "aircraft.electrical.battery2.voltage";

    #endregion

    #region Control

    public const string ControlSlides = "control.slides";
    public const string ControlTowingPin = "control.towingPin";
    public const string CabinReady = "simulator.cabinReady";

    #endregion

    #region DisplayBrightness

    public const string DisplayBrightnessFo = "system.numerical.N_DISPLAY_BRIGHTNESS_FO";
    public const string DisplayBrightnessFi = "system.numerical.N_DISPLAY_BRIGHTNESS_FI";

    #endregion

    #region FlightControls

    public const string FcFlaps = "system.switches.S_FC_FLAPS";
    public const string FcSpeedbrake = "system.analog.A_FC_SPEEDBRAKE"; // analog
    public static readonly DataRef<int> FcSpeedbrakeArmed = new("system.switches.S_FC_SPEEDBRAKE_ARMED", DataRefTier.Normal, 0);

    /// <summary>Elevator trim in degrees. Warning: Fenix encodes the LVAR as value * 1000.</summary>
    public const string FcElevatorTrim = "aircraft.flightControls.trim.elevator";
    public const string FcElevatorTrimNum = "system.numerical.N_FC_ELEVATOR_TRIM"; // numerical read-back

    public const string FcRudderTrim = "system.numerical.N_FC_RUDDER_TRIM";
    public const string FcRudderTrimReset = "system.switches.S_FC_RUDDER_TRIM_RESET";

    // Sidesticks — numerical inputs (FO command direction only)
    public const string SidestickFoPitch = "system.numerical.N_FC_SIDESTICK_FO_PITCH";
    public const string SidestickFoBank = "system.numerical.N_FC_SIDESTICK_FO_BANK";

    // Flight control surface output datarefs — read-only, actual surface positions
    // after FBW mixing.
    // Elevator: -1 (full up) .. 0 (neutral) .. +1 (full down)    (normalized; sign inverted for FS2Crew LVAR mirror)
    // Aileron:  -1 (full left) .. 0 (neutral) .. +1 (full right) (normalized)
    // Rudder:   -25 deg (full left) .. 0 .. +25 deg (full right) (degrees)
    public const string FcSurfaceElevator = "aircraft.flightControls.elevator";
    public const string FcSurfaceAileron = "aircraft.flightControls.aileron";
    public const string FcSurfaceRudder = "aircraft.flightControls.flightControlSurfaces.Rudder";

    // FO analog input datarefs — writable Int32 inputs that command ProSim's FCS.
    // Fallback 512 is the raw neutral: a dead axis must read centered, never deflected.
    // Authored FO-side; PilotSeatMap swaps to A_FC_CAPT_* inside Subscribe when seated right.
    public static readonly DataRef<int> AnalogFoPitch = new("system.analog.A_FC_FO_PITCH", DataRefTier.Normal, 512);
    public static readonly DataRef<int> AnalogFoRoll = new("system.analog.A_FC_FO_ROLL", DataRefTier.Normal, 512);
    public static readonly DataRef<int> AnalogFoRudder = new("system.analog.A_FC_FO_RUDDER", DataRefTier.Normal, 512);

    public const string FcRudderNum = "system.numerical.N_FC_RUDDER";

    public const string Throttle1Input = "system.numerical.N_FC_THROTTLE1";
    public const string Throttle2Input = "system.numerical.N_FC_THROTTLE2";

    /// <summary>Throttle lever position (Double, normalised 0..1+; idle ~ 0).</summary>
    public const string Throttle1Lever = "aircraft.flightControls.throttle.1.lever";
    /// <summary>Throttle lever position (Double, normalised 0..1+; idle ~ 0).</summary>
    public const string Throttle2Lever = "aircraft.flightControls.throttle.2.lever";

    // ADIRS / IRS — position-available booleans for the three ADIRUs
    // (the cockpit IR mode switches are OhNavIr1Mode etc. in MiscOverhead).
    public const string Adiru1PositionAvailable = "aircraft.adiru.1.position_available";
    public const string Adiru2PositionAvailable = "aircraft.adiru.2.position_available";
    public const string Adiru3PositionAvailable = "aircraft.adiru.3.position_available";

    /// <summary>
    /// Flap handle index (Int32: 0=Up, 1=F1, 2=F1+F, 3=F2, 4=F3, 5=F4) — distinct from
    /// <see cref="FcFlaps"/>, which is the cockpit switch dataref.
    /// </summary>
    public static readonly DataRef<int> FlapPositionHandle = new("aircraft.flap.positionHandle", DataRefTier.Normal, 0);

    #endregion

    #region LandingGear

    public const string MipGear = "system.switches.S_MIP_GEAR";

    #endregion

    #region ParkingBrake

    /// <summary>Read the cockpit handle for brake status, preferred over the B_HYD gate
    /// (observed inverted on A322 — see <see cref="HydParkingBrakeSet"/>).</summary>
    public static readonly DataRef<int> MipParkingBrake = new("system.switches.S_MIP_PARKING_BRAKE", DataRefTier.Normal, 0);
    /// <summary>
    /// Actual hydraulic parking-brake state gate. Note: read the cockpit handle
    /// (<see cref="MipParkingBrake"/>) for brake status — this gate was observed
    /// inverted on A322.
    /// </summary>
    public const string HydParkingBrakeSet = "system.gates.B_HYD_PARKING_BRAKE_SET";

    #endregion

    #region Engines

    public const string EngMaster1 = "system.switches.S_ENG_MASTER_1";
    public const string EngMaster2 = "system.switches.S_ENG_MASTER_2";
    public const string EngMode = "system.switches.S_ENG_MODE"; // 0=Crank 1=Norm 2=Start

    #endregion

    #region Apu

    public const string OhElecApuMaster = "system.switches.S_OH_ELEC_APU_MASTER";
    public const string OhElecApuStart = "system.switches.S_OH_ELEC_APU_START";
    public const string OhPneumaticApuBleed = "system.switches.S_OH_PNEUMATIC_APU_BLEED";
    public const string ApuAvailable = "system.gates.B_ELEC_POWERUP";
    public static readonly DataRef<bool> ApuRunning = new("system.gates.B_APU_RUNNING", DataRefTier.Normal, false);
    public const string OhElecApuGenerator = "system.switches.S_OH_ELEC_APU_GENERATOR";
    public const string ApuBleedValve = "aircraft.systems.pneumatic.valve.BLEED_VALVE";
    public const string ApuBleedIndicatorUpper = "system.indicators.I_OH_PNEUMATIC_APU_BLEED_U";
    public const string ApuBleedIndicatorLower = "system.indicators.I_OH_PNEUMATIC_APU_BLEED_L";

    #endregion

    #region ElectricalOverhead

    public const string OhElecBat1 = "system.switches.S_OH_ELEC_BAT1";
    public const string OhElecBat2 = "system.switches.S_OH_ELEC_BAT2";
    public const string OhElecExtPwr = "system.switches.S_OH_ELEC_EXT_PWR";
    public const string OhElecGen1 = "system.switches.S_OH_ELEC_GEN1";
    public const string OhElecGen2 = "system.switches.S_OH_ELEC_GEN2";
    public const string OhProbeHeat = "system.switches.S_OH_PROBE_HEAT";

    // Indicator-LED datarefs for the EXT PWR pushbutton. Lower LED is the steady-state
    // "ON" lamp (lit while external power is feeding the bus); upper LED is "AVAILABLE"
    // (lit while GPU truck is plugged in). Poll these instead of the momentary cockpit
    // pushbutton (S_OH_ELEC_EXT_PWR), which is unreliable to poll.
    public const string ExtPwrIndicatorLower = "system.indicators.I_OH_ELEC_EXT_PWR_L";
    public const string ExtPwrIndicatorUpper = "system.indicators.I_OH_ELEC_EXT_PWR_U";

    // system.gates.B_* refs are booleans by ProSim convention — declared bool (#83).
    public static readonly DataRef<bool> ElecExternalConnect = new("system.gates.B_ELEC_EXTERNAL_CONNECT", DataRefTier.Normal, false); // AC external connect
    public static readonly DataRef<bool> ElecBusPowerDcEss = new("system.gates.B_ELEC_BUS_POWER_DC_ESS", DataRefTier.Frequent, false);
    public static readonly DataRef<bool> ElecBusPowerAcEss = new("system.gates.B_ELEC_BUS_POWER_AC_ESS", DataRefTier.Frequent, false);
    public static readonly DataRef<bool> ElecBusPowerDc1 = new("system.gates.B_ELEC_BUS_POWER_DC_1", DataRefTier.Frequent, false);
    public const string ElecBatterySwitch1 = "system.gates.B_ELEC_BATTERY_SWITCH_1";

    /// <summary>
    /// Audio switching selector — 0:CAPT (capt swapped to ACP3), 1:NORM,
    /// 2:F/O (FO swapped to ACP3). Drives the per-ACP power gate.
    /// Fallback 1 (NORM) is deliberate: no data means the normal ACP layout.
    /// </summary>
    public static readonly DataRef<int> AudioSwitching = new("system.switches.S_AUDIO_SWITCHING", DataRefTier.Frequent, 1);

    #endregion

    #region Pneumatics

    public const string OhPneumaticPack1 = "system.switches.S_OH_PNEUMATIC_PACK_1";
    public const string OhPneumaticPack2 = "system.switches.S_OH_PNEUMATIC_PACK_2";
    public static readonly DataRef<int> OhPneumaticEng1AntiIce = new("system.switches.S_OH_PNEUMATIC_ENG1_ANTI_ICE", DataRefTier.Normal, 0);
    public static readonly DataRef<int> OhPneumaticEng2AntiIce = new("system.switches.S_OH_PNEUMATIC_ENG2_ANTI_ICE", DataRefTier.Normal, 0);
    public static readonly DataRef<int> OhPneumaticWingAntiIce = new("system.switches.S_OH_PNEUMATIC_WING_ANTI_ICE", DataRefTier.Normal, 0);
    public const string OhPneumaticXbleedSelector = "system.switches.S_OH_PNEUMATIC_XBLEED_SELECTOR"; // 0=Shut 1=Auto 2=Open

    // Pneumatic Indicators
    public const string PneumaticPack1Indicator = "system.indicators.I_OH_PNEUMATIC_PACK_1_L";
    public const string PneumaticPack2Indicator = "system.indicators.I_OH_PNEUMATIC_PACK_2_L";
    public const string WingAntiIceIndicator = "system.indicators.I_OH_PNEUMATIC_WING_ANTI_ICE_L";
    public const string Eng1AntiIceIndicator = "system.indicators.I_OH_PNEUMATIC_ENG1_ANTI_ICE_L";
    public const string Eng2AntiIceIndicator = "system.indicators.I_OH_PNEUMATIC_ENG2_ANTI_ICE_L";

    #endregion

    #region FuelPumps

    public const string OhFuelLeft1 = "system.switches.S_OH_FUEL_LEFT_1";
    public const string OhFuelLeft2 = "system.switches.S_OH_FUEL_LEFT_2";
    public const string OhFuelRight1 = "system.switches.S_OH_FUEL_RIGHT_1";
    public const string OhFuelRight2 = "system.switches.S_OH_FUEL_RIGHT_2";
    public const string OhFuelCenter1 = "system.switches.S_OH_FUEL_CENTER_1";
    public const string OhFuelCenter2 = "system.switches.S_OH_FUEL_CENTER_2";

    #endregion

    #region ExteriorLights

    /// <summary>Beacon switch — also the ProSim-liveness sentinel for several ground
    /// services (they probe RawValue, not the fallback).</summary>
    public static readonly DataRef<int> OhExtLtBeacon = new("system.switches.S_OH_EXT_LT_BEACON", DataRefTier.Normal, 0); // 0=Off 1=On
    /// <summary>Warning — strobe values differ between platforms: Fenix 0=Auto/1=Off/2=On vs ProSim 0=Auto/1=On/2=Off.</summary>
    public const string OhExtLtStrobe = "system.switches.S_OH_EXT_LT_STROBE";
    public static readonly DataRef<int> OhExtLtLandingL = new("system.switches.S_OH_EXT_LT_LANDING_L", DataRefTier.Normal, 0); // 0=Off 1=On 2=Retract
    public static readonly DataRef<int> OhExtLtLandingR = new("system.switches.S_OH_EXT_LT_LANDING_R", DataRefTier.Normal, 0);
    public const string OhExtLtRwyTurnoff = "system.switches.S_OH_EXT_LT_RWY_TURNOFF"; // 0=Off 1=On
    public const string OhExtLtNose = "system.switches.S_OH_EXT_LT_NOSE"; // 0=Off 1=Taxi 2=TO
    public const string OhExtLtWing = "system.switches.S_OH_EXT_LT_WING"; // 0=Off 1=On
    public const string OhExtLtNavLogo = "system.switches.S_OH_EXT_LT_NAV_LOGO";

    #endregion

    #region InteriorLights

    public const string OhIntLtEmer = "system.switches.S_OH_INT_LT_EMER"; // 0=On 1=Arm 2=Off
    public const string OhIntLtDome = "system.switches.S_OH_INT_LT_DOME"; // 0=Brt 1=Dim 2=Off

    #endregion

    #region Signs

    public static readonly DataRef<int> OhSigns = new("system.switches.S_OH_SIGNS", DataRefTier.Normal, 0); // 0=Auto 1=On 2=Off
    public const string OhSignsSmoking = "system.switches.S_OH_SIGNS_SMOKING";

    #endregion

    #region MiscOverhead

    // Oxygen
    public const string OhOxygenCrewOxygen = "system.switches.S_OH_OXYGEN_CREW_OXYGEN";

    // GPWS
    public const string OhGpwsLdgFlap3 = "system.switches.S_OH_GPWS_LDG_FLAP3";

    // IRS / ADIRS — 0=Off 1=Nav 2=Att
    public const string OhNavIr1Mode = "system.switches.S_OH_NAV_IR1_MODE";
    public const string OhNavIr2Mode = "system.switches.S_OH_NAV_IR2_MODE";
    public const string OhNavIr3Mode = "system.switches.S_OH_NAV_IR3_MODE";

    // Hydraulic yellow electric pump
    public const string OhHydYellowElecPump = "system.switches.S_OH_HYD_YELLOW_ELEC_PUMP";
    public const string OhHydYellowElecPumpIndicator = "system.indicators.I_OH_HYD_YELLOW_ELEC_PUMP_L";

    // Calls
    public const string OhCallsAft = "system.switches.S_OH_CALLS_AFT";
    public const string OhCallsMech = "system.switches.S_OH_CALLS_MECH"; // [0:Normal, 1:Pushed]
    public const string OhCallsFwd = "system.switches.S_OH_CALLS_FWD";  // [0:Normal, 1:Pushed]

    // Fire test
    public const string FireApuTest = "system.switches.S_OH_FIRE_APU_TEST";

    #endregion

    #region AutobrakeAndGpwsTerrain

    public const string MipAutobrakeMax = "system.switches.S_MIP_AUTOBRAKE_MAX";
    public const string MipAutobrakeMaxIndicator = "system.indicators.I_MIP_AUTOBRAKE_MAX_L";
    public const string MipAutobrakeMed = "system.switches.S_MIP_AUTOBRAKE_MED";
    public const string MipAutobrakeMedIndicator = "system.indicators.I_MIP_AUTOBRAKE_MED_L";
    public const string MipAutobrakeLo = "system.switches.S_MIP_AUTOBRAKE_LO";
    public const string MipAutobrakeLoIndicator = "system.indicators.I_MIP_AUTOBRAKE_LO_L";
    public const string MipBrakeFan = "system.switches.S_MIP_BRAKE_FAN";
    public const string MipBrakeFanIndicator = "system.indicators.I_MIP_BRAKE_FAN_L";
    public const string MipGpwsTerrainOnNdCapt = "system.switches.S_MIP_GPWS_TERRAIN_ON_ND_CAPT";
    public const string MipGpwsTerrainOnNdCaptIndicator = "system.indicators.I_MIP_GPWS_TERRAIN_ON_ND_CAPT_L";
    public const string MipGpwsTerrainOnNdFo = "system.switches.S_MIP_GPWS_TERRAIN_ON_ND_FO";
    public const string MipGpwsTerrainOnNdFoIndicator = "system.indicators.I_MIP_GPWS_TERRAIN_ON_ND_FO_L";

    #endregion

    #region Fcu

    public const string FcuAthr = "system.switches.S_FCU_ATHR";
    public const string FcuExped = "system.switches.S_FCU_EXPED";
    public const string FcuSpdMach = "system.switches.S_FCU_SPD_MACH";
    public const string FcuAp1 = "system.switches.S_FCU_AP1";
    public static readonly DataRef<double> FcuAp1Indicator = new("system.indicators.I_FCU_AP1", DataRefTier.Frequent, 0.0);
    public const string FcuAp2 = "system.switches.S_FCU_AP2";
    public static readonly DataRef<double> FcuAp2Indicator = new("system.indicators.I_FCU_AP2", DataRefTier.Frequent, 0.0);
    public static readonly DataRef<double> FcuAthrIndicator = new("system.indicators.I_FCU_ATHR", DataRefTier.Frequent, 0.0);
    public const string FcuAppr = "system.switches.S_FCU_APPR";
    public static readonly DataRef<double> FcuApprIndicator = new("system.indicators.I_FCU_APPR", DataRefTier.Frequent, 0.0);
    public const string FcuLoc = "system.switches.S_FCU_LOC";
    public static readonly DataRef<double> FcuLocIndicator = new("system.indicators.I_FCU_LOC", DataRefTier.Frequent, 0.0);

    // Speed / heading / altitude / VS knobs are all push-pull:
    // 0=Normal, 1=Pushed (managed), 2=Pulled (selected)
    public const string FcuSpeed = "system.switches.S_FCU_SPEED";
    public const string FcuSpeedNum = "system.numerical.N_FCU_SPEED";
    public static readonly DataRef<double> FcuSpeedManaged = new("system.indicators.I_FCU_SPEED_MANAGED", DataRefTier.Frequent, 0.0);
    public const string FcuSpeedMode = "system.indicators.I_FCU_SPEED_MODE";

    public const string FcuHeading = "system.switches.S_FCU_HEADING";
    public const string FcuHeadingNum = "system.numerical.N_FCU_HEADING";
    public static readonly DataRef<double> FcuHeadingManaged = new("system.indicators.I_FCU_HEADING_MANAGED", DataRefTier.Frequent, 0.0);

    public const string FcuAltitude = "system.switches.S_FCU_ALTITUDE";
    public const string FcuAltitudeScale = "system.switches.S_FCU_ALTITUDE_SCALE"; // 0=100ft 1=1000ft
    public const string FcuAltitudeNum = "system.numerical.N_FCU_ALTITUDE";
    public static readonly DataRef<double> FcuAltitudeManaged = new("system.indicators.I_FCU_ALTITUDE_MANAGED", DataRefTier.Frequent, 0.0);

    public const string FcuVerticalSpeed = "system.switches.S_FCU_VERTICAL_SPEED";
    public const string FcuVsNum = "system.numerical.N_FCU_VS";

    // FCU display values (analog read-backs used to verify spoken FCU commands; the
    // altitude value is also a flight-data input).
    public static readonly DataRef<double> FcuSpeedValue = new("system.analog.A_FCU_SPEED", DataRefTier.Frequent, 0.0);
    public static readonly DataRef<double> FcuHeadingValue = new("system.analog.A_FCU_HEADING", DataRefTier.Frequent, 0.0);
    public static readonly DataRef<double> FcuAltitudeValue = new("system.analog.A_FCU_ALTITUDE", DataRefTier.Frequent, 0.0);
    public static readonly DataRef<double> FcuVsValue = new("system.analog.A_FCU_VS", DataRefTier.Frequent, 0.0);

    public const string FcuHdgVsTrkFpa = "system.switches.S_FCU_HDGVS_TRKFPA";
    public const string FcuTrackFpaModeIndicator = "system.indicators.I_FCU_TRACK_FPA_MODE";

    #endregion

    #region EfisCaptain

    public const string Efis1Fd = "system.switches.S_FCU_EFIS1_FD";
    public const string Efis1FdIndicator = "system.indicators.I_FCU_EFIS1_FD";
    public const string Efis1Ls = "system.switches.S_FCU_EFIS1_LS";
    public const string Efis1LsIndicator = "system.indicators.I_FCU_EFIS1_LS";
    public const string Efis1Cstr = "system.switches.S_FCU_EFIS1_CSTR";
    public const string Efis1CstrIndicator = "system.indicators.I_FCU_EFIS1_CSTR";
    public const string Efis1Arpt = "system.switches.S_FCU_EFIS1_ARPT";
    public const string Efis1ArptIndicator = "system.indicators.I_FCU_EFIS1_ARPT";
    public const string Efis1NdMode = "system.switches.S_FCU_EFIS1_ND_MODE"; // 0=ILS 1=VOR 2=NAV 3=ARC 4=PLAN
    public const string Efis1NdZoom = "system.switches.S_FCU_EFIS1_ND_ZOOM"; // 0=10 .. 5=320
    public const string Efis1Nav1 = "system.switches.S_FCU_EFIS1_NAV1"; // 0=Off 1=ADF 2=VOR
    public const string Efis1Nav2 = "system.switches.S_FCU_EFIS1_NAV2";
    public const string Efis1BaroMode = "system.switches.S_FCU_EFIS1_BARO_MODE"; // 0=inHg 1=hPa
    public const string Efis1BaroStd = "system.switches.S_FCU_EFIS1_BARO_STD"; // 0=Normal 1=Push(STD) 2=Pull(QNH)
    public const string Efis1QnhIndicator = "system.indicators.I_FCU_EFIS1_QNH";
    public const string Efis1BaroHpa = "system.numerical.N_FCU_EFIS1_BARO_HPA";
    public const string Efis1BaroInch = "system.numerical.N_FCU_EFIS1_BARO_INCH";

    #endregion

    #region EfisFirstOfficer

    public const string Efis2Fd = "system.switches.S_FCU_EFIS2_FD";
    public const string Efis2FdIndicator = "system.indicators.I_FCU_EFIS2_FD";
    public const string Efis2Ls = "system.switches.S_FCU_EFIS2_LS";
    public const string Efis2LsIndicator = "system.indicators.I_FCU_EFIS2_LS";
    public const string Efis2Cstr = "system.switches.S_FCU_EFIS2_CSTR";
    public const string Efis2CstrIndicator = "system.indicators.I_FCU_EFIS2_CSTR";
    public const string Efis2Arpt = "system.switches.S_FCU_EFIS2_ARPT";
    public const string Efis2ArptIndicator = "system.indicators.I_FCU_EFIS2_ARPT";
    public const string Efis2NdMode = "system.switches.S_FCU_EFIS2_ND_MODE";
    public const string Efis2NdZoom = "system.switches.S_FCU_EFIS2_ND_ZOOM";
    public const string Efis2Nav1 = "system.switches.S_FCU_EFIS2_NAV1";
    public const string Efis2Nav2 = "system.switches.S_FCU_EFIS2_NAV2";
    /// <summary>Fallback 1 (hPa): with no data, baro readouts assume hectopascals.</summary>
    public static readonly DataRef<int> Efis2BaroMode = new("system.switches.S_FCU_EFIS2_BARO_MODE", DataRefTier.Normal, 1);
    public const string Efis2BaroStd = "system.switches.S_FCU_EFIS2_BARO_STD";
    /// <summary>Effective STD state GATE — deliberately distinct from the
    /// <see cref="Efis2BaroStd"/> push-pull switch; spoken-token reads want the effective
    /// state, not the momentary knob position (#83, do not "fix" one onto the other).</summary>
    public static readonly DataRef<bool> Efis2BaroStdGate = new("system.gates.B_FCU_EFIS2_BARO_STD", DataRefTier.Normal, false);
    public const string Efis2QnhIndicator = "system.indicators.I_FCU_EFIS2_QNH";
    // Authored FO-side; PilotSeatMap swaps to EFIS1 when seated right.
    public static readonly DataRef<double> Efis2BaroHpa = new("system.numerical.N_FCU_EFIS2_BARO_HPA", DataRefTier.Normal, 0.0);
    public static readonly DataRef<double> Efis2BaroInch = new("system.numerical.N_FCU_EFIS2_BARO_INCH", DataRefTier.Normal, 0.0);

    #endregion

    #region Ecam

    public const string EcamRcl = "system.switches.S_ECAM_RCL";
    public const string EcamApu = "system.switches.S_ECAM_APU";
    public const string EcamEngine = "system.switches.S_ECAM_ENGINE";
    public const string EcamEngineIndicator = "system.indicators.I_ECAM_ENGINE";
    public const string EcamDoor = "system.switches.S_ECAM_DOOR";
    public const string EcamHyd = "system.switches.S_ECAM_HYD";
    public const string EcamStatus = "system.switches.S_ECAM_STATUS";
    public const string EcamStatusIndicator = "system.indicators.I_ECAM_STATUS";
    public const string EcamTo = "system.switches.S_ECAM_TO";

    #endregion

    #region TransponderTcas

    public const string XpdrOperation = "system.switches.S_XPDR_OPERATION"; // 0=Auto 1=Stdby 2=On
    public const string XpdrAtc = "system.switches.S_XPDR_ATC"; // 0=1 1=2
    public static readonly DataRef<int> XpdrMode = new("system.switches.S_XPDR_MODE", DataRefTier.Normal, 0); // 0=Stdby 1=TA 2=TA/RA
    public const string XpdrAltReporting = "system.switches.S_XPDR_ALTREPORTING"; // 0=On 1=Off
    public const string TcasRange = "system.switches.S_TCAS_RANGE"; // 0=Normal 1=Above 2=Below 3=Thrt

    #endregion

    #region WeatherRadar

    public const string WrSys = "system.switches.S_WR_SYS"; // 0=Off 1=Sys1 2=Sys2
    public const string WrMultiscan = "system.switches.S_WR_MULTISCAN"; // 0=Manual 1=Auto
    public const string WrPredWs = "system.switches.S_WR_PRED_WS"; // 0=Auto 1=Off

    #endregion

    #region Wipers

    public const string MiscWiperCapt = "system.switches.S_MISC_WIPER_CAPT"; // 0=Off 1=Slow 2=Fast
    public const string MiscWiperFo = "system.switches.S_MISC_WIPER_FO";

    #endregion

    #region Clocks

    public const string MipClockEt = "system.switches.S_MIP_CLOCK_ET"; // 0=RUN 1=STP 2=RST
    public const string MipClockChr = "system.switches.S_MIP_CLOCK_CHR";
    public const string MipChronoFo = "system.switches.S_MIP_CHRONO_FO";

    #endregion

    #region RmpTransfer

    public const string PedRmp1Xfer = "system.switches.S_PED_RMP1_XFER";
    public const string PedRmp2Xfer = "system.switches.S_PED_RMP2_XFER";

    #endregion

    #region Cdu2Keys

    // CDU2 (FO MCDU) key switch datarefs.
    public const string Cdu2Key0 = "system.switches.S_CDU2_KEY_0";
    public const string Cdu2Key1 = "system.switches.S_CDU2_KEY_1";
    public const string Cdu2Key2 = "system.switches.S_CDU2_KEY_2";
    public const string Cdu2Key3 = "system.switches.S_CDU2_KEY_3";
    public const string Cdu2Key4 = "system.switches.S_CDU2_KEY_4";
    public const string Cdu2Key5 = "system.switches.S_CDU2_KEY_5";
    public const string Cdu2Key6 = "system.switches.S_CDU2_KEY_6";
    public const string Cdu2Key7 = "system.switches.S_CDU2_KEY_7";
    public const string Cdu2Key8 = "system.switches.S_CDU2_KEY_8";
    public const string Cdu2Key9 = "system.switches.S_CDU2_KEY_9";
    public const string Cdu2KeyA = "system.switches.S_CDU2_KEY_A";
    public const string Cdu2KeyB = "system.switches.S_CDU2_KEY_B";
    public const string Cdu2KeyC = "system.switches.S_CDU2_KEY_C";
    public const string Cdu2KeyD = "system.switches.S_CDU2_KEY_D";
    public const string Cdu2KeyE = "system.switches.S_CDU2_KEY_E";
    public const string Cdu2KeyF = "system.switches.S_CDU2_KEY_F";
    public const string Cdu2KeyG = "system.switches.S_CDU2_KEY_G";
    public const string Cdu2KeyH = "system.switches.S_CDU2_KEY_H";
    public const string Cdu2KeyI = "system.switches.S_CDU2_KEY_I";
    public const string Cdu2KeyJ = "system.switches.S_CDU2_KEY_J";
    public const string Cdu2KeyK = "system.switches.S_CDU2_KEY_K";
    public const string Cdu2KeyL = "system.switches.S_CDU2_KEY_L";
    public const string Cdu2KeyM = "system.switches.S_CDU2_KEY_M";
    public const string Cdu2KeyN = "system.switches.S_CDU2_KEY_N";
    public const string Cdu2KeyO = "system.switches.S_CDU2_KEY_O";
    public const string Cdu2KeyP = "system.switches.S_CDU2_KEY_P";
    public const string Cdu2KeyQ = "system.switches.S_CDU2_KEY_Q";
    public const string Cdu2KeyR = "system.switches.S_CDU2_KEY_R";
    public const string Cdu2KeyS = "system.switches.S_CDU2_KEY_S";
    public const string Cdu2KeyT = "system.switches.S_CDU2_KEY_T";
    public const string Cdu2KeyU = "system.switches.S_CDU2_KEY_U";
    public const string Cdu2KeyV = "system.switches.S_CDU2_KEY_V";
    public const string Cdu2KeyW = "system.switches.S_CDU2_KEY_W";
    public const string Cdu2KeyX = "system.switches.S_CDU2_KEY_X";
    public const string Cdu2KeyY = "system.switches.S_CDU2_KEY_Y";
    public const string Cdu2KeyZ = "system.switches.S_CDU2_KEY_Z";
    public const string Cdu2KeyAirport = "system.switches.S_CDU2_KEY_AIRPORT";
    public const string Cdu2KeyArrowDown = "system.switches.S_CDU2_KEY_ARROW_DOWN";
    public const string Cdu2KeyArrowLeft = "system.switches.S_CDU2_KEY_ARROW_LEFT";
    public const string Cdu2KeyArrowRight = "system.switches.S_CDU2_KEY_ARROW_RIGHT";
    public const string Cdu2KeyArrowUp = "system.switches.S_CDU2_KEY_ARROW_UP";
    public const string Cdu2KeyAtcCom = "system.switches.S_CDU2_KEY_ATC_COM";
    public const string Cdu2KeyClb = "system.switches.S_CDU2_KEY_CLB";
    public const string Cdu2KeyClear = "system.switches.S_CDU2_KEY_CLEAR";
    public const string Cdu2KeyClearLine = "system.switches.S_CDU2_KEY_CLEAR_LINE";
    public const string Cdu2KeyData = "system.switches.S_CDU2_KEY_DATA";
    public const string Cdu2KeyDir = "system.switches.S_CDU2_KEY_DIR";
    public const string Cdu2KeyDot = "system.switches.S_CDU2_KEY_DOT";
    public const string Cdu2KeyFpln = "system.switches.S_CDU2_KEY_FPLN";
    public const string Cdu2KeyFuelPred = "system.switches.S_CDU2_KEY_FUEL_PRED";
    public const string Cdu2KeyInit = "system.switches.S_CDU2_KEY_INIT";
    public const string Cdu2KeyLsk1L = "system.switches.S_CDU2_KEY_LSK1L";
    public const string Cdu2KeyLsk1R = "system.switches.S_CDU2_KEY_LSK1R";
    public const string Cdu2KeyLsk2L = "system.switches.S_CDU2_KEY_LSK2L";
    public const string Cdu2KeyLsk2R = "system.switches.S_CDU2_KEY_LSK2R";
    public const string Cdu2KeyLsk3L = "system.switches.S_CDU2_KEY_LSK3L";
    public const string Cdu2KeyLsk3R = "system.switches.S_CDU2_KEY_LSK3R";
    public const string Cdu2KeyLsk4L = "system.switches.S_CDU2_KEY_LSK4L";
    public const string Cdu2KeyLsk4R = "system.switches.S_CDU2_KEY_LSK4R";
    public const string Cdu2KeyLsk5L = "system.switches.S_CDU2_KEY_LSK5L";
    public const string Cdu2KeyLsk5R = "system.switches.S_CDU2_KEY_LSK5R";
    public const string Cdu2KeyLsk6L = "system.switches.S_CDU2_KEY_LSK6L";
    public const string Cdu2KeyLsk6R = "system.switches.S_CDU2_KEY_LSK6R";
    public const string Cdu2KeyMenu = "system.switches.S_CDU2_KEY_MENU";
    public const string Cdu2KeyMinus = "system.switches.S_CDU2_KEY_MINUS";
    public const string Cdu2KeyOvfly = "system.switches.S_CDU2_KEY_OVFLY";
    public const string Cdu2KeyPerf = "system.switches.S_CDU2_KEY_PERF";
    public const string Cdu2KeyProg = "system.switches.S_CDU2_KEY_PROG";
    public const string Cdu2KeyRadNav = "system.switches.S_CDU2_KEY_RAD_NAV";
    public const string Cdu2KeySecFpln = "system.switches.S_CDU2_KEY_SEC_FPLN";
    public const string Cdu2KeySlash = "system.switches.S_CDU2_KEY_SLASH";
    public const string Cdu2KeySpace = "system.switches.S_CDU2_KEY_SPACE";

    #endregion

    #region Radios

    // COM radio frequency read-backs (spoken radio commands verify against these).
    public static readonly DataRef<double> RadioCom1Standby = new("system.analog.R_COM1_STANDBY", DataRefTier.Normal, 0.0);
    public static readonly DataRef<double> RadioCom1Active = new("system.analog.R_COM1_ACTIVE", DataRefTier.Normal, 0.0);
    public static readonly DataRef<double> RadioCom2Standby = new("system.analog.R_COM2_STANDBY", DataRefTier.Normal, 0.0);
    public static readonly DataRef<double> RadioCom2Active = new("system.analog.R_COM2_ACTIVE", DataRefTier.Normal, 0.0);

    #endregion

    #region Mcdu

    /// <summary>FO MCDU display content. Frequent is deliberate — the reader settle-checks
    /// consecutive frames. Authored FO-side; PilotSeatMap swaps to mcdu1 when seated right.</summary>
    public static readonly DataRef<string?> Mcdu2Display = new("aircraft.mcdu2.display", DataRefTier.Frequent, null);

    #endregion

    #region Abnormals

    /// <summary>FWC left ECAM content string — the failure monitor's trigger feed.</summary>
    public static readonly DataRef<string> FwcContentLeft = new("aircraft.fwc.content.left.str", DataRefTier.Normal, "");
    public static readonly DataRef<double> MipMasterWarningFo = new("system.indicators.I_MIP_MASTER_WARNING_FO", DataRefTier.Normal, 0.0);
    public static readonly DataRef<double> MipMasterCautionFo = new("system.indicators.I_MIP_MASTER_CAUTION_FO", DataRefTier.Normal, 0.0);

    #endregion

    /// <summary>
    /// SimConnect simulation variable names (not "L:" LVARs and not ProSim datarefs) —
    /// kept here because the source catalog treats them as part of the same wire vocabulary.
    /// </summary>
    public static class SimVars
    {
        /// <summary>MSFS camera state — drives the sim-session gate.</summary>
        public static readonly SimVarRef<int> CameraState = new("CAMERA STATE", "Enum", DataRefTier.Normal, 0);

        /// <summary>True while the user is walking around as an avatar (MSFS 2024).</summary>
        public static readonly SimVarRef<bool> IsAvatar = new("IS AVATAR", "Bool", DataRefTier.Normal, false);

        public const string Eng1Combustion = "ENG COMBUSTION:1";
        public const string Eng2Combustion = "ENG COMBUSTION:2";
        public const string DoorPointFwd = "INTERACTIVE POINT OPEN:8";
        public const string DoorPointAft = "INTERACTIVE POINT OPEN:9";
    }
}
