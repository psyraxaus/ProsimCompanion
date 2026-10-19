namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Company / ACARS-style channel (Prosim2FO "Prompt F" semantics): a spoken loadsheet readout
/// from real weight/pax/CG datarefs (ready once the final loadsheet lands in
/// efb.finalLoadsheet, else once weights are populated) and optional deterministic mid-cruise
/// company messages. The FO reads the loadsheet (the FO reads the numbers to the captain);
/// company messages get the ACARS double-beep. A PDC/clearance readout stays out of scope —
/// no squawk / cleared-altitude source exists.
/// </summary>
public sealed class CompanyOptions : IOptionSection
{
    public static string SectionName => "company";

    [OptionField(FieldKind.Bool, "speechSettings.common.enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Deliver the loadsheet automatically pre-departure once ready (else only on
    /// "request loadsheet" or the web button).</summary>
    [OptionField(FieldKind.Bool, "speechSettings.company.autoLoadsheet", DependsOn = "Enabled")]
    public bool AutoLoadsheet { get; set; }

    /// <summary>Persist the spoken loadsheet (plus the raw EFB loadsheet when present) beside
    /// the JSONL session log as *.loadsheet.txt.</summary>
    [OptionField(FieldKind.Bool, "speechSettings.company.persist", DependsOn = "Enabled", Advanced = true)]
    public bool PersistLoadsheet { get; set; } = true;

    /// <summary>Optional mid-cruise company message (deterministic template around destination
    /// facts; LLM styling may layer on later). Off by default.</summary>
    [OptionField(FieldKind.Bool, "speechSettings.company.cruiseMessages", DependsOn = "Enabled")]
    public bool CruiseMessages { get; set; }

    /// <summary>Probability (0–1) of a cruise company message, rolled once per flight.</summary>
    [OptionField(FieldKind.Number, "speechSettings.company.cruiseProb", Step = "0.05", Min = "0", Max = "1", DependsOn = "Enabled; CruiseMessages")]
    public double CruiseMessageProbability { get; set; } = 0.15;

    /// <summary>Play the ACARS-style double-beep before company deliveries.</summary>
    [OptionField(FieldKind.Bool, "speechSettings.company.chime", DependsOn = "Enabled")]
    public bool Chime { get; set; } = true;
}
