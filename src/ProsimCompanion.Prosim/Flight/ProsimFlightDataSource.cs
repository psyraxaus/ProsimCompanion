using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Flight;

namespace ProsimCompanion.Prosim.Flight;

/// <summary>
/// Live <see cref="IFlightDataSource"/> over the ProSim push read model. All reads are cached
/// subscription values — <see cref="Sample"/> never touches the network. While ProSim is
/// disconnected (values stale) the snapshot reports <see cref="FlightDataSnapshot.IsValid"/> =
/// false and the flight state engine holds its last phase.
/// </summary>
public sealed class ProsimFlightDataSource : IFlightDataSource, IDisposable
{
    /// <summary>N1 (%) above which take-off thrust is considered set while on the ground.</summary>
    private const double TakeoffThrustN1Threshold = 75;

    /// <summary>The refs phase derivation actually depends on. Until every one of these has
    /// pushed a first value the snapshot reports <see cref="FlightDataSnapshot.IsReady"/> =
    /// false and the flight state engine refuses to classify: at startup ProSim registers the
    /// subscription set over several seconds (flight test 2026-08-16, issue #59 — the gear
    /// refs registered 6 s AFTER the engine had already classified Unknown→Approach off the
    /// half-populated defaults). Exposed internal for tests.</summary>
    internal static readonly string[] PhaseCriticalRefs =
    [
        ProsimDataRefNames.OnGroundGate.Name,
        ProsimDataRefNames.IndicatedAirspeed.Name,
        ProsimDataRefNames.GroundSpeed.Name,
        ProsimDataRefNames.Altitude.Name,
        ProsimDataRefNames.RadioAltitude.Name,
        ProsimDataRefNames.VerticalSpeed.Name,
        ProsimDataRefNames.Engine1State.Name,
        ProsimDataRefNames.Engine2State.Name,
        ProsimDataRefNames.EngineRunning1.Name,
        ProsimDataRefNames.EngineRunning2.Name,
        ProsimDataRefNames.Engine1N1Percent.Name,
        ProsimDataRefNames.Engine2N1Percent.Name,
        ProsimDataRefNames.PushbackState.Name,
        ProsimDataRefNames.MipParkingBrake.Name,
        ProsimDataRefNames.GearDown.Name,
        ProsimDataRefNames.ElecBusPowerDcBat.Name,
    ];

    // Phase-critical refs (names confirmed against the predecessors' proven flight data
    // source; tiers and fallbacks live on the catalog descriptors, #83).
    private readonly IDataRefSubscription<bool> _onGround;
    private readonly IDataRefSubscription<double> _ias;
    private readonly IDataRefSubscription<double> _groundSpeed;
    private readonly IDataRefSubscription<double> _altitude;
    private readonly IDataRefSubscription<double> _radioAltitude;
    private readonly IDataRefSubscription<double> _verticalSpeed;
    private readonly IDataRefSubscription<string> _engine1State;
    private readonly IDataRefSubscription<string> _engine2State;
    private readonly IDataRefSubscription<bool> _engine1Running;
    private readonly IDataRefSubscription<bool> _engine2Running;
    private readonly IDataRefSubscription<double> _engine1N1;
    private readonly IDataRefSubscription<double> _engine2N1;
    private readonly IDataRefSubscription<int> _pushback;
    private readonly IDataRefSubscription<int> _parkBrake;
    private readonly IDataRefSubscription<bool> _gearDown;
    private readonly IDataRefSubscription<bool> _dcBatteryBusPowered;

    // Beacon+APU gate the untrustworthy pushback flag (issue #100). Deliberately NOT
    // phase-critical: a missing APU ref degrades to flag-only evidence, never blocks IsReady.
    private readonly IDataRefSubscription<bool> _apuRunning;

    // Callout/monitoring refs (Phase 5) — names confirmed in Prosim2FO's proven source.
    private readonly IDataRefSubscription<double> _flexN1;
    private readonly IDataRefSubscription<double> _togaN1;
    private readonly IDataRefSubscription<int> _v1;
    private readonly IDataRefSubscription<int> _vr;
    private readonly IDataRefSubscription<int> _v2;
    private readonly IDataRefSubscription<double> _vls;
    private readonly IDataRefSubscription<int> _flapHandle;
    private readonly IDataRefSubscription<double> _fcuAltitude;
    private readonly IDataRefSubscription<bool> _groundSpoilers;
    private readonly IDataRefSubscription<int> _reverseLeftMax;
    private readonly IDataRefSubscription<int> _reverseRightMax;

    // Flow-monitor refs.
    private readonly IDataRefSubscription<double> _altitudeAgl;
    private readonly IDataRefSubscription<int> _landingLightL;
    private readonly IDataRefSubscription<int> _landingLightR;
    private readonly IDataRefSubscription<int> _seatbeltSigns;
    private readonly IDataRefSubscription<int> _beacon;
    private readonly IDataRefSubscription<int> _speedbrakeArmed;
    private readonly IDataRefSubscription<int> _xpdrMode;
    private readonly IDataRefSubscription<double> _tat;
    private readonly IDataRefSubscription<double> _oat;
    private readonly IDataRefSubscription<bool> _inCloud;
    private readonly IDataRefSubscription<double> _visibility;
    private readonly IDataRefSubscription<int> _engAntiIce1;
    private readonly IDataRefSubscription<int> _engAntiIce2;
    private readonly IDataRefSubscription<int> _wingAntiIce;

    private readonly IDataRefSubscription[] _phaseCritical;
    private readonly IDataRefSubscription[] _all;

    public ProsimFlightDataSource(IProsimDataRefs dataRefs)
    {
        ArgumentNullException.ThrowIfNull(dataRefs);

        // Crossing-detection refs (IAS/altitude/RA/VS, N1, VLS, FCU altitude, spoilers,
        // reversers) carry Critical/Frequent tiers on their descriptors: V1/rotate and RA
        // gates hang off prev-vs-current comparisons, and 250 ms adds audible latency at
        // 150 kt. The flow monitor ticks at 1 Hz — Normal/Infrequent tiers suffice there.
        _onGround = dataRefs.Subscribe(ProsimDataRefNames.OnGroundGate);
        _ias = dataRefs.Subscribe(ProsimDataRefNames.IndicatedAirspeed);
        _groundSpeed = dataRefs.Subscribe(ProsimDataRefNames.GroundSpeed);
        _altitude = dataRefs.Subscribe(ProsimDataRefNames.Altitude);
        _radioAltitude = dataRefs.Subscribe(ProsimDataRefNames.RadioAltitude);
        _verticalSpeed = dataRefs.Subscribe(ProsimDataRefNames.VerticalSpeed);
        _engine1State = dataRefs.Subscribe(ProsimDataRefNames.Engine1State);
        _engine2State = dataRefs.Subscribe(ProsimDataRefNames.Engine2State);
        _engine1Running = dataRefs.Subscribe(ProsimDataRefNames.EngineRunning1);
        _engine2Running = dataRefs.Subscribe(ProsimDataRefNames.EngineRunning2);
        _engine1N1 = dataRefs.Subscribe(ProsimDataRefNames.Engine1N1Percent);
        _engine2N1 = dataRefs.Subscribe(ProsimDataRefNames.Engine2N1Percent);
        _pushback = dataRefs.Subscribe(ProsimDataRefNames.PushbackState);
        _parkBrake = dataRefs.Subscribe(ProsimDataRefNames.MipParkingBrake);
        _gearDown = dataRefs.Subscribe(ProsimDataRefNames.GearDown);
        _dcBatteryBusPowered = dataRefs.Subscribe(ProsimDataRefNames.ElecBusPowerDcBat);
        _apuRunning = dataRefs.Subscribe(ProsimDataRefNames.ApuRunning);
        _flexN1 = dataRefs.Subscribe(ProsimDataRefNames.EnginesLimitFlex);
        _togaN1 = dataRefs.Subscribe(ProsimDataRefNames.EnginesLimitToga);
        _v1 = dataRefs.Subscribe(ProsimDataRefNames.FmsPerfTakeoffV1);
        _vr = dataRefs.Subscribe(ProsimDataRefNames.FmsPerfTakeoffVr);
        _v2 = dataRefs.Subscribe(ProsimDataRefNames.FmsPerfTakeoffV2);
        _vls = dataRefs.Subscribe(ProsimDataRefNames.Fac1Vls);
        _flapHandle = dataRefs.Subscribe(ProsimDataRefNames.FlapPositionHandle);
        _fcuAltitude = dataRefs.Subscribe(ProsimDataRefNames.FcuAltitudeValue);
        _groundSpoilers = dataRefs.Subscribe(ProsimDataRefNames.GroundSpoilersDeployed);
        _reverseLeftMax = dataRefs.Subscribe(ProsimDataRefNames.ThrottleLeftMaxReverse);
        _reverseRightMax = dataRefs.Subscribe(ProsimDataRefNames.ThrottleRightMaxReverse);
        _altitudeAgl = dataRefs.Subscribe(ProsimDataRefNames.AltitudeAboveGround);
        _landingLightL = dataRefs.Subscribe(ProsimDataRefNames.OhExtLtLandingL);
        _landingLightR = dataRefs.Subscribe(ProsimDataRefNames.OhExtLtLandingR);
        _seatbeltSigns = dataRefs.Subscribe(ProsimDataRefNames.OhSigns);
        _beacon = dataRefs.Subscribe(ProsimDataRefNames.OhExtLtBeacon);
        _speedbrakeArmed = dataRefs.Subscribe(ProsimDataRefNames.FcSpeedbrakeArmed);
        _xpdrMode = dataRefs.Subscribe(ProsimDataRefNames.XpdrMode);
        _tat = dataRefs.Subscribe(ProsimDataRefNames.TemperatureTat);
        _oat = dataRefs.Subscribe(ProsimDataRefNames.TemperatureOat);
        _inCloud = dataRefs.Subscribe(ProsimDataRefNames.AmbientInCloud);
        _visibility = dataRefs.Subscribe(ProsimDataRefNames.AmbientVisibility);
        _engAntiIce1 = dataRefs.Subscribe(ProsimDataRefNames.OhPneumaticEng1AntiIce);
        _engAntiIce2 = dataRefs.Subscribe(ProsimDataRefNames.OhPneumaticEng2AntiIce);
        _wingAntiIce = dataRefs.Subscribe(ProsimDataRefNames.OhPneumaticWingAntiIce);

        // Must cover the same refs, in the same order, as PhaseCriticalRefs above.
        _phaseCritical =
        [
            _onGround, _ias, _groundSpeed, _altitude, _radioAltitude, _verticalSpeed,
            _engine1State, _engine2State, _engine1Running, _engine2Running, _engine1N1, _engine2N1,
            _pushback, _parkBrake, _gearDown, _dcBatteryBusPowered,
        ];

        _all =
        [
            .. _phaseCritical,
            _apuRunning,
            _flexN1, _togaN1, _v1, _vr, _v2, _vls, _flapHandle, _fcuAltitude,
            _groundSpoilers, _reverseLeftMax, _reverseRightMax,
            _altitudeAgl, _landingLightL, _landingLightR, _seatbeltSigns, _beacon,
            _speedbrakeArmed, _xpdrMode, _tat, _oat, _inCloud, _visibility,
            _engAntiIce1, _engAntiIce2, _wingAntiIce,
        ];
    }

    /// <inheritdoc />
    public FlightDataSnapshot Sample()
    {
        // Valid only once real data has arrived and the connection is alive.
        if (_ias.RawValue is null || _ias.IsStale)
        {
            return new FlightDataSnapshot { IsValid = false };
        }

        var engine1 = ReadEngineState(_engine1State, _engine1Running);
        var engine2 = ReadEngineState(_engine2State, _engine2Running);
        // Engine-running is the state STRING ORed with the raw running boolean (issue #59):
        // an unexpected/transient state string maps to Off (see the hazard note on
        // FlightDataSnapshot.AnyEngineRunningRaw), and a momentary both-engines-off read
        // mid-taxi would walk the phase engine backwards into Preflight/Shutdown logic.
        var anyRunningRaw = _engine1Running.Value || _engine2Running.Value;
        var anyRunning = engine1 == EngineReadState.Running || engine2 == EngineReadState.Running || anyRunningRaw;
        var maxN1 = Math.Max(_engine1N1.Value, _engine2N1.Value);

        return new FlightDataSnapshot
        {
            IsValid = true,
            IsReady = _phaseCritical.All(subscription =>
                subscription.RawValue is not null && !subscription.IsStale),
            OnGround = _onGround.Value,
            IndicatedAirspeedKt = _ias.Value,
            GroundSpeedKt = _groundSpeed.Value,
            AltitudeFt = _altitude.Value,
            RadioAltitudeFt = _radioAltitude.Value,
            VerticalSpeedFpm = _verticalSpeed.Value,
            AircraftPowered = _dcBatteryBusPowered.Value,
            AnyEngineRunning = anyRunning,
            EngineStarting = engine1 == EngineReadState.Starting || engine2 == EngineReadState.Starting,
            PushbackActive = _pushback.Value > 0,
            ParkBrakeSet = _parkBrake.Value != 0,
            GearDown = _gearDown.Value,
            ApuRunning = _apuRunning.Value,
            // On the ground only (issue #101): climb/cruise N1 routinely exceeds 75%, which
            // kept this flag true for entire flights in telemetry — and reverse thrust after
            // touchdown can exceed it too, a TakeoffRoll trap if arrival context ever gaps.
            TakeoffThrustSet = _onGround.Value && anyRunning && maxN1 >= TakeoffThrustN1Threshold,
            RawPushbackState = _pushback.Value,
            RawEngine1State = _engine1State.Value,
            RawEngine2State = _engine2State.Value,
            MaxN1Percent = maxN1,
            AverageN1Percent = (_engine1N1.Value + _engine2N1.Value) / 2.0,
            FlexN1Target = _flexN1.Value,
            TogaN1Target = _togaN1.Value,
            V1Kt = _v1.Value,
            VrKt = _vr.Value,
            V2Kt = _v2.Value,
            VlsKt = _vls.Value,
            FlapHandle = _flapHandle.Value,
            FcuAltitudeFt = _fcuAltitude.Value,
            GroundSpoilersDeployed = _groundSpoilers.Value,
            ReversersMaxBoth = _reverseLeftMax.Value != 0 && _reverseRightMax.Value != 0,
            AltitudeAglFt = _altitudeAgl.Value,
            AnyLandingLightOn = _landingLightL.Value == 1 || _landingLightR.Value == 1,
            SeatbeltSignsMode = _seatbeltSigns.Value,
            BeaconOn = _beacon.Value != 0,
            SpeedbrakeArmed = _speedbrakeArmed.Value != 0,
            XpdrMode = _xpdrMode.Value,
            TatC = _tat.Value,   // benign 15 °C descriptor fallbacks: a missing temperature
            OatC = _oat.Value,   // dataref must never read as icing conditions

            InCloud = _inCloud.Value,
            VisibilityM = _visibility.Value,
            EngineAntiIce1On = _engAntiIce1.Value != 0,
            EngineAntiIce2On = _engAntiIce2.Value != 0,
            WingAntiIceOn = _wingAntiIce.Value != 0,
            AnyEngineRunningRaw = anyRunningRaw,
        };
    }

    public void Dispose()
    {
        foreach (var subscription in _all)
        {
            subscription.Dispose();
        }
    }

    /// <summary>Prefers the descriptive state string ("off"/"starting"/"running"); falls back to
    /// the boolean running dataref when the string is empty (missing dataref still yields a
    /// usable Off/Running).</summary>
    private static EngineReadState ReadEngineState(
        IDataRefSubscription<string> stateRef, IDataRefSubscription<bool> runningRef)
    {
        var state = stateRef.Value;
        if (!string.IsNullOrEmpty(state))
        {
            if (state.Equals("running", StringComparison.OrdinalIgnoreCase))
            {
                return EngineReadState.Running;
            }

            if (state.Equals("starting", StringComparison.OrdinalIgnoreCase))
            {
                return EngineReadState.Starting;
            }

            return EngineReadState.Off;
        }

        return runningRef.Value ? EngineReadState.Running : EngineReadState.Off;
    }

    private enum EngineReadState
    {
        Off,
        Starting,
        Running,
    }
}
