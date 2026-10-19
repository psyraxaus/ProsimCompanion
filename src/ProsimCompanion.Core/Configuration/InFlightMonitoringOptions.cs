namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// The four in-flight FO monitoring modules (issue #148), under <c>sop.monitoring</c>. Each
/// has its own switch, OFF by default; every module arms on "Flight live" and only ever
/// speaks — nothing here writes to the aircraft. Edited on Settings → Voice First Officer →
/// Callouts &amp; Placards → In-flight monitoring.
/// </summary>
public sealed class InFlightMonitoringOptions
{
    public FuelCheckOptions FuelCheck { get; set; } = new();

    public GrossErrorCheckOptions GrossErrorCheck { get; set; } = new();

    public DestinationWeatherWatchOptions DestinationWeather { get; set; } = new();

    public ReadbackOptions Readbacks { get; set; } = new();
}

/// <summary>Periodic and on-request cruise fuel check against the SimBrief navlog.</summary>
public sealed class FuelCheckOptions
{
    [OptionField(FieldKind.Bool, "speechSettings.callouts.monitoring.fuelCheck")]
    public bool Enabled { get; set; }

    /// <summary>Minutes between automatic checks in the cruise; the first one comes this long
    /// after the cruise begins. 0 = on request ("fuel check") only.</summary>
    [OptionField(FieldKind.Number, "speechSettings.callouts.monitoring.fuelEvery", Suffix = "min", Min = "0", Max = "120", DependsOn = "Enabled")]
    public int IntervalMinutes { get; set; } = 30;

    /// <summary>A fuel-on-board figure this many kilograms (or more) BELOW the plan is a
    /// shortfall: spoken at High instead of Normal.</summary>
    [OptionField(FieldKind.Number, "speechSettings.callouts.monitoring.shortfall", Suffix = "kg", Min = "0", Max = "5000", DependsOn = "Enabled")]
    public int ShortfallMarginKg { get; set; } = 300;

    /// <summary>Below this the difference against plan is "on plan" rather than a figure.</summary>
    [OptionField(FieldKind.Number, "speechSettings.callouts.monitoring.onPlan", Suffix = "kg", Min = "0", Max = "2000", DependsOn = "Enabled")]
    public int OnPlanWithinKg { get; set; } = 100;
}

/// <summary>The one-line takeoff gross-error check against the final loadsheet and the last
/// takeoff performance calculation.</summary>
public sealed class GrossErrorCheckOptions
{
    [OptionField(FieldKind.Bool, "speechSettings.callouts.monitoring.grossError")]
    public bool Enabled { get; set; }

    /// <summary>Automatic once per departure cycle, when the final loadsheet is out and the
    /// V-speeds are in the FMS. Off = "gross error check" on request only.</summary>
    [OptionField(FieldKind.Bool, "speechSettings.callouts.monitoring.automatic", DependsOn = "Enabled")]
    public bool Automatic { get; set; } = true;

    [OptionField(FieldKind.Number, "speechSettings.callouts.monitoring.zfw", Suffix = "kg", Min = "0", Max = "5000", DependsOn = "Enabled")]
    public int ZfwToleranceKg { get; set; } = 500;

    [OptionField(FieldKind.Number, "speechSettings.callouts.monitoring.blockFuel", Suffix = "kg", Min = "0", Max = "5000", DependsOn = "Enabled")]
    public int BlockFuelToleranceKg { get; set; } = 300;

    [OptionField(FieldKind.Number, "speechSettings.common.vSpeedTolerance", Suffix = "kt", Min = "0", Max = "20", DependsOn = "Enabled")]
    public int VSpeedToleranceKt { get; set; } = 3;

    [OptionField(FieldKind.Number, "speechSettings.callouts.monitoring.flex", Suffix = "°C", Min = "0", Max = "20", DependsOn = "Enabled")]
    public int FlexToleranceC { get; set; } = 2;
}

/// <summary>Destination (and alternate) weather watch from the cruise onward.</summary>
public sealed class DestinationWeatherWatchOptions
{
    [OptionField(FieldKind.Bool, "speechSettings.callouts.monitoring.destWx")]
    public bool Enabled { get; set; }

    /// <summary>Visibility (metres) whose crossing — down or back up — is announced.</summary>
    [OptionField(FieldKind.Number, "speechSettings.callouts.monitoring.visibility", Suffix = "m", Min = "0", Max = "10000", DependsOn = "Enabled")]
    public int VisibilityThresholdM { get; set; } = 1500;

    /// <summary>Ceiling (feet) whose crossing — down or back up — is announced.</summary>
    [OptionField(FieldKind.Number, "speechSettings.callouts.monitoring.ceiling", Suffix = "ft", Min = "0", Max = "5000", DependsOn = "Enabled")]
    public int CeilingThresholdFt { get; set; } = 500;

    /// <summary>A tailwind component on the planned landing runway above this many knots is
    /// announced (and its going away again).</summary>
    [OptionField(FieldKind.Number, "speechSettings.callouts.monitoring.tailwind", Suffix = "kt", Min = "0", Max = "30", DependsOn = "Enabled")]
    public int TailwindThresholdKt { get; set; } = 5;

    /// <summary>At most one announcement per trigger type (visibility, ceiling, ATIS, wind)
    /// in this many minutes.</summary>
    [OptionField(FieldKind.Number, "speechSettings.callouts.monitoring.quiet", Suffix = "min", Min = "1", Max = "120", DependsOn = "Enabled")]
    public int MinutesBetweenAnnouncements { get; set; } = 15;

    /// <summary>Announce an ATIS information letter change at the destination.</summary>
    [OptionField(FieldKind.Bool, "speechSettings.callouts.monitoring.atis", DependsOn = "Enabled")]
    public bool AnnounceAtisChange { get; set; } = true;
}

/// <summary>Voice read-backs outside a checklist: "altimeter one zero one three", "V speeds
/// one four one, one four four, one four seven", "runway two seven right", "minimums two one
/// zero" — the FO checks the figure against the aircraft and answers "checked" or the value
/// it reads.</summary>
public sealed class ReadbackOptions
{
    [OptionField(FieldKind.Bool, "speechSettings.callouts.monitoring.readbacks")]
    public bool Enabled { get; set; }

    /// <summary>Altimeter read-back tolerance, hectopascals.</summary>
    [OptionField(FieldKind.Number, "speechSettings.callouts.monitoring.altimeter", Suffix = "hPa", Min = "0", Max = "5", Step = "0.1", DependsOn = "Enabled")]
    public double AltimeterToleranceHpa { get; set; } = 0.5;

    /// <summary>V-speed read-back tolerance, knots.</summary>
    [OptionField(FieldKind.Number, "speechSettings.common.vSpeedTolerance", Suffix = "kt", Min = "0", Max = "10", DependsOn = "Enabled")]
    public int VSpeedToleranceKt { get; set; } = 1;

    /// <summary>Minimums read-back tolerance, feet.</summary>
    [OptionField(FieldKind.Number, "speechSettings.callouts.monitoring.minimums", Suffix = "ft", Min = "0", Max = "100", DependsOn = "Enabled")]
    public int MinimumsToleranceFt { get; set; } = 10;
}
