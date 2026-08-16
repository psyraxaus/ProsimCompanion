using ProsimCompanion.Core.Aircraft.WeightAndBalance;

namespace ProsimCompanion.Core.State;

public enum LoadsheetSlotStatus
{
    None,
    Generating,
    Sent,
    Failed,
}

/// <summary>One loadsheet slot (prelim or final) as the web UI shows it.</summary>
public sealed record LoadsheetSlotView(
    LoadsheetSlotStatus Status,
    int EditionNumber,
    DateTimeOffset? SentAtUtc,
    double ZfwKg,
    double TowKg,
    double MacZfw,
    double MacTow,
    double FuelKg,
    int Pax,
    string? Error);

/// <param name="StdOverrideUtc">Manually entered scheduled-departure time (UTC, time-of-day);
/// null means the OFP's STD applies. Drives the STD-offset prelim auto-trigger and the
/// Loadsheet page's "manual" source chip (Prosim2GSX parity).</param>
public sealed record LoadsheetSnapshot(
    LoadsheetSlotView Prelim,
    LoadsheetSlotView Final,
    TimeOnly? StdOverrideUtc = null)
{
    public static readonly LoadsheetSlotView EmptySlot =
        new(LoadsheetSlotStatus.None, 0, null, 0, 0, 0, 0, 0, 0, null);

    public static readonly LoadsheetSnapshot Empty = new(EmptySlot, EmptySlot);
}

/// <summary>
/// UI-facing status of the in-house loadsheet pipeline. Written by the loadsheet generator
/// service; read by the /flight page. <see cref="Changed"/> fires on the writer's thread.
/// </summary>
public sealed class LoadsheetStore : SnapshotStore<LoadsheetSnapshot>
{
    public LoadsheetStore()
        : base(LoadsheetSnapshot.Empty)
    {
    }

    public void SetPrelim(LoadsheetSlotView slot) => Update(snapshot => snapshot with { Prelim = slot });

    public void SetFinal(LoadsheetSlotView slot) => Update(snapshot => snapshot with { Final = slot });

    /// <summary>Sets (or clears, with null) the manual STD override. Survives a slot reset —
    /// clearing loadsheets does not forget the departure time the user typed.</summary>
    public void SetStdOverride(TimeOnly? stdUtc) => Update(snapshot => snapshot with { StdOverrideUtc = stdUtc });

    public void Reset() => Update(snapshot => LoadsheetSnapshot.Empty with { StdOverrideUtc = snapshot.StdOverrideUtc });

    /// <summary>Convenience projection from calculation output.</summary>
    public static LoadsheetSlotView SlotFrom(
        LoadsheetSlotStatus status, int edno, LoadsheetData data, DateTimeOffset? sentAt, string? error = null)
        => new(status, edno, sentAt, data.ZeroFuelWeight, data.TakeoffWeight,
            data.ZeroFuelWeightMac, data.TakeoffWeightMac, data.FuelWeight, data.TotalPassengers, error);
}

/// <summary>
/// Web-UI control seam for the loadsheet pipeline (implemented by the Prosim project's
/// generator service). All methods degrade-not-fail: false/logged on error.
/// </summary>
public interface ILoadsheetControl
{
    /// <summary>Generate (or resend — each call increments the edition number) the
    /// preliminary loadsheet from the current OFP + live state.</summary>
    Task<bool> GeneratePreliminaryAsync(CancellationToken cancellationToken = default);

    /// <summary>Generate (or resend) the final loadsheet. Requires a prior prelim.</summary>
    Task<bool> GenerateFinalAsync(CancellationToken cancellationToken = default);

    /// <summary>Clears cached loadsheets and returns the edition counter to 1.</summary>
    void ResetCycle();
}

/// <summary>Result of an FMS INIT sync push (values as written, in display units).</summary>
public sealed record FmsSyncResult(
    string Source,
    double ZfwTonnes,
    double ZfwCgPercent,
    double BlockTonnes);

/// <summary>
/// Pushes ZFW / ZFWCG / block fuel into the MCDU INIT B fields (implemented by the Prosim
/// project). Null result = nothing to sync or the writes failed (logged).
/// </summary>
public interface IFmsInitSync
{
    Task<FmsSyncResult?> SyncAsync(CancellationToken cancellationToken = default);
}
