namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Scenario mode (issue #197, <c>scenario</c>): rarely, opt-in, the app arms one of the
/// catalogue's minor failures in ProSim at a scheduled moment so the FO's ECAM dialogue and the
/// decision discussion get used. The write path is the ProSim SDK's failure API behind its own
/// gate (ADR-0018) — never a dataref, never ProSim's own random-failure generator. Off by default.
/// </summary>
public sealed class ScenarioOptions : IOptionSection
{
    public static string SectionName => "scenario";

    /// <summary>Master switch. Off: nothing is planned, nothing is armed, "clear all failures"
    /// still works.</summary>
    [OptionField(FieldKind.Bool, "speechSettings.scenario.enabled", Label = "speechSettings.common.enabled")]
    public bool Enabled { get; set; }

    /// <summary>Probability (0–1) that a flight gets a scenario — 0.10 is about one event every ten flights.</summary>
    [OptionField(FieldKind.Number, "speechSettings.scenario.perFlightChance", Min = "0", Max = "1", Step = "0.05", DependsOn = "Enabled")]
    public double PerFlightChance { get; set; } = 0.10;

    /// <summary>Let the major entries (engine fire / failure, APU fire) be drawn.</summary>
    [OptionField(FieldKind.Bool, "speechSettings.scenario.allowMajor", DependsOn = "Enabled")]
    public bool AllowMajor { get; set; }

    /// <summary>The two takeoff scenarios (an RTO cause, an engine failure after V1) — their
    /// own switch, and only with the RTO / EFATO features on.</summary>
    [OptionField(FieldKind.Bool, "speechSettings.scenario.takeoffScenarios", DependsOn = "Enabled")]
    public bool TakeoffScenarios { get; set; }

    /// <summary>Nothing is armed in flight below this height above ground, feet.</summary>
    [OptionField(FieldKind.Number, "speechSettings.scenario.minAltitudeFt", Suffix = "ft", Min = "500", Max = "10000", Step = "100", DependsOn = "Enabled")]
    public int MinAltitudeFt { get; set; } = 1500;

    /// <summary>Show the planned scenario on the STATUS page and answer "any failures planned?"
    /// — for testing; off keeps the surprise.</summary>
    [OptionField(FieldKind.Bool, "speechSettings.scenario.showPlanned", DependsOn = "Enabled")]
    public bool ShowPlanned { get; set; }
}
