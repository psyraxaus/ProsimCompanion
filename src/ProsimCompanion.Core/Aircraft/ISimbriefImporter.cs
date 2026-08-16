namespace ProsimCompanion.Core.Aircraft;

public enum SimbriefImportOutcome
{
    Imported,
    AlreadyImported,

    /// <summary>No SimBrief pilot id in ProSim (efb.simbrief.id empty).</summary>
    NoPilotId,

    FetchFailed,
    ImportFailed,
}

/// <summary>
/// Fetches the SimBrief OFP and imports it into ProSim's EFB the way the predecessor did —
/// writing the booked seat map, passenger statistics, planned fuel (as the refuel target
/// total), planned cargo and the simbriefPlanImported flag. This is what makes seat-accurate
/// boarding and correct refuel targets possible.
/// </summary>
public interface ISimbriefImporter
{
    /// <param name="force">Re-fetch and re-import even when ProSim already reports the plan
    /// imported (the web UI's manual fetch button — picks up an OFP regenerated on SimBrief).
    /// A forced import also re-rolls any pax randomization (issue #64).</param>
    /// <param name="source">Who triggered this import ("automation", "web efb-init", …) —
    /// logged per attempt so a re-import storm is diagnosable from the log alone (issue #64:
    /// six imports in eight minutes with no way to tell which caller fired them).</param>
    Task<SimbriefImportOutcome> TryImportAsync(bool force = false, string source = "unspecified", CancellationToken cancellationToken = default);
}
