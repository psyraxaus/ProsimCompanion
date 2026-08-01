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
    Task<SimbriefImportOutcome> TryImportAsync(CancellationToken cancellationToken = default);
}
