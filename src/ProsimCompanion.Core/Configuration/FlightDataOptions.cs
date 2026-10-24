namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Flight-data pillar settings (SimBrief OFP, loadsheets, FMS sync) — the "flightData"
/// section of config/settings.json. Every default is safe for a missing section.
/// </summary>
public sealed class FlightDataOptions : IOptionSection
{
    public static string SectionName => "flightData";

    /// <summary>Generate + uplink the preliminary loadsheet automatically when the GSX
    /// Refueling service goes active (predecessor behaviour).</summary>
    [OptionField(FieldKind.Bool, "appSettings.loadsheets.prelimOnRefuel")]
    public bool AutoPrelimOnRefuel { get; set; } = true;

    /// <summary>Also auto-generate the prelim at STD minus <see cref="PrelimStdOffsetMinutes"/>
    /// (Prosim2GSX's timing model — STD from the OFP or the Loadsheet page's manual entry).
    /// Off by default; the refuel trigger already covers the common flow. Whichever trigger
    /// fires first wins — one automatic prelim per cycle either way.</summary>
    [OptionField(FieldKind.Bool, "appSettings.loadsheets.prelimAtStd")]
    public bool AutoPrelimAtStd { get; set; }

    /// <summary>Minutes before STD for the STD-based prelim trigger (Prosim2GSX default 30).</summary>
    [OptionField(FieldKind.Number, "appSettings.loadsheets.prelimOffset", Suffix = "min", Min = "0", DependsOn = "AutoPrelimAtStd")]
    public int PrelimStdOffsetMinutes { get; set; } = 30;

    /// <summary>Generate + uplink the final loadsheet automatically after boarding completes
    /// (following the crew-realism delay below).</summary>
    [OptionField(FieldKind.Bool, "appSettings.loadsheets.finalOnBoarding")]
    public bool AutoFinalOnBoardingComplete { get; set; } = true;

    /// <summary>Random delay window between boarding-complete and the final loadsheet, in
    /// seconds — models the dispatcher finalizing figures (predecessor defaults 90–150 s).</summary>
    [OptionField(FieldKind.Range, "appSettings.loadsheets.finalDelay", RangeEnd = nameof(FinalDelayMaxSeconds), Suffix = "s", Min = "0", DependsOn = "AutoFinalOnBoardingComplete")]
    public int FinalDelayMinSeconds { get; set; } = 90;

    [OptionField(FieldKind.RangeUpper, RangeStart = nameof(FinalDelayMinSeconds))]
    public int FinalDelayMaxSeconds { get; set; } = 150;

    /// <summary>Push ZFW/ZFWCG/block into the MCDU INIT B automatically when the final
    /// loadsheet is produced. Off by default — the pilot keeps positive control.</summary>
    [OptionField(FieldKind.Bool, "appSettings.loadsheets.syncFms")]
    public bool AutoSyncFmsOnFinal { get; set; }

    // ---- Last-minute changes (issue #181, 2026-10-24) ----

    /// <summary>Check the boarded load against the final loadsheet once boarding completes
    /// (issue #181): within the tolerances below the FO reads a last-minute change and an LMC
    /// block stands under the final; beyond them the captain is asked for a revised final.
    /// Only acts when a final already exists at boarding-complete.</summary>
    [OptionField(FieldKind.Bool, "appSettings.loadsheets.lmc")]
    public bool LmcEnabled { get; set; }

    /// <summary>Passengers the boarded count may differ from the final before a revised
    /// loadsheet is required.</summary>
    [OptionField(FieldKind.Number, "appSettings.loadsheets.lmcMaxPax", Min = "0", DependsOn = "LmcEnabled")]
    public int LmcMaxPax { get; set; } = 4;

    /// <summary>Zero-fuel or take-off weight difference allowed before a revised loadsheet is required.</summary>
    [OptionField(FieldKind.Number, "appSettings.loadsheets.lmcMaxWeight", Suffix = "kg", Min = "0", DependsOn = "LmcEnabled")]
    public int LmcMaxWeightKg { get; set; } = 300;

    /// <summary>MAC shift (percent) allowed before a revised loadsheet is required.</summary>
    [OptionField(FieldKind.Number, "appSettings.loadsheets.lmcMaxMacShift", Suffix = "%", Min = "0", Step = "0.1", DependsOn = "LmcEnabled")]
    public double LmcMaxMacShift { get; set; } = 0.5;

    /// <summary>Beyond tolerance, issue the revised final without asking the captain.</summary>
    [OptionField(FieldKind.Bool, "appSettings.loadsheets.lmcAutoRevise", DependsOn = "LmcEnabled")]
    public bool LmcAutoRevise { get; set; }

    /// <summary>SimBrief fetch attempts (transient failures were a known predecessor
    /// annoyance; a retry was always wanted).</summary>
    public int SimbriefFetchAttempts { get; set; } = 3;
}
