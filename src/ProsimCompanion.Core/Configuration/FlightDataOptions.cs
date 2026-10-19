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

    /// <summary>SimBrief fetch attempts (transient failures were a known predecessor
    /// annoyance; a retry was always wanted).</summary>
    public int SimbriefFetchAttempts { get; set; } = 3;
}
