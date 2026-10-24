namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Wind limits the performance pages, the briefings and the FO's wind check judge the reported
/// wind against (issue #182; the <c>aircraftLimits</c> section, Settings → Display &amp; Flight
/// Data → Aircraft limits). The defaults are the A320 family's PUBLISHED DEMONSTRATED values
/// (FCOM limitations: 38 kt crosswind gust included, 15 kt tailwind, 33 kt on a wet runway) and
/// the common RWYCC crosswind table — operators restrict them further, so every figure is
/// editable. Advisory only: nothing here stops a takeoff or a landing.
/// </summary>
public sealed class AircraftLimitsOptions : IOptionSection
{
    public static string SectionName => "aircraftLimits";

    [OptionField(FieldKind.Number, "appSettings.aircraftLimits.tailwindTakeoff", Suffix = "kt", Min = "0", Max = "30")]
    public int MaxTailwindTakeoffKt { get; set; } = 15;

    [OptionField(FieldKind.Number, "appSettings.aircraftLimits.tailwindLanding", Suffix = "kt", Min = "0", Max = "30")]
    public int MaxTailwindLandingKt { get; set; } = 15;

    [OptionField(FieldKind.Number, "appSettings.aircraftLimits.crosswindTakeoff", Suffix = "kt", Min = "0", Max = "60")]
    public int MaxCrosswindTakeoffKt { get; set; } = 38;

    [OptionField(FieldKind.Number, "appSettings.aircraftLimits.crosswindLanding", Suffix = "kt", Min = "0", Max = "60")]
    public int MaxCrosswindLandingKt { get; set; } = 38;

    /// <summary>Crosswind limit on a wet runway (takeoff and landing).</summary>
    [OptionField(FieldKind.Number, "appSettings.aircraftLimits.crosswindWet", Suffix = "kt", Min = "0", Max = "60")]
    public int MaxCrosswindWetKt { get; set; } = 33;

    // ---- Contaminated runway, by runway condition code (GRF RWYCC 5 … 1) ----

    [OptionField(FieldKind.Number, "appSettings.aircraftLimits.rwycc5", Suffix = "kt", Min = "0", Max = "60")]
    public int CrosswindRwycc5Kt { get; set; } = 25;

    [OptionField(FieldKind.Number, "appSettings.aircraftLimits.rwycc4", Suffix = "kt", Min = "0", Max = "60")]
    public int CrosswindRwycc4Kt { get; set; } = 20;

    [OptionField(FieldKind.Number, "appSettings.aircraftLimits.rwycc3", Suffix = "kt", Min = "0", Max = "60")]
    public int CrosswindRwycc3Kt { get; set; } = 15;

    [OptionField(FieldKind.Number, "appSettings.aircraftLimits.rwycc2", Suffix = "kt", Min = "0", Max = "60")]
    public int CrosswindRwycc2Kt { get; set; } = 10;

    [OptionField(FieldKind.Number, "appSettings.aircraftLimits.rwycc1", Suffix = "kt", Min = "0", Max = "60")]
    public int CrosswindRwycc1Kt { get; set; } = 5;
}
