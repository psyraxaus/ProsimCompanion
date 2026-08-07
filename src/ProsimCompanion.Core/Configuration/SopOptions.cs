namespace ProsimCompanion.Core.Configuration;

/// <summary>One switchable callout: enabled flag + spoken text.</summary>
public class CalloutSetting
{
    public CalloutSetting()
    {
    }

    public CalloutSetting(string text, bool enabled = true)
    {
        Text = text;
        Enabled = enabled;
    }

    public bool Enabled { get; set; } = true;
    public string Text { get; set; } = "";
}

/// <summary>A callout triggered at an IAS threshold.</summary>
public sealed class SpeedCalloutSetting : CalloutSetting
{
    public SpeedCalloutSetting()
    {
    }

    public SpeedCalloutSetting(string text, int speedKt, bool enabled = true)
        : base(text, enabled)
    {
        SpeedKt = speedKt;
    }

    public int SpeedKt { get; set; }
}

/// <summary>A callout triggered descending through a radio altitude.</summary>
public sealed class RadioCalloutSetting : CalloutSetting
{
    public RadioCalloutSetting()
    {
    }

    public RadioCalloutSetting(string text, int raFt, bool enabled = true)
        : base(text, enabled)
    {
        RaFt = raFt;
    }

    public int RaFt { get; set; }
}

/// <summary>Which crossing direction an altitude callout fires in.</summary>
public enum AltitudeCalloutDirection
{
    Both,
    Climb,
    Descent,
}

/// <summary>A barometric-altitude crossing callout ("ten thousand").</summary>
public sealed class AltitudeCallout
{
    public bool Enabled { get; set; } = true;
    public int AtFt { get; set; }
    public string Text { get; set; } = "";
    public AltitudeCalloutDirection Direction { get; set; } = AltitudeCalloutDirection.Both;
}

/// <summary>"One thousand to go" against the FCU-selected altitude.</summary>
public sealed class OneThousandToGoSetting
{
    public bool Enabled { get; set; } = true;
    public string Text { get; set; } = "one thousand to go";
    public int WithinFt { get; set; } = 1000;
}

/// <summary>Max IAS per flap handle position (handle scale: 0=Up 1=F1 2=F1+F 3=F2 4=F3 5=F4).</summary>
public sealed class FlapPlacard
{
    public int FlapHandle { get; set; }
    public int MaxKt { get; set; }
}

/// <summary>Overspeed advisory texts and pacing.</summary>
public sealed class PlacardAdvisoryOptions
{
    public bool Enabled { get; set; } = true;
    public int WarnWithinKt { get; set; } = 5;
    public int CooldownSeconds { get; set; } = 15;
    public string ApproachingText { get; set; } = "speed, approaching flap limit";
    public string ExceededText { get; set; } = "speed, flap limit";
    public string GearExceededText { get; set; } = "speed, gear limit";
}

/// <summary>One flow-monitor advisory: enabled + wording + priority (no thresholds here —
/// shared thresholds live on <see cref="FlowMonitorOptions"/>/<see cref="SopWeatherOptions"/>).</summary>
public sealed class FlowCheckSetting
{
    public FlowCheckSetting()
    {
    }

    public FlowCheckSetting(string text, string priority, bool enabled = true)
    {
        Text = text;
        Priority = priority;
        Enabled = enabled;
    }

    public bool Enabled { get; set; } = true;
    public string Text { get; set; } = "";

    /// <summary>SpeechPriority name: low / normal / high / critical (unparseable → high).</summary>
    public string Priority { get; set; } = "high";
}

/// <summary>Silent-flow anomaly advisories. Each is individually toggleable with its own
/// wording + priority (predecessor defaults; the opt-in extras ship disabled).</summary>
public sealed class FlowMonitorOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Minimum seconds between repeats of the same advisory (never nags).</summary>
    public double RateLimitSeconds { get; set; } = 60;

    public FlowCheckSetting LandingLightsAboveCeiling { get; set; } = new("landing lights still on", "low");
    public FlowCheckSetting LandingLightsBelowCeiling { get; set; } = new("landing lights", "low", enabled: false);
    public FlowCheckSetting FlapsNotRetracted { get; set; } = new("flaps still extended", "high");
    public FlowCheckSetting GearStillDown { get; set; } = new("gear still down", "high");
    public FlowCheckSetting ParkingBrakeWithThrust { get; set; } = new("parking brake still set", "high");
    public FlowCheckSetting SeatbeltSignsOff { get; set; } = new("seatbelt signs off", "low", enabled: false);

    // Opt-in extras (disabled by default).
    public FlowCheckSetting BeaconOffEngineRunning { get; set; } = new("beacon", "high", enabled: false);
    public FlowCheckSetting SpoilersNotArmed { get; set; } = new("spoilers not armed", "high", enabled: false);
    public FlowCheckSetting TransponderNotSet { get; set; } = new("transponder", "high", enabled: false);

    /// <summary>AGL (ft) above which flaps should be retracted after takeoff.</summary>
    public double FlapsCleanAboveAglFt { get; set; } = 3000;

    /// <summary>AGL (ft) above which the gear should be up in the climb.</summary>
    public double GearUpAboveAglFt { get; set; } = 1000;
}

/// <summary>Weather-awareness advisories (icing, anti-ice hygiene, ISA deviation). Named
/// "Sop…" to leave the plain <c>WeatherOptions</c> name to the weather-source section — the
/// bound JSON is unaffected (the <c>sop.weather</c> key comes from the property name).</summary>
public sealed class SopWeatherOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>In icing conditions with engine anti-ice off.</summary>
    public FlowCheckSetting IcingConditions { get; set; } = new("Icing conditions. Consider engine anti-ice.", "high");

    /// <summary>Anti-ice left on well clear of icing (fuel/perf penalty).</summary>
    public FlowCheckSetting AntiIceLeftOn { get; set; } = new("Anti-ice is still on, and we're clear of icing.", "low");

    /// <summary>Top-of-climb ISA-deviation note — its Text is IGNORED (built dynamically with
    /// the value); only Enabled/Priority are read.</summary>
    public FlowCheckSetting IsaDeviation { get; set; } = new("", "low");

    /// <summary>TAT (°C) at or below which icing is possible (with visible moisture).</summary>
    public double IcingTatMaxC { get; set; } = 10;

    /// <summary>Require visible moisture (in cloud, or visibility below the threshold).</summary>
    public bool RequireVisibleMoisture { get; set; } = true;

    /// <summary>Visibility (m) at or below which counts as visible moisture (fog/mist).</summary>
    public double MoistureVisibilityM { get; set; } = 1500;

    /// <summary>TAT (°C) above which anti-ice on is clearly outside the icing envelope.</summary>
    public double AntiIceClearC { get; set; } = 15;

    /// <summary>Seconds the anti-ice-left-on condition must hold before advising (avoids
    /// brief warm layers).</summary>
    public double AntiIceDwellSeconds { get; set; } = 120;

    /// <summary>|ISA deviation| (°C) at or above which the top-of-climb note is made.</summary>
    public double IsaDeviationThresholdC { get; set; } = 10;
}

/// <summary>Stabilized-approach announcement config.</summary>
public sealed class StabilizedOptions
{
    public bool Enabled { get; set; } = true;
    public string StableText { get; set; } = "stabilized";
    public string UnstableText { get; set; } = "unstable, go around";
    public bool RequireThrustStabilized { get; set; }
    public double MinStabilizedN1 { get; set; } = 40;
}

/// <summary>One stabilized-approach gate, evaluated descending through its radio altitude.</summary>
public sealed class ApproachGate
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "";
    public int AglFt { get; set; }
    public int MaxSinkRateFpm { get; set; } = 1000;

    /// <summary>Allowed IAS band around VLS (there is no VAPP dataref — VLS + band is the
    /// predecessor-proven reference).</summary>
    public int SpeedBandBelowVls { get; set; } = 5;
    public int SpeedBandAboveVls { get; set; } = 20;

    public bool RequireGearDown { get; set; } = true;
    public int RequireFlapHandleAtLeast { get; set; } = 3;

    /// <summary>Also announce when the gate passes (only the 1000 gate by default).</summary>
    public bool AnnounceStabilized { get; set; }
}

/// <summary>
/// SOP callouts + stabilized-approach configuration (the flight-deck behavior of the voice FO).
/// Defaults are Prosim2FO's shipped profile verbatim. Everything here is ADVISORY — it only
/// ever speaks, never commands or blocks. In Prosim2FO this lived in hot-reloadable
/// sop/*.json profiles; it moves there if/when ProsimCompanion grows SOP profiles.
/// </summary>
public sealed class SopOptions
{
    public const string SectionName = "sop";

    /// <summary>Master switch for SOP callouts (hot-toggles; checked every tick).</summary>
    public bool CalloutsEnabled { get; set; } = true;

    /// <summary>Callout sampling cadence (floored to 50 ms; read once at start).</summary>
    public int CalloutPollIntervalMs { get; set; } = 100;

    // ---- Takeoff ----
    public CalloutSetting ThrustSet { get; set; } = new("thrust set");
    public SpeedCalloutSetting HundredKnots { get; set; } = new("one hundred", 100);
    public CalloutSetting V1 { get; set; } = new("V one");
    public CalloutSetting Rotate { get; set; } = new("rotate");
    public CalloutSetting V2 { get; set; } = new("V two", enabled: false);
    public CalloutSetting PositiveClimb { get; set; } = new("positive climb");

    // ---- Climb/cruise/descent ----
    public static IReadOnlyList<AltitudeCallout> DefaultAltitudeCallouts =>
    [
        new() { Enabled = true, AtFt = 10_000, Text = "ten thousand", Direction = AltitudeCalloutDirection.Both },
        // Transition altitude has no dataref — a fixed-altitude entry the user opts into.
        new() { Enabled = false, AtFt = 18_000, Text = "transition altitude", Direction = AltitudeCalloutDirection.Climb },
    ];

    public List<AltitudeCallout> AltitudeCallouts { get; set; } = [.. DefaultAltitudeCallouts];
    public OneThousandToGoSetting OneThousandToGo { get; set; } = new();

    // ---- Approach ----
    public RadioCalloutSetting OneThousand { get; set; } = new("one thousand", 1000);
    public RadioCalloutSetting FiveHundred { get; set; } = new("five hundred", 500);
    public CalloutSetting HundredAbove { get; set; } = new("one hundred above");
    public CalloutSetting Minimums { get; set; } = new("minimums");

    // ---- Rollout ----
    public CalloutSetting Spoilers { get; set; } = new("spoilers");
    public CalloutSetting ReverseGreen { get; set; } = new("reverse green");
    public SpeedCalloutSetting DecelSpeed { get; set; } = new("seventy knots", 70);

    // ---- Placards ----
    public static IReadOnlyList<FlapPlacard> DefaultFlapPlacards =>
    [
        new() { FlapHandle = 1, MaxKt = 230 },
        new() { FlapHandle = 2, MaxKt = 200 },
        new() { FlapHandle = 3, MaxKt = 185 },
        new() { FlapHandle = 4, MaxKt = 177 },
        new() { FlapHandle = 5, MaxKt = 177 },
    ];

    public List<FlapPlacard> FlapPlacards { get; set; } = [.. DefaultFlapPlacards];

    /// <summary>Gear-extended speed limit (kt); 0 disables the advisory.</summary>
    public int GearMaxKt { get; set; } = 280;

    public PlacardAdvisoryOptions PlacardAdvisory { get; set; } = new();

    // ---- Flow monitor + weather advisories ----
    public FlowMonitorOptions FlowMonitor { get; set; } = new();
    public SopWeatherOptions Weather { get; set; } = new();

    // ---- Stabilized approach ----
    public StabilizedOptions Stabilized { get; set; } = new();

    public static IReadOnlyList<ApproachGate> DefaultApproachGates =>
    [
        new()
        {
            Name = "1000", AglFt = 1000, MaxSinkRateFpm = 1000, SpeedBandBelowVls = 5,
            SpeedBandAboveVls = 20, RequireGearDown = true, RequireFlapHandleAtLeast = 3,
            AnnounceStabilized = true,
        },
        new()
        {
            Name = "500", AglFt = 500, MaxSinkRateFpm = 1000, SpeedBandBelowVls = 5,
            SpeedBandAboveVls = 15, RequireGearDown = true, RequireFlapHandleAtLeast = 4,
            AnnounceStabilized = false,
        },
    ];

    /// <summary>Evaluated highest-first; entries with AglFt &lt;= 0 are ignored.</summary>
    public List<ApproachGate> ApproachGates { get; set; } = [.. DefaultApproachGates];
}
