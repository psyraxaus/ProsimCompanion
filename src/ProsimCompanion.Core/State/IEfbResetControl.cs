namespace ProsimCompanion.Core.State;

/// <summary>Which reset ran (the <c>efb-reset</c> session event's <c>kind</c>).</summary>
public enum EfbResetKind
{
    /// <summary>App-side only: INIT overrides and the loadsheet cycle. The aircraft and the
    /// ProSim EFB keep the OFP — the leg restarts on the same plan (ProsimInterface's
    /// <c>ResetFlight()</c>).</summary>
    Soft,

    /// <summary>The OFP is unloaded from the ProSim EFB (seat map, passenger statistics,
    /// planned fuel/cargo, loadsheet slots, the imported flag) AND every app-side flight-cycle
    /// store is reset through the turnaround path — the state before any OFP was fetched
    /// (ProsimInterface's <c>UnloadOfp()</c>).</summary>
    Full,
}

/// <summary>Outcome of a reset: what was cleared (dataref names and store names) and what the
/// ProSim EFB refused — a partial full reset is reported, never hidden.</summary>
public sealed record EfbResetResult(
    EfbResetKind Kind,
    IReadOnlyList<string> Cleared,
    IReadOnlyList<string> Failed)
{
    /// <summary>True when nothing was refused.</summary>
    public bool Ok => Failed.Count == 0;
}

/// <summary>
/// The two EFB reset flows of the INIT page and the <c>efb.resetFlight</c> /
/// <c>efb.unloadOfp</c> commands (implemented by the Prosim project). Degrade-not-fail: a
/// ProSim that is absent or refuses a write shows up in <see cref="EfbResetResult.Failed"/>,
/// the app-side part still runs.
/// </summary>
public interface IEfbResetControl
{
    /// <summary>Soft reset — INIT overrides cleared, loadsheet cycle back to edition 1. No
    /// dataref is written to the ProSim EFB.</summary>
    Task<EfbResetResult> ResetFlightAsync(CancellationToken cancellationToken = default);

    /// <summary>Full reset — the OFP unloaded from the ProSim EFB and the flight-cycle stores
    /// reset, so no GSX service is called until a new OFP arrives.</summary>
    Task<EfbResetResult> UnloadOfpAsync(CancellationToken cancellationToken = default);
}
