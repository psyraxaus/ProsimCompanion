using ProsimCompanion.Core.Aircraft.WeightAndBalance;

namespace ProsimCompanion.Core.State;

public enum LoadsheetSlotStatus
{
    None,
    Generating,
    Sent,
    Failed,

    /// <summary>An automatic trigger fired but a precondition (OFP, CG datarefs) is not there
    /// yet — the trigger is armed and re-checking; <c>Error</c> carries what it waits for.</summary>
    Waiting,
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

/// <summary>What became of a last-minute change (issue #181).</summary>
public enum LmcDisposition
{
    /// <summary>No difference against the final — shown only after an on-demand check.</summary>
    NoChange,

    /// <summary>Within tolerance: the LMC block stands under the final, applied by hand.</summary>
    Applied,

    /// <summary>Beyond tolerance; the captain's "issue a revised final?" question is open.</summary>
    AwaitingDecision,

    /// <summary>Beyond tolerance; a revised final (EDNO +1) was issued.</summary>
    RevisionIssued,

    /// <summary>Beyond tolerance; the captain declined the revision (or the question timed
    /// out) and flies on the LMC block with the caption.</summary>
    FlownOnCaptainsAuthority,
}

/// <summary>The last-minute change on the current final (issue #181), as the Loadsheet page
/// and the FO read it.</summary>
/// <param name="Result">The evaluation: deltas, new figures, verdict.</param>
/// <param name="Disposition">What became of it.</param>
/// <param name="AgainstEdition">The final's edition the change was measured against.</param>
/// <param name="EvaluatedAtUtc">When.</param>
public sealed record LoadsheetLmc(LmcResult Result, LmcDisposition Disposition, int AgainstEdition, DateTimeOffset EvaluatedAtUtc);

/// <param name="StdOverrideUtc">Manually entered scheduled-departure time (UTC, time-of-day);
/// null means the OFP's STD applies. Drives the STD-offset prelim auto-trigger and the
/// Loadsheet page's "manual" source chip (Prosim2GSX parity).</param>
/// <param name="Lmc">The last-minute change against the final, null until one was evaluated
/// this cycle (issue #181).</param>
public sealed record LoadsheetSnapshot(
    LoadsheetSlotView Prelim,
    LoadsheetSlotView Final,
    TimeOnly? StdOverrideUtc = null,
    LoadsheetLmc? Lmc = null)
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

    /// <summary>Sets (or clears, with null) the last-minute change on the final (issue #181).</summary>
    public void SetLmc(LoadsheetLmc? lmc) => Update(snapshot => snapshot with { Lmc = lmc });

    /// <summary>Changes what became of the standing LMC; a no-op without one.</summary>
    public void SetLmcDisposition(LmcDisposition disposition)
        => Update(snapshot => snapshot.Lmc is { } lmc ? snapshot with { Lmc = lmc with { Disposition = disposition } } : snapshot);

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

    /// <summary>Evaluates the last-minute change against the final loadsheet now (issue #181)
    /// — the on-demand path behind <c>loadsheet.lmc</c> and the page button; ignores the
    /// once-per-cycle latch. False when no final exists or the live CG is implausible.</summary>
    Task<bool> EvaluateLastMinuteChangeAsync(CancellationToken cancellationToken = default);

    /// <summary>Issues a revised final after a last-minute change (issue #181): the existing
    /// final path with the edition bumped by one and a REVISION line. Requires a prior final.</summary>
    Task<bool> GenerateRevisedFinalAsync(CancellationToken cancellationToken = default);
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
