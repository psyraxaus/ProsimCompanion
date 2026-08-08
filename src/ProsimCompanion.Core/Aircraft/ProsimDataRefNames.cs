namespace ProsimCompanion.Core.Aircraft;

/// <summary>
/// Canonical catalog of ProSim SDK dataref names, ported from ProsimInterface's
/// <c>ProsimConstants.cs</c>. The full ~4,470-row reference is <c>ProsimDataref.csv</c>
/// at the repo root.
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

    public const string Altitude = "aircraft.altitude";
    public const string GroundContact = "aircraft.ground";
    public const string EngineRunning1 = "aircraft.engines.1.running";
    public const string EngineRunning2 = "aircraft.engines.2.running";
    public const string Engine1N1 = "aircraft.engine1.raw";
    public const string Engine2N1 = "aircraft.engine2.raw";
    public const string BankAngle = "aircraft.bank";
    public const string PitchAngle = "aircraft.pitch";
    public const string HeadingMagnetic = "aircraft.heading.magnetic";
    public const string HeadingTrue = "aircraft.heading.true";
    public const string GroundSpeed = "aircraft.speed.ground";
    public const string IndicatedAirspeed = "aircraft.speed.ias";
    public const string VerticalSpeed = "aircraft.verticalspeed";
    public const string AircraftTime = "aircraft.time";

    #endregion

    #region Fuel

    public const string FuelTotal = "aircraft.fuel.total.amount.kg";
    public const string FuelTotalCapacity = "aircraft.fuel.total.capacity";
    public const string FuelLeft = "aircraft.fuel.left.amount.kg";
    public const string FuelRight = "aircraft.fuel.right.amount.kg";
    public const string FuelCenter = "aircraft.fuel.center.amount.kg";
    public const string FuelAct1 = "aircraft.fuel.ACT1.amount.kg";
    public const string FuelAct2 = "aircraft.fuel.ACT2.amount.kg";
    public const string FuelLeftCapacity = "aircraft.fuel.left.capacity";
    public const string FuelRightCapacity = "aircraft.fuel.right.capacity";
    public const string FuelCenterCapacity = "aircraft.fuel.center.capacity";

    // Per-tank A320 breakdown — `aircraft.fuel.left.amount.kg` is the WING aggregate
    // (inner + outer). The granular `aircraft.systems.fuel.*` refs expose each
    // individual tank, matching the 5-tank picture ProSim's own FUEL EFB page shows.
    // Amounts move during refuel (poll at the same cadence as the wing aggregates);
    // capacities are airframe-fixed, slow polling is plenty.
    public const string FuelLeftInner = "aircraft.systems.fuel.left.inner.amount.kg";
    public const string FuelLeftInnerCapacity = "aircraft.systems.fuel.left.inner.capacity";
    public const string FuelLeftOuter = "aircraft.systems.fuel.left.outer.amount.kg";
    public const string FuelLeftOuterCapacity = "aircraft.systems.fuel.left.outer.capacity";
    public const string FuelRightInner = "aircraft.systems.fuel.right.inner.amount.kg";
    public const string FuelRightInnerCapacity = "aircraft.systems.fuel.right.inner.capacity";
    public const string FuelRightOuter = "aircraft.systems.fuel.right.outer.amount.kg";
    public const string FuelRightOuterCapacity = "aircraft.systems.fuel.right.outer.capacity";

    /// <summary>Weight display unit from ProSim config.</summary>
    public const string WeightUnit = "system.config.Units.Weight";

    #endregion

    #region Refuel

    public const string RefuelFuelTarget = "aircraft.refuel.fuelTarget";
    public const string RefuelFuelTargetKg = "aircraft.refuel.fuelTarget.kg";
    public const string RefuelActive = "aircraft.refuel.refuelingActive";
    public const string RefuelPower = "aircraft.refuel.refuelingPower";
    public const string RefuelRate = "aircraft.refuel.refuelingRate";

    #endregion

    #region WeightAndBalance

    public const string WeightGross = "aircraft.weight.gross";
    public const string WeightGrossMax = "aircraft.weight.grossMax";
    public const string WeightZfw = "aircraft.weight.zfw";
    public const string WeightZfwMax = "aircraft.weight.zfwMax";
    public const string WeightFuel = "aircraft.weight.fuel";
    /// <summary>Readable equivalent of the write-only FMS INIT ZFWCG; drives MACZFW/MACGW.</summary>
    public const string Zfwcg = "aircraft.zfwcg";
    public const string CenterOfGravity = "aircraft.cg";
    public const string BalanceMac = "aircraft.balance.MAC";

    #endregion

    #region Passengers

    public const string PaxZone1Capacity = "aircraft.passengers.zone1.capacity";
    public const string PaxZone2Capacity = "aircraft.passengers.zone2.capacity";
    public const string PaxZone3Capacity = "aircraft.passengers.zone3.capacity";
    public const string PaxZone4Capacity = "aircraft.passengers.zone4.capacity";
    public const string PaxZone1Amount = "aircraft.passengers.zone1.amount";
    public const string PaxZone2Amount = "aircraft.passengers.zone2.amount";
    public const string PaxZone3Amount = "aircraft.passengers.zone3.amount";
    public const string PaxZone4Amount = "aircraft.passengers.zone4.amount";
    public const string PaxTotalWeight = "aircraft.passengers.total.weight";
    public const string PaxSeatOccupation = "aircraft.passengers.seatOccupation";
    public const string PaxSeatOccupationString = "aircraft.passengers.seatOccupation.string";
    /// <summary>Booked-passenger string (EFB namespace; also used as the pax-booked readout).</summary>
    public const string PaxBookedString = "efb.passengers.booked.string";

    #endregion

    #region Cargo

    public const string CargoForwardAmount = "aircraft.cargo.forward.amount";
    public const string CargoForwardCapacity = "aircraft.cargo.forward.capacity";
    public const string CargoAftAmount = "aircraft.cargo.aft.amount";
    public const string CargoAftCapacity = "aircraft.cargo.aft.capacity";
    public const string CargoBulkAmount = "aircraft.cargo.bulk.amount";
    public const string CargoBulkCapacity = "aircraft.cargo.bulk.capacity";

    #endregion

    #region Doors

    public const string Door1L = "doors.entry.left.fwd";
    public const string Door2L = "doors.wing.left.1";
    public const string Door3L = "doors.wing.left.2";
    public const string Door4L = "doors.entry.left.aft";
    public const string Door1R = "doors.entry.right.fwd";
    public const string Door2R = "doors.wing.right.1";
    public const string Door3R = "doors.wing.right.2";
    public const string Door4R = "doors.entry.right.aft";
    public const string DoorCargoForward = "doors.cargo.forward";
    public const string DoorCargoAft = "doors.cargo.aft";
    public const string DoorCargoBulk = "doors.cargo.bulk";
    public const string CockpitDoorSwitch = "system.switches.S_PED_COCKPIT_DOOR"; // [0:Normal, 1:Unlock, 2:Lock]
    public const string CockpitDoorState = "system.switches.S_DOORS_COCKPIT";
    public const string CockpitDoorIndicatorUpper = "system.indicators.I_PED_COCKPIT_DOOR_U";

    #endregion

    #region GroundServices

    public const string GroundPneumatic = "groundservice.pneumatic";
    public const string GroundPreconditionedAir = "groundservice.preconditionedAir";
    public const string GroundPower = "groundservice.groundpower";
    public const string Chocks = "efb.chocks";
    public const string PushbackState = "groundservice.pushback";
    public const string PushbackWait = "groundservice.pushback.wait";

    #endregion

    #region Efb

    public const string EfbLoaded = "efb.loaded";
    public const string EfbReady = "efb.ready";
    public const string EfbTest = "efb.test";
    public const string EfbBoardingStatus = "efb.efb.boardingStatus"; // double "efb." is the real published path
    public const string EfbPlannedCargoKg = "efb.plannedCargoKg";
    public const string EfbPlannedFuel = "efb.plannedfuel";
    public const string EfbPrelimLoadsheet = "efb.prelimLoadsheet";
    public const string EfbFinalLoadsheet = "efb.finalLoadsheet";
    public const string EfbSimbriefId = "efb.simbrief.id";
    public const string EfbSimbriefPlanImported = "efb.simbriefPlanImported";
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

    public const string FmsOrigin = "aircraft.fms.origin";
    public const string FmsDestination = "aircraft.fms.destination";
    public const string FmsAlternate = "aircraft.fms.alternate";
    public const string FmsCruiseAlt = "aircraft.fms.cruiseAlt";
    public const string FmsFlightPhase = "aircraft.fms.flightPhase";
    public const string FmsFlightPlanXml = "aircraft.fms.flightPlanXml";
    public const string FmsRoute = "aircraft.fms.route";
    public const string FmsTimeToDest = "aircraft.fms.TimeToDest";

    // FMS Performance
    public const string FmsPerfTakeoffFlaps = "aircraft.fms.perf.takeOff.flaps";
    public const string FmsPerfTakeoffFlexTemp = "aircraft.fms.perf.takeOff.flexTemp";
    public const string FmsPerfTakeoffV1 = "aircraft.fms.perf.takeOff.v1";
    public const string FmsPerfTakeoffVr = "aircraft.fms.perf.takeOff.vr";
    public const string FmsPerfTakeoffV2 = "aircraft.fms.perf.takeOff.v2";
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
    /// </summary>
    public const string ConfigEngineType = "system.config.Config.EPR";

    /// <summary>ProSim's configured weight unit — "LBS" for pounds, otherwise kilograms
    /// (predecessor's empirically-established rule; drives the "aircraft" unit source).</summary>
    public const string ConfigWeightUnit = "system.config.Units.Weight";

    /// <summary>
    /// Display-unit selector for the runway-shift value written to
    /// aircraft.fms.perf.takeOff.shift. Values: "Meters" | "Feet". Read this before
    /// composing the shift integer — ProSim interprets the number in this unit.
    /// </summary>
    public const string UnitTakeoffShift = "system.config.Units.TakeoffShiftUnit";

    #endregion

    #region SystemState

    public const string SimulatorConnected = "simulator.connected";
    public const string SimulatorTime = "simulator.time";
    public const string ZuluTime = "simulator.zuluTime";
    public const string AircraftTitle = "simulator.aircraft.title";

    #endregion

    #region Audio

    public const string CommIfeInUse = "aircraft.communication.ifeInUse";
    public const string CommPaInUse = "aircraft.communication.paInUse";
    public const string CommVideoInUse = "aircraft.communication.videoInUse";
    /// <summary>Path PREFIX for per-window audio refs — append the window name.</summary>
    public const string AudioWindowsPrefix = "aircraft.communication.windows.";
    public const string RmpPower1Switch = "system.switches.S_PED_RMP1_POWER";
    public const string RmpPower2Switch = "system.switches.S_PED_RMP2_POWER";

    // INT/RAD source switches
    public const string IntRadCpt = "system.switches.S_ASP_INTRAD";  // [0:INT, 1:Off, 2:RAD]
    public const string IntRadFo = "system.switches.S_ASP2_INTRAD"; // [0:INT, 1:Off, 2:RAD]

    // ACP1 Volume Knobs
    public const string Acp1CabAnalog = "system.analog.A_ASP_CAB_VOLUME";
    public const string Acp1Hf1Analog = "system.analog.A_ASP_HF_1_VOLUME";
    public const string Acp1Hf2Analog = "system.analog.A_ASP_HF_2_VOLUME";
    public const string Acp1IntAnalog = "system.analog.A_ASP_INT_VOLUME";
    public const string Acp1PaAnalog = "system.analog.A_ASP_PA_VOLUME";
    public const string Acp1Vhf1Analog = "system.analog.A_ASP_VHF_1_VOLUME";
    public const string Acp1Vhf2Analog = "system.analog.A_ASP_VHF_2_VOLUME";
    public const string Acp1Vhf3Analog = "system.analog.A_ASP_VHF_3_VOLUME";

    // ACP1 Latch Switches
    public const string Acp1CabLatch = "system.switches.S_ASP_CAB_REC_LATCH";
    public const string Acp1Hf1Latch = "system.switches.S_ASP_HF_1_REC_LATCH";
    public const string Acp1Hf2Latch = "system.switches.S_ASP_HF_2_REC_LATCH";
    public const string Acp1IntLatch = "system.switches.S_ASP_INT_REC_LATCH";
    public const string Acp1PaLatch = "system.switches.S_ASP_PA_REC_LATCH";
    public const string Acp1Vhf1Latch = "system.switches.S_ASP_VHF_1_REC_LATCH";
    public const string Acp1Vhf2Latch = "system.switches.S_ASP_VHF_2_REC_LATCH";
    public const string Acp1Vhf3Latch = "system.switches.S_ASP_VHF_3_REC_LATCH";

    // ACP2 Volume Knobs
    public const string Acp2CabAnalog = "system.analog.A_ASP2_CAB_VOLUME";
    public const string Acp2Hf1Analog = "system.analog.A_ASP2_HF_1_VOLUME";
    public const string Acp2Hf2Analog = "system.analog.A_ASP2_HF_2_VOLUME";
    public const string Acp2IntAnalog = "system.analog.A_ASP2_INT_VOLUME";
    public const string Acp2PaAnalog = "system.analog.A_ASP2_PA_VOLUME";
    public const string Acp2Vhf1Analog = "system.analog.A_ASP2_VHF_1_VOLUME";
    public const string Acp2Vhf2Analog = "system.analog.A_ASP2_VHF_2_VOLUME";
    public const string Acp2Vhf3Analog = "system.analog.A_ASP2_VHF_3_VOLUME";

    // ACP2 Latch Switches
    public const string Acp2CabLatch = "system.switches.S_ASP2_CAB_REC_LATCH";
    public const string Acp2Hf1Latch = "system.switches.S_ASP2_HF_1_REC_LATCH";
    public const string Acp2Hf2Latch = "system.switches.S_ASP2_HF_2_REC_LATCH";
    public const string Acp2IntLatch = "system.switches.S_ASP2_INT_REC_LATCH";
    public const string Acp2PaLatch = "system.switches.S_ASP2_PA_REC_LATCH";
    public const string Acp2Vhf1Latch = "system.switches.S_ASP2_VHF_1_REC_LATCH";
    public const string Acp2Vhf2Latch = "system.switches.S_ASP2_VHF_2_REC_LATCH";
    public const string Acp2Vhf3Latch = "system.switches.S_ASP2_VHF_3_REC_LATCH";

    // ACP3 (Observer) Volume Knobs
    public const string Acp3CabAnalog = "system.analog.A_ASP3_CAB_VOLUME";
    public const string Acp3Hf1Analog = "system.analog.A_ASP3_HF_1_VOLUME";
    public const string Acp3Hf2Analog = "system.analog.A_ASP3_HF_2_VOLUME";
    public const string Acp3IntAnalog = "system.analog.A_ASP3_INT_VOLUME";
    public const string Acp3PaAnalog = "system.analog.A_ASP3_PA_VOLUME";
    public const string Acp3Vhf1Analog = "system.analog.A_ASP3_VHF_1_VOLUME";
    public const string Acp3Vhf2Analog = "system.analog.A_ASP3_VHF_2_VOLUME";
    public const string Acp3Vhf3Analog = "system.analog.A_ASP3_VHF_3_VOLUME";

    // ACP3 (Observer) Latch Switches
    public const string Acp3CabLatch = "system.switches.S_ASP3_CAB_REC_LATCH";
    public const string Acp3Hf1Latch = "system.switches.S_ASP3_HF_1_REC_LATCH";
    public const string Acp3Hf2Latch = "system.switches.S_ASP3_HF_2_REC_LATCH";
    public const string Acp3IntLatch = "system.switches.S_ASP3_INT_REC_LATCH";
    public const string Acp3PaLatch = "system.switches.S_ASP3_PA_REC_LATCH";
    public const string Acp3Vhf1Latch = "system.switches.S_ASP3_VHF_1_REC_LATCH";
    public const string Acp3Vhf2Latch = "system.switches.S_ASP3_VHF_2_REC_LATCH";
    public const string Acp3Vhf3Latch = "system.switches.S_ASP3_VHF_3_REC_LATCH";

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
    public const string FcSpeedbrakeArmed = "system.switches.S_FC_SPEEDBRAKE_ARMED";

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
    public const string AnalogFoPitch = "system.analog.A_FC_FO_PITCH";
    public const string AnalogFoRoll = "system.analog.A_FC_FO_ROLL";
    public const string AnalogFoRudder = "system.analog.A_FC_FO_RUDDER";

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
    public const string FlapPositionHandle = "aircraft.flap.positionHandle";

    #endregion

    #region LandingGear

    public const string MipGear = "system.switches.S_MIP_GEAR";

    #endregion

    #region ParkingBrake

    public const string MipParkingBrake = "system.switches.S_MIP_PARKING_BRAKE";
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
    public const string ApuRunning = "system.gates.B_APU_RUNNING";
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

    public const string ElecExternalConnect = "system.gates.B_ELEC_EXTERNAL_CONNECT"; // AC external connect
    public const string ElecBusPowerDcEss = "system.gates.B_ELEC_BUS_POWER_DC_ESS";
    public const string ElecBusPowerAcEss = "system.gates.B_ELEC_BUS_POWER_AC_ESS";
    public const string ElecBusPowerDc1 = "system.gates.B_ELEC_BUS_POWER_DC_1";
    public const string ElecBatterySwitch1 = "system.gates.B_ELEC_BATTERY_SWITCH_1";

    /// <summary>
    /// Audio switching selector — 0:CAPT (capt swapped to ACP3), 1:NORM,
    /// 2:F/O (FO swapped to ACP3). Drives the per-ACP power gate.
    /// </summary>
    public const string AudioSwitching = "system.switches.S_AUDIO_SWITCHING";

    #endregion

    #region Pneumatics

    public const string OhPneumaticPack1 = "system.switches.S_OH_PNEUMATIC_PACK_1";
    public const string OhPneumaticPack2 = "system.switches.S_OH_PNEUMATIC_PACK_2";
    public const string OhPneumaticEng1AntiIce = "system.switches.S_OH_PNEUMATIC_ENG1_ANTI_ICE";
    public const string OhPneumaticEng2AntiIce = "system.switches.S_OH_PNEUMATIC_ENG2_ANTI_ICE";
    public const string OhPneumaticWingAntiIce = "system.switches.S_OH_PNEUMATIC_WING_ANTI_ICE";
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

    public const string OhExtLtBeacon = "system.switches.S_OH_EXT_LT_BEACON"; // 0=Off 1=On
    /// <summary>Warning — strobe values differ between platforms: Fenix 0=Auto/1=Off/2=On vs ProSim 0=Auto/1=On/2=Off.</summary>
    public const string OhExtLtStrobe = "system.switches.S_OH_EXT_LT_STROBE";
    public const string OhExtLtLandingL = "system.switches.S_OH_EXT_LT_LANDING_L"; // 0=Off 1=On 2=Retract
    public const string OhExtLtLandingR = "system.switches.S_OH_EXT_LT_LANDING_R";
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

    public const string OhSigns = "system.switches.S_OH_SIGNS"; // 0=Auto 1=On 2=Off
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

    public const string FcuAp1 = "system.switches.S_FCU_AP1";
    public const string FcuAp1Indicator = "system.indicators.I_FCU_AP1";
    public const string FcuAp2 = "system.switches.S_FCU_AP2";
    public const string FcuAp2Indicator = "system.indicators.I_FCU_AP2";
    public const string FcuAthrIndicator = "system.indicators.I_FCU_ATHR";
    public const string FcuAppr = "system.switches.S_FCU_APPR";
    public const string FcuApprIndicator = "system.indicators.I_FCU_APPR";
    public const string FcuLoc = "system.switches.S_FCU_LOC";
    public const string FcuLocIndicator = "system.indicators.I_FCU_LOC";

    // Speed / heading / altitude / VS knobs are all push-pull:
    // 0=Normal, 1=Pushed (managed), 2=Pulled (selected)
    public const string FcuSpeed = "system.switches.S_FCU_SPEED";
    public const string FcuSpeedNum = "system.numerical.N_FCU_SPEED";
    public const string FcuSpeedManaged = "system.indicators.I_FCU_SPEED_MANAGED";
    public const string FcuSpeedMode = "system.indicators.I_FCU_SPEED_MODE";

    public const string FcuHeading = "system.switches.S_FCU_HEADING";
    public const string FcuHeadingNum = "system.numerical.N_FCU_HEADING";
    public const string FcuHeadingManaged = "system.indicators.I_FCU_HEADING_MANAGED";

    public const string FcuAltitude = "system.switches.S_FCU_ALTITUDE";
    public const string FcuAltitudeScale = "system.switches.S_FCU_ALTITUDE_SCALE"; // 0=100ft 1=1000ft
    public const string FcuAltitudeNum = "system.numerical.N_FCU_ALTITUDE";
    public const string FcuAltitudeManaged = "system.indicators.I_FCU_ALTITUDE_MANAGED";

    public const string FcuVerticalSpeed = "system.switches.S_FCU_VERTICAL_SPEED";
    public const string FcuVsNum = "system.numerical.N_FCU_VS";

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
    public const string Efis2BaroMode = "system.switches.S_FCU_EFIS2_BARO_MODE";
    public const string Efis2BaroStd = "system.switches.S_FCU_EFIS2_BARO_STD";
    public const string Efis2QnhIndicator = "system.indicators.I_FCU_EFIS2_QNH";
    public const string Efis2BaroHpa = "system.numerical.N_FCU_EFIS2_BARO_HPA";
    public const string Efis2BaroInch = "system.numerical.N_FCU_EFIS2_BARO_INCH";

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
    public const string XpdrMode = "system.switches.S_XPDR_MODE"; // 0=Stdby 1=TA 2=TA/RA
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

    /// <summary>
    /// SimConnect / Fenix LVAR names ("L:" prefixed). These are SimConnect wire
    /// identifiers, not ProSim SDK dataref paths — same exactness rule applies.
    /// </summary>
    public static class Lvars
    {
        #region GsxIntegration

        public const string DoorToggleCargo1 = "L:FSDT_GSX_AIRCRAFT_CARGO_1_TOGGLE";
        public const string DoorToggleCargo2 = "L:FSDT_GSX_AIRCRAFT_CARGO_2_TOGGLE";
        public const string CargoLoading1 = "L:FSDT_GSX_BOARDING_CARGO_EXIT_0";
        public const string CargoLoading2 = "L:FSDT_GSX_BOARDING_CARGO_EXIT_1";
        public const string CargoUnloading1 = "L:FSDT_GSX_DEBOARDING_CARGO_EXIT_0";
        public const string CargoUnloading2 = "L:FSDT_GSX_DEBOARDING_CARGO_EXIT_1";
        public const string DoorToggleService1 = "L:FSDT_GSX_AIRCRAFT_SERVICE_1_TOGGLE";
        public const string DoorToggleService2 = "L:FSDT_GSX_AIRCRAFT_SERVICE_2_TOGGLE";
        public const string CockpitDoor = "L:S_COCKPIT_DOOR";
        public const string FsdtCockpitDoorOpen = "L:FSDT_GSX_COCKPIT_DOOR_OPEN";

        #endregion

        #region DisplayBrightness

        public const string DisplayBrightnessFo = "L:A_DISPLAY_BRIGHTNESS_FO";
        public const string DisplayBrightnessFi = "L:A_DISPLAY_BRIGHTNESS_FI";

        #endregion

        #region FlightControls

        public const string FcFlaps = "L:S_FC_FLAPS";
        public const string FcSpeedbrake = "L:A_FC_SPEEDBRAKE";
        public const string FcSpeedbrakeArmed = "L:S_FC_SPEEDBRAKE_ARMED";
        /// <summary>Warning — Fenix encodes elevator trim as value * 1000; ProSim's dataref returns degrees.</summary>
        public const string FcElevatorTrim = "L:A_FC_ELEVATOR_TRIM";
        public const string FcElevatorTrimNum = "L:N_FC_ELEVATOR_TRIM"; // numerical read-back
        public const string FcRudderTrimReset = "L:S_FC_RUDDER_TRIM_RESET";
        public const string SidestickCaptPitch = "L:N_FC_SIDESTICK_CAPT_PITCH";
        public const string SidestickFoPitch = "L:N_FC_SIDESTICK_FO_PITCH";
        public const string SidestickCaptBank = "L:N_FC_SIDESTICK_CAPT_BANK";
        public const string SidestickFoBank = "L:N_FC_SIDESTICK_FO_BANK";
        public const string FcRudderNum = "L:N_FC_RUDDER";
        public const string ThrottleLeftInput = "L:A_FC_THROTTLE_LEFT_INPUT";
        public const string ThrottleRightInput = "L:A_FC_THROTTLE_RIGHT_INPUT";

        #endregion

        #region GearAndBrakes

        public const string MipGear = "L:S_MIP_GEAR";
        public const string MipParkingBrake = "L:S_MIP_PARKING_BRAKE";
        public const string MipAutobrakeMax = "L:S_MIP_AUTOBRAKE_MAX";
        public const string MipAutobrakeMed = "L:S_MIP_AUTOBRAKE_MED";
        public const string MipAutobrakeLo = "L:S_MIP_AUTOBRAKE_LO";
        public const string MipBrakeFan = "L:S_MIP_BRAKE_FAN";
        public const string MipGpwsTerrainOnNdCapt = "L:S_MIP_GPWS_TERRAIN_ON_ND_CAPT";
        public const string MipGpwsTerrainOnNdFo = "L:S_MIP_GPWS_TERRAIN_ON_ND_FO";

        #endregion

        #region Engines

        public const string EngMaster1 = "L:S_ENG_MASTER_1";
        public const string EngMaster2 = "L:S_ENG_MASTER_2";
        public const string EngMode = "L:S_ENG_MODE";

        #endregion

        #region Apu

        public const string OhElecApuMaster = "L:S_OH_ELEC_APU_MASTER";
        public const string OhElecApuStart = "L:S_OH_ELEC_APU_START";
        public const string OhPneumaticApuBleed = "L:S_OH_PNEUMATIC_APU_BLEED";
        public const string ApuAvailIndicator = "L:I_OH_ELEC_APU_START_U"; // APU START upper LED = AVAIL

        #endregion

        #region Electrical

        public const string OhElecBat1 = "L:S_OH_ELEC_BAT1";
        public const string OhElecBat2 = "L:S_OH_ELEC_BAT2";
        public const string OhElecExtPwr = "L:S_OH_ELEC_EXT_PWR";
        public const string ExtPwrIndicatorLower = "L:I_OH_ELEC_EXT_PWR_L"; // indicator: ext pwr connected (ON)
        public const string ExtPwrIndicatorUpper = "L:I_OH_ELEC_EXT_PWR_U"; // indicator: ext pwr available
        public const string ElecBusPowerDcEss = "L:B_ELEC_BUS_POWER_DC_ESS";
        public const string ConfigGpu = "L:B_CONFIG_GPU";
        public const string ConfigChocks = "L:B_CONFIG_CHOCKS";

        #endregion

        #region Pneumatics

        public const string OhPneumaticPack1 = "L:S_OH_PNEUMATIC_PACK_1";
        public const string OhPneumaticPack2 = "L:S_OH_PNEUMATIC_PACK_2";
        public const string OhPneumaticEng1AntiIce = "L:S_OH_PNEUMATIC_ENG1_ANTI_ICE";
        public const string OhPneumaticEng2AntiIce = "L:S_OH_PNEUMATIC_ENG2_ANTI_ICE";
        public const string OhPneumaticWingAntiIce = "L:S_OH_PNEUMATIC_WING_ANTI_ICE";
        public const string OhPneumaticXbleedSelector = "L:S_OH_PNEUMATIC_XBLEED_SELECTOR";

        #endregion

        #region FuelPumps

        public const string OhFuelLeft1 = "L:S_OH_FUEL_LEFT_1";
        public const string OhFuelLeft2 = "L:S_OH_FUEL_LEFT_2";
        public const string OhFuelRight1 = "L:S_OH_FUEL_RIGHT_1";
        public const string OhFuelRight2 = "L:S_OH_FUEL_RIGHT_2";
        public const string OhFuelCenter1 = "L:S_OH_FUEL_CENTER_1";
        public const string OhFuelCenter2 = "L:S_OH_FUEL_CENTER_2";

        #endregion

        #region Refuel

        public const string RefuelPower = "L:S_THIRD_PARTY_REFUELG"; // sic — misspelled in ProSim/Fenix ("REFUELG")
        public const string RefuelValveLeft = "L:S_EXT_REFUELING_VALVE_LEFT";
        public const string RefuelValveCenter = "L:S_EXT_REFUELINGL_VALVE_CENTER"; // sic — misspelled in ProSim/Fenix ("REFUELINGL")
        public const string RefuelValveRight = "L:S_EXT_REFUELING_VALVE_RIGHT";
        public const string RefuelValveAct1 = "L:S_EXT_REFUELING_ACT_VALVE_1";
        public const string RefuelValveAct2 = "L:S_EXT_REFUELING_ACT_VALVE_2";
        public const string RefuelModeCover = "L:S_EXT_REFUELING_MODE_Cover"; // sic — mixed-case "Cover" is exact
        public const string RefuelMode = "L:S_EXT_REFUELING_MODE";

        #endregion

        #region ExteriorLights

        // Warning — strobe values differ: Fenix 0=Auto/1=Off/2=On vs ProSim 0=Auto/1=On/2=Off.
        public const string OhExtLtBeacon = "L:S_OH_EXT_LT_BEACON";
        public const string OhExtLtStrobe = "L:S_OH_EXT_LT_STROBE";
        public const string OhExtLtLandingL = "L:S_OH_EXT_LT_LANDING_L";
        public const string OhExtLtLandingR = "L:S_OH_EXT_LT_LANDING_R";
        public const string OhExtLtRwyTurnoff = "L:S_OH_EXT_LT_RWY_TURNOFF";
        public const string OhExtLtNose = "L:S_OH_EXT_LT_NOSE";
        public const string OhExtLtWing = "L:S_OH_EXT_LT_WING";
        public const string OhExtLtNavLogo = "L:S_OH_EXT_LT_NAV_LOGO";

        #endregion

        #region InteriorLightsAndSigns

        public const string OhIntLtEmer = "L:S_OH_INT_LT_EMER";
        public const string OhIntLtDome = "L:S_OH_INT_LT_DOME";
        public const string OhSigns = "L:S_OH_SIGNS";
        public const string OhSignsSmoking = "L:S_OH_SIGNS_SMOKING";

        #endregion

        #region MiscOverhead

        public const string OhOxygenCrewOxygen = "L:S_OH_OXYGEN_CREW_OXYGEN";
        public const string OhGpwsLdgFlap3 = "L:S_OH_GPWS_LDG_FLAP3";
        public const string OhNavIr1Mode = "L:S_OH_NAV_IR1_MODE";
        public const string OhNavIr2Mode = "L:S_OH_NAV_IR2_MODE";
        public const string OhNavIr3Mode = "L:S_OH_NAV_IR3_MODE";
        public const string OhHydYellowElecPump = "L:S_OH_HYD_YELLOW_ELEC_PUMP";
        public const string OhHydYellowElecPumpIndicator = "L:I_OH_HYD_YELLOW_ELEC_PUMP_L";
        public const string OhCallsAft = "L:S_OH_CALLS_AFT";
        public const string FireApuTest = "L:S_OH_FIRE_APU_TEST";

        #endregion

        #region Fcu

        public const string FcuAp1 = "L:S_FCU_AP1";
        public const string FcuAp1Indicator = "L:I_FCU_AP1";
        public const string FcuAp2 = "L:S_FCU_AP2";
        public const string FcuAp2Indicator = "L:I_FCU_AP2";
        public const string FcuAppr = "L:S_FCU_APPR";
        public const string FcuLoc = "L:S_FCU_LOC";
        public const string FcuSpeed = "L:S_FCU_SPEED";
        public const string FcuSpeedNum = "L:N_FCU_SPEED";
        public const string FcuSpeedManaged = "L:I_FCU_SPEED_MANAGED";
        public const string FcuSpeedMode = "L:I_FCU_SPEED_MODE";
        public const string FcuHeading = "L:S_FCU_HEADING";
        public const string FcuHeadingNum = "L:N_FCU_HEADING";
        public const string FcuHeadingManaged = "L:I_FCU_HEADING_MANAGED";
        public const string FcuAltitude = "L:S_FCU_ALTITUDE";
        public const string FcuAltitudeScale = "L:S_FCU_ALTITUDE_SCALE";
        public const string FcuAltitudeNum = "L:N_FCU_ALTITUDE";
        public const string FcuAltitudeManaged = "L:I_FCU_ALTITUDE_MANAGED";
        public const string FcuVerticalSpeed = "L:S_FCU_VERTICAL_SPEED";
        public const string FcuVsNum = "L:N_FCU_VS";
        public const string FcuHdgVsTrkFpa = "L:S_FCU_HDGVS_TRKFPA";
        public const string FcuTrackFpaModeIndicator = "L:I_FCU_TRACK_FPA_MODE";

        #endregion

        #region EfisCaptain

        public const string Efis1Fd = "L:S_FCU_EFIS1_FD";
        public const string Efis1FdIndicator = "L:I_FCU_EFIS1_FD";
        public const string Efis1Ls = "L:S_FCU_EFIS1_LS";
        public const string Efis1LsIndicator = "L:I_FCU_EFIS1_LS";
        public const string Efis1Cstr = "L:S_FCU_EFIS1_CSTR";
        public const string Efis1CstrIndicator = "L:I_FCU_EFIS1_CSTR";
        public const string Efis1Arpt = "L:S_FCU_EFIS1_ARPT";
        public const string Efis1ArptIndicator = "L:I_FCU_EFIS1_ARPT";
        public const string Efis1NdMode = "L:S_FCU_EFIS1_ND_MODE";
        public const string Efis1NdZoom = "L:S_FCU_EFIS1_ND_ZOOM";
        public const string Efis1Nav1 = "L:S_FCU_EFIS1_NAV1";
        public const string Efis1Nav2 = "L:S_FCU_EFIS1_NAV2";
        public const string Efis1BaroMode = "L:S_FCU_EFIS1_BARO_MODE";
        public const string Efis1QnhIndicator = "L:I_FCU_EFIS1_QNH";
        public const string Efis1BaroHpa = "L:N_FCU_EFIS1_BARO_HPA";
        public const string Efis1BaroInch = "L:N_FCU_EFIS1_BARO_INCH";

        #endregion

        #region EfisFirstOfficer

        public const string Efis2Fd = "L:S_FCU_EFIS2_FD";
        public const string Efis2FdIndicator = "L:I_FCU_EFIS2_FD";
        public const string Efis2Ls = "L:S_FCU_EFIS2_LS";
        public const string Efis2LsIndicator = "L:I_FCU_EFIS2_LS";
        public const string Efis2Cstr = "L:S_FCU_EFIS2_CSTR";
        public const string Efis2CstrIndicator = "L:I_FCU_EFIS2_CSTR";
        public const string Efis2Arpt = "L:S_FCU_EFIS2_ARPT";
        public const string Efis2ArptIndicator = "L:I_FCU_EFIS2_ARPT";
        public const string Efis2NdMode = "L:S_FCU_EFIS2_ND_MODE";
        public const string Efis2NdZoom = "L:S_FCU_EFIS2_ND_ZOOM";
        public const string Efis2Nav1 = "L:S_FCU_EFIS2_NAV1";
        public const string Efis2Nav2 = "L:S_FCU_EFIS2_NAV2";
        public const string Efis2BaroMode = "L:S_FCU_EFIS2_BARO_MODE";
        public const string Efis2QnhIndicator = "L:I_FCU_EFIS2_QNH";
        public const string Efis2BaroHpa = "L:N_FCU_EFIS2_BARO_HPA";
        public const string Efis2BaroInch = "L:N_FCU_EFIS2_BARO_INCH";

        #endregion

        #region Ecam

        public const string EcamRcl = "L:S_ECAM_RCL";
        public const string EcamApu = "L:S_ECAM_APU";
        public const string EcamEngine = "L:S_ECAM_ENGINE";
        public const string EcamEngineIndicator = "L:I_ECAM_ENGINE";
        public const string EcamDoor = "L:S_ECAM_DOOR";
        public const string EcamHyd = "L:S_ECAM_HYD";
        public const string EcamStatus = "L:S_ECAM_STATUS";
        public const string EcamStatusIndicator = "L:I_ECAM_STATUS";
        public const string EcamTo = "L:S_ECAM_TO";

        #endregion

        #region TransponderTcas

        public const string XpdrOperation = "L:S_XPDR_OPERATION";
        public const string XpdrAtc = "L:S_XPDR_ATC";
        public const string XpdrMode = "L:S_XPDR_MODE";
        public const string XpdrAltReporting = "L:S_XPDR_ALTREPORTING";
        public const string TcasRange = "L:S_TCAS_RANGE";

        #endregion

        #region WeatherRadar

        public const string WrSys = "L:S_WR_SYS";
        public const string WrMultiscan = "L:S_WR_MULTISCAN";
        public const string WrPredWs = "L:S_WR_PRED_WS";

        #endregion

        #region WipersAndClocks

        public const string MiscWiperCapt = "L:S_MISC_WIPER_CAPT";
        public const string MiscWiperFo = "L:S_MISC_WIPER_FO";
        public const string MipClockEt = "L:S_MIP_CLOCK_ET";
        public const string MipClockChr = "L:S_MIP_CLOCK_CHR";
        public const string MipChronoFo = "L:S_MIP_CHRONO_FO";

        #endregion

        #region RmpTransfer

        public const string PedRmp1Xfer = "L:S_PED_RMP1_XFER";
        public const string PedRmp2Xfer = "L:S_PED_RMP2_XFER";

        #endregion

        #region Audio

        public const string IntRadCpt = "L:S_ASP_INTRAD";
        public const string IntRadFo = "L:S_ASP2_INTRAD";
        public const string AcpCabSend = "L:S_ASP_CAB_SEND";
        public const string AcpVhfSend = "L:S_ASP_VHF_1_SEND";
        public const string AcpReset = "L:S_ASP_RESET";
        public const string AcpCabCall = "L:I_ASP_CAB_CALL";
        public const string AcpIntCallCpt = "L:I_ASP_INT_CALL";
        public const string AcpIntCallFo = "L:I_ASP2_INT_CALL";

        #endregion

        #region Cdu2Keys

        public const string Cdu2Key0 = "L:S_CDU2_KEY_0";
        public const string Cdu2Key1 = "L:S_CDU2_KEY_1";
        public const string Cdu2Key2 = "L:S_CDU2_KEY_2";
        public const string Cdu2Key3 = "L:S_CDU2_KEY_3";
        public const string Cdu2Key4 = "L:S_CDU2_KEY_4";
        public const string Cdu2Key5 = "L:S_CDU2_KEY_5";
        public const string Cdu2Key6 = "L:S_CDU2_KEY_6";
        public const string Cdu2Key7 = "L:S_CDU2_KEY_7";
        public const string Cdu2Key8 = "L:S_CDU2_KEY_8";
        public const string Cdu2Key9 = "L:S_CDU2_KEY_9";
        public const string Cdu2KeyA = "L:S_CDU2_KEY_A";
        public const string Cdu2KeyB = "L:S_CDU2_KEY_B";
        public const string Cdu2KeyC = "L:S_CDU2_KEY_C";
        public const string Cdu2KeyD = "L:S_CDU2_KEY_D";
        public const string Cdu2KeyE = "L:S_CDU2_KEY_E";
        public const string Cdu2KeyF = "L:S_CDU2_KEY_F";
        public const string Cdu2KeyG = "L:S_CDU2_KEY_G";
        public const string Cdu2KeyH = "L:S_CDU2_KEY_H";
        public const string Cdu2KeyI = "L:S_CDU2_KEY_I";
        public const string Cdu2KeyJ = "L:S_CDU2_KEY_J";
        public const string Cdu2KeyK = "L:S_CDU2_KEY_K";
        public const string Cdu2KeyL = "L:S_CDU2_KEY_L";
        public const string Cdu2KeyM = "L:S_CDU2_KEY_M";
        public const string Cdu2KeyN = "L:S_CDU2_KEY_N";
        public const string Cdu2KeyO = "L:S_CDU2_KEY_O";
        public const string Cdu2KeyP = "L:S_CDU2_KEY_P";
        public const string Cdu2KeyQ = "L:S_CDU2_KEY_Q";
        public const string Cdu2KeyR = "L:S_CDU2_KEY_R";
        public const string Cdu2KeyS = "L:S_CDU2_KEY_S";
        public const string Cdu2KeyT = "L:S_CDU2_KEY_T";
        public const string Cdu2KeyU = "L:S_CDU2_KEY_U";
        public const string Cdu2KeyV = "L:S_CDU2_KEY_V";
        public const string Cdu2KeyW = "L:S_CDU2_KEY_W";
        public const string Cdu2KeyX = "L:S_CDU2_KEY_X";
        public const string Cdu2KeyY = "L:S_CDU2_KEY_Y";
        public const string Cdu2KeyZ = "L:S_CDU2_KEY_Z";
        public const string Cdu2KeyAirport = "L:S_CDU2_KEY_AIRPORT";
        public const string Cdu2KeyArrowDown = "L:S_CDU2_KEY_ARROW_DOWN";
        public const string Cdu2KeyArrowLeft = "L:S_CDU2_KEY_ARROW_LEFT";
        public const string Cdu2KeyArrowRight = "L:S_CDU2_KEY_ARROW_RIGHT";
        public const string Cdu2KeyArrowUp = "L:S_CDU2_KEY_ARROW_UP";
        public const string Cdu2KeyAtcCom = "L:S_CDU2_KEY_ATC_COM";
        public const string Cdu2KeyClb = "L:S_CDU2_KEY_CLB";
        public const string Cdu2KeyClear = "L:S_CDU2_KEY_CLEAR";
        public const string Cdu2KeyClearLine = "L:S_CDU2_KEY_CLEAR_LINE";
        public const string Cdu2KeyData = "L:S_CDU2_KEY_DATA";
        public const string Cdu2KeyDir = "L:S_CDU2_KEY_DIR";
        public const string Cdu2KeyDot = "L:S_CDU2_KEY_DOT";
        public const string Cdu2KeyFpln = "L:S_CDU2_KEY_FPLN";
        public const string Cdu2KeyFuelPred = "L:S_CDU2_KEY_FUEL_PRED";
        public const string Cdu2KeyInit = "L:S_CDU2_KEY_INIT";
        public const string Cdu2KeyLsk1L = "L:S_CDU2_KEY_LSK1L";
        public const string Cdu2KeyLsk1R = "L:S_CDU2_KEY_LSK1R";
        public const string Cdu2KeyLsk2L = "L:S_CDU2_KEY_LSK2L";
        public const string Cdu2KeyLsk2R = "L:S_CDU2_KEY_LSK2R";
        public const string Cdu2KeyLsk3L = "L:S_CDU2_KEY_LSK3L";
        public const string Cdu2KeyLsk3R = "L:S_CDU2_KEY_LSK3R";
        public const string Cdu2KeyLsk4L = "L:S_CDU2_KEY_LSK4L";
        public const string Cdu2KeyLsk4R = "L:S_CDU2_KEY_LSK4R";
        public const string Cdu2KeyLsk5L = "L:S_CDU2_KEY_LSK5L";
        public const string Cdu2KeyLsk5R = "L:S_CDU2_KEY_LSK5R";
        public const string Cdu2KeyLsk6L = "L:S_CDU2_KEY_LSK6L";
        public const string Cdu2KeyLsk6R = "L:S_CDU2_KEY_LSK6R";
        public const string Cdu2KeyMenu = "L:S_CDU2_KEY_MENU";
        public const string Cdu2KeyMinus = "L:S_CDU2_KEY_MINUS";
        public const string Cdu2KeyOvfly = "L:S_CDU2_KEY_OVFLY";
        public const string Cdu2KeyPerf = "L:S_CDU2_KEY_PERF";
        public const string Cdu2KeyProg = "L:S_CDU2_KEY_PROG";
        public const string Cdu2KeyRadNav = "L:S_CDU2_KEY_RAD_NAV";
        public const string Cdu2KeySecFpln = "L:S_CDU2_KEY_SEC_FPLN";
        public const string Cdu2KeySlash = "L:S_CDU2_KEY_SLASH";
        public const string Cdu2KeySpace = "L:S_CDU2_KEY_SPACE";

        #endregion
    }

    /// <summary>
    /// SimConnect simulation variable names (not "L:" LVARs and not ProSim datarefs) —
    /// kept here because the source catalog treats them as part of the same wire vocabulary.
    /// </summary>
    public static class SimVars
    {
        public const string Eng1Combustion = "ENG COMBUSTION:1";
        public const string Eng2Combustion = "ENG COMBUSTION:2";
        public const string DoorPointFwd = "INTERACTIVE POINT OPEN:8";
        public const string DoorPointAft = "INTERACTIVE POINT OPEN:9";
    }
}
