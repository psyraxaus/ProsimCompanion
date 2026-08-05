namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Flight-data pillar settings (SimBrief OFP, loadsheets, FMS sync) — the "flightData"
/// section of config/settings.json. Every default is safe for a missing section.
/// </summary>
public sealed class FlightDataOptions
{
    public const string SectionName = "flightData";

    /// <summary>Generate + uplink the preliminary loadsheet automatically when the GSX
    /// Refueling service goes active (predecessor behaviour).</summary>
    public bool AutoPrelimOnRefuel { get; set; } = true;

    /// <summary>Generate + uplink the final loadsheet automatically after boarding completes
    /// (following the crew-realism delay below).</summary>
    public bool AutoFinalOnBoardingComplete { get; set; } = true;

    /// <summary>Random delay window between boarding-complete and the final loadsheet, in
    /// seconds — models the dispatcher finalizing figures (predecessor defaults 90–150 s).</summary>
    public int FinalDelayMinSeconds { get; set; } = 90;

    public int FinalDelayMaxSeconds { get; set; } = 150;

    /// <summary>Push ZFW/ZFWCG/block into the MCDU INIT B automatically when the final
    /// loadsheet is produced. Off by default — the pilot keeps positive control.</summary>
    public bool AutoSyncFmsOnFinal { get; set; }

    /// <summary>SimBrief fetch attempts (transient failures were a known predecessor
    /// annoyance; a retry was always wanted).</summary>
    public int SimbriefFetchAttempts { get; set; } = 3;
}
