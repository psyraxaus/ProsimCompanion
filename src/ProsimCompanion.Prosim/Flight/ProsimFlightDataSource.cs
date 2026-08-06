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
    // Dataref names confirmed against the predecessors' proven flight data source.
    private const string OnGround = "system.gates.B_GROUND";
    private const string Ias = "aircraft.speed.ias";
    private const string GroundSpeed = "aircraft.speed.ground";
    private const string Altitude = "aircraft.altitude";
    private const string RadioAltitude = "aircraft.altitude.radio";
    private const string VerticalSpeed = "aircraft.verticalspeed";
    private const string Engine1State = "aircraft.systems.engines.1.state";
    private const string Engine2State = "aircraft.systems.engines.2.state";
    private const string Engine1Running = "aircraft.engines.1.running";
    private const string Engine2Running = "aircraft.engines.2.running";
    private const string Engine1N1 = "aircraft.engines.1.n1";
    private const string Engine2N1 = "aircraft.engines.2.n1";
    private const string Pushback = "groundservice.pushback";
    private const string ParkBrake = "system.switches.S_MIP_PARKING_BRAKE";
    private const string GearDown = "aircraft.gearDown";
    private const string DcBatteryBusPowered = "system.gates.B_ELEC_BUS_POWER_DC_BAT";

    // Callout/monitoring refs (Phase 5) — names confirmed in Prosim2FO's proven source.
    private const string FlexN1 = "aircraft.engines.limits.flex";
    private const string TogaN1 = "aircraft.engines.limits.toga";
    private const string V1 = "aircraft.fms.perf.takeOff.v1";
    private const string Vr = "aircraft.fms.perf.takeOff.vr";
    private const string V2 = "aircraft.fms.perf.takeOff.v2";
    private const string Vls = "aircraft.FAC1.VLS";
    private const string FlapHandle = "aircraft.flap.positionHandle";
    private const string FcuAltitude = "system.analog.A_FCU_ALTITUDE";
    // ProSim's typo, not ours — ground spoilers are only readable via this debug ref.
    private const string GroundSpoilers = "debug.groundSpoilersDeployd";
    private const string ReverseLeftMax = "system.switches.S_FC_THROTTLE_LEFT_MAX_REVERSE";
    private const string ReverseRightMax = "system.switches.S_FC_THROTTLE_RIGHT_MAX_REVERSE";

    /// <summary>N1 (%) above which take-off thrust is considered set while on the ground.</summary>
    private const double TakeoffThrustN1Threshold = 75;

    private readonly Dictionary<string, IDataRefSubscription> _subscriptions = new(StringComparer.Ordinal);

    public ProsimFlightDataSource(IProsimDataRefs dataRefs)
    {
        ArgumentNullException.ThrowIfNull(dataRefs);

        foreach (var (name, tier) in Subscriptions())
        {
            _subscriptions[name] = dataRefs.Subscribe(name, tier);
        }
    }

    /// <inheritdoc />
    public FlightDataSnapshot Sample()
    {
        var ias = _subscriptions[Ias];

        // Valid only once real data has arrived and the connection is alive.
        if (ias.RawValue is null || ias.IsStale)
        {
            return new FlightDataSnapshot { IsValid = false };
        }

        var engine1 = ReadEngineState(Engine1State, Engine1Running);
        var engine2 = ReadEngineState(Engine2State, Engine2Running);
        var anyRunning = engine1 == EngineReadState.Running || engine2 == EngineReadState.Running;
        var maxN1 = Math.Max(Get(Engine1N1, 0.0), Get(Engine2N1, 0.0));

        return new FlightDataSnapshot
        {
            IsValid = true,
            OnGround = Get(OnGround, true),
            IndicatedAirspeedKt = Get(Ias, 0.0),
            GroundSpeedKt = Get(GroundSpeed, 0.0),
            AltitudeFt = Get(Altitude, 0.0),
            RadioAltitudeFt = Get(RadioAltitude, 0.0),
            VerticalSpeedFpm = Get(VerticalSpeed, 0.0),
            AircraftPowered = Get(DcBatteryBusPowered, false),
            AnyEngineRunning = anyRunning,
            EngineStarting = engine1 == EngineReadState.Starting || engine2 == EngineReadState.Starting,
            PushbackActive = Get(Pushback, 0) > 0,
            ParkBrakeSet = Get(ParkBrake, 0) != 0,
            GearDown = Get(GearDown, true),
            TakeoffThrustSet = anyRunning && maxN1 >= TakeoffThrustN1Threshold,
            RawPushbackState = Get(Pushback, 0),
            RawEngine1State = Get(Engine1State, ""),
            RawEngine2State = Get(Engine2State, ""),
            MaxN1Percent = maxN1,
            AverageN1Percent = (Get(Engine1N1, 0.0) + Get(Engine2N1, 0.0)) / 2.0,
            FlexN1Target = Get(FlexN1, 0.0),
            TogaN1Target = Get(TogaN1, 0.0),
            V1Kt = Get(V1, 0),
            VrKt = Get(Vr, 0),
            V2Kt = Get(V2, 0),
            VlsKt = Get(Vls, 0.0),
            FlapHandle = Get(FlapHandle, 0),
            FcuAltitudeFt = Get(FcuAltitude, 0.0),
            GroundSpoilersDeployed = Get(GroundSpoilers, false),
            ReversersMaxBoth = Get(ReverseLeftMax, 0) != 0 && Get(ReverseRightMax, 0) != 0,
        };
    }

    public void Dispose()
    {
        foreach (var subscription in _subscriptions.Values)
        {
            subscription.Dispose();
        }
        _subscriptions.Clear();
    }

    private static IEnumerable<(string Name, DataRefTier Tier)> Subscriptions()
    {
        // Crossing-detection refs run at the 100 ms tier: V1/rotate and RA gates hang off
        // prev-vs-current comparisons, and 250 ms adds audible latency at 150 kt.
        yield return (OnGround, DataRefTier.Frequent);
        yield return (Ias, DataRefTier.Critical);
        yield return (GroundSpeed, DataRefTier.Frequent);
        yield return (Altitude, DataRefTier.Critical);
        yield return (RadioAltitude, DataRefTier.Critical);
        yield return (VerticalSpeed, DataRefTier.Critical);
        yield return (Engine1State, DataRefTier.Normal);
        yield return (Engine2State, DataRefTier.Normal);
        yield return (Engine1Running, DataRefTier.Normal);
        yield return (Engine2Running, DataRefTier.Normal);
        yield return (Engine1N1, DataRefTier.Frequent);
        yield return (Engine2N1, DataRefTier.Frequent);
        yield return (Pushback, DataRefTier.Normal);
        yield return (ParkBrake, DataRefTier.Normal);
        yield return (GearDown, DataRefTier.Normal);
        yield return (DcBatteryBusPowered, DataRefTier.Normal);
        yield return (FlexN1, DataRefTier.Infrequent);
        yield return (TogaN1, DataRefTier.Infrequent);
        yield return (V1, DataRefTier.Infrequent);
        yield return (Vr, DataRefTier.Infrequent);
        yield return (V2, DataRefTier.Infrequent);
        yield return (Vls, DataRefTier.Frequent);
        yield return (FlapHandle, DataRefTier.Normal);
        yield return (FcuAltitude, DataRefTier.Frequent);
        yield return (GroundSpoilers, DataRefTier.Frequent);
        yield return (ReverseLeftMax, DataRefTier.Frequent);
        yield return (ReverseRightMax, DataRefTier.Frequent);
    }

    /// <summary>Prefers the descriptive state string ("off"/"starting"/"running"); falls back to
    /// the boolean running dataref when the string is empty (missing dataref still yields a
    /// usable Off/Running).</summary>
    private EngineReadState ReadEngineState(string stateRef, string runningRef)
    {
        var state = Get(stateRef, "");
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

        return Get(runningRef, false) ? EngineReadState.Running : EngineReadState.Off;
    }

    private T Get<T>(string name, T fallback) => _subscriptions[name].GetValue(fallback);

    private enum EngineReadState
    {
        Off,
        Starting,
        Running,
    }
}
