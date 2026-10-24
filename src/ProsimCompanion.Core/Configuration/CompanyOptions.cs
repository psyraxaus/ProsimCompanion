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

    // ---- Movement messages and delay codes (issue #185) ----

    /// <summary>Send the IATA movement messages (MVT) to the company at the OFF and IN edges:
    /// AD out/off with the EA at takeoff, AA on/in at on-blocks, the DL code/minutes suffix when
    /// a delay code is on record. Into the cockpit RCVD MSGS over ACARS when ProSim is there.</summary>
    [OptionField(FieldKind.Bool, "speechSettings.company.movementMessages", DependsOn = "Enabled")]
    public bool MovementMessages { get; set; }

    /// <summary>Also say "company copies, movement message sent" when one goes out. Silent by default.</summary>
    [OptionField(FieldKind.Bool, "speechSettings.company.movementMessagesSpoken", DependsOn = "Enabled; MovementMessages")]
    public bool MovementMessagesSpoken { get; set; }

    /// <summary>At off-blocks, when the aircraft is later than the schedule plus the tolerance,
    /// the FO asks for the IATA delay code (pick one from delay-codes.json; a timeout records 99).</summary>
    [OptionField(FieldKind.Bool, "speechSettings.company.delayCodes", DependsOn = "Enabled")]
    public bool DelayCodes { get; set; }

    /// <summary>Minutes past the scheduled departure before a flight counts as late — the
    /// industry on-time-performance threshold (15).</summary>
    [OptionField(FieldKind.Number, "speechSettings.company.delayTolerance", Suffix = "min", Min = "0", Max = "120", DependsOn = "Enabled")]
    public int DelayToleranceMinutes { get; set; } = 15;
}
