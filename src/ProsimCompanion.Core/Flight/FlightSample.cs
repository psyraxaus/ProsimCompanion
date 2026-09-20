using System.Text.Json.Serialization;

namespace ProsimCompanion.Core.Flight;

/// <summary>
/// The compact per-second record of what the phase engine saw — the <c>flight-sample</c>
/// session event. Short JSON keys on purpose: a three-hour flight is ~10k lines and the
/// session log is pulled over the telemetry API. This record is both the writer's payload
/// and the replay reader's parser, so the key list lives in exactly one place; a field the
/// rule table starts reading must be added here in the same change or replays of older
/// recordings silently default it.
/// </summary>
public sealed record FlightSample
{
    /// <summary>Committed phase at sample time (what the live engine said — the replay
    /// compares its own verdict against this).</summary>
    [JsonPropertyName("ph")] public string Phase { get; init; } = "";

    [JsonPropertyName("og")] public bool OnGround { get; init; }
    [JsonPropertyName("ias")] public double IasKt { get; init; }
    [JsonPropertyName("gs")] public double GroundSpeedKt { get; init; }
    [JsonPropertyName("alt")] public double AltitudeFt { get; init; }
    [JsonPropertyName("ra")] public double RadioAltitudeFt { get; init; }
    [JsonPropertyName("agl")] public double AltitudeAglFt { get; init; }
    [JsonPropertyName("vs")] public double VerticalSpeedFpm { get; init; }
    [JsonPropertyName("pwr")] public bool Powered { get; init; }
    [JsonPropertyName("eng")] public bool EnginesRunning { get; init; }
    [JsonPropertyName("engRaw")] public bool EnginesRunningRaw { get; init; }
    [JsonPropertyName("st")] public bool EngineStarting { get; init; }
    [JsonPropertyName("pb")] public bool PushbackActive { get; init; }
    [JsonPropertyName("pbRaw")] public int RawPushbackState { get; init; }
    [JsonPropertyName("brk")] public bool ParkBrakeSet { get; init; }
    [JsonPropertyName("gear")] public bool GearDown { get; init; }
    [JsonPropertyName("apu")] public bool ApuRunning { get; init; }
    [JsonPropertyName("bcn")] public bool BeaconOn { get; init; }
    [JsonPropertyName("thr")] public bool TakeoffThrustSet { get; init; }
    [JsonPropertyName("n1")] public double MaxN1Percent { get; init; }
    [JsonPropertyName("n1avg")] public double AverageN1Percent { get; init; }
    [JsonPropertyName("flap")] public int FlapHandle { get; init; }
    [JsonPropertyName("fcu")] public double FcuAltitudeFt { get; init; }
    [JsonPropertyName("fms")] public double FmsCruiseAltFt { get; init; }
    [JsonPropertyName("v1")] public int V1Kt { get; init; }
    [JsonPropertyName("vr")] public int VrKt { get; init; }
    [JsonPropertyName("v2")] public int V2Kt { get; init; }

    /// <summary>Set only while a manual override has frozen the engine, so a replay diff can
    /// explain a stretch where the live phase stopped following the rules.</summary>
    [JsonPropertyName("frz"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Frozen { get; init; }

    /// <summary>The engine's boarding latch (Preflight → Departure evidence). Omitted while
    /// false so recordings made before 2026-09-20 parse unchanged; the replay harness also
    /// re-derives it from the session's GSX Boarding events, so older recordings still
    /// exercise the Departure rule.</summary>
    [JsonPropertyName("brd"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool BoardingStarted { get; init; }

    /// <summary>Builds the record from the engine's view. Values are rounded to what the rule
    /// table can distinguish so unchanged flight states compare equal and are not re-written.</summary>
    public static FlightSample From(FlightStateView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var s = view.Data ?? throw new ArgumentException("A sample needs flight data.", nameof(view));
        return new FlightSample
        {
            Phase = view.Phase.ToString(),
            OnGround = s.OnGround,
            IasKt = Math.Round(s.IndicatedAirspeedKt, 1),
            GroundSpeedKt = Math.Round(s.GroundSpeedKt, 1),
            AltitudeFt = Math.Round(s.AltitudeFt),
            RadioAltitudeFt = Math.Round(s.RadioAltitudeFt),
            AltitudeAglFt = Math.Round(s.AltitudeAglFt),
            VerticalSpeedFpm = Math.Round(s.VerticalSpeedFpm),
            Powered = s.AircraftPowered,
            EnginesRunning = s.AnyEngineRunning,
            EnginesRunningRaw = s.AnyEngineRunningRaw,
            EngineStarting = s.EngineStarting,
            PushbackActive = s.PushbackActive,
            RawPushbackState = s.RawPushbackState,
            ParkBrakeSet = s.ParkBrakeSet,
            GearDown = s.GearDown,
            ApuRunning = s.ApuRunning,
            BeaconOn = s.BeaconOn,
            TakeoffThrustSet = s.TakeoffThrustSet,
            MaxN1Percent = Math.Round(s.MaxN1Percent),
            AverageN1Percent = Math.Round(s.AverageN1Percent),
            FlapHandle = s.FlapHandle,
            FcuAltitudeFt = Math.Round(s.FcuAltitudeFt),
            FmsCruiseAltFt = Math.Round(s.FmsCruiseAltFt),
            V1Kt = s.V1Kt,
            VrKt = s.VrKt,
            V2Kt = s.V2Kt,
            Frozen = view.Frozen,
            BoardingStarted = s.BoardingStarted,
        };
    }

    /// <summary>The snapshot the replay feeds the engine: valid, ready, and carrying every
    /// field the rule table reads.</summary>
    public FlightDataSnapshot ToSnapshot() => new()
    {
        IsValid = true,
        IsReady = true,
        OnGround = OnGround,
        IndicatedAirspeedKt = IasKt,
        GroundSpeedKt = GroundSpeedKt,
        AltitudeFt = AltitudeFt,
        RadioAltitudeFt = RadioAltitudeFt,
        AltitudeAglFt = AltitudeAglFt,
        VerticalSpeedFpm = VerticalSpeedFpm,
        AircraftPowered = Powered,
        AnyEngineRunning = EnginesRunning,
        AnyEngineRunningRaw = EnginesRunningRaw,
        EngineStarting = EngineStarting,
        PushbackActive = PushbackActive,
        RawPushbackState = RawPushbackState,
        ParkBrakeSet = ParkBrakeSet,
        GearDown = GearDown,
        ApuRunning = ApuRunning,
        BeaconOn = BeaconOn,
        TakeoffThrustSet = TakeoffThrustSet,
        MaxN1Percent = MaxN1Percent,
        AverageN1Percent = AverageN1Percent,
        FlapHandle = FlapHandle,
        FcuAltitudeFt = FcuAltitudeFt,
        FmsCruiseAltFt = FmsCruiseAltFt,
        V1Kt = V1Kt,
        VrKt = VrKt,
        V2Kt = V2Kt,
        BoardingStarted = BoardingStarted,
    };
}
