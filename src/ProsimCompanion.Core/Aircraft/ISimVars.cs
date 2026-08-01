namespace ProsimCompanion.Core.Aircraft;

/// <summary>
/// The seam for MSFS SimConnect simulation variables, mirroring the <see cref="IProsimDataRefs"/>
/// read model: subscribe once, read the cached value. Implemented by ProsimCompanion.Sim.
/// LVAR access is a separate transport decided at the start of Phase 2 (see ADR-0003 and
/// docs/integrations/gsx.md §5).
/// </summary>
public interface ISimVars
{
    /// <summary>
    /// Subscribes to a SimVar (e.g. "PLANE ALTITUDE") or LVAR ("L:FSDT_GSX_MENU_OPEN", unit
    /// "number") in the given unit. The first subscription for a name fixes its unit; the
    /// cadence tier maps to a SimConnect request period. Dispose the handle to release the
    /// registration. Note: the sim silently auto-creates unknown LVAR names as 0 — use the
    /// centralized names in <see cref="ProsimDataRefNames.Lvars"/>, never ad-hoc strings.
    /// </summary>
    IDataRefSubscription Subscribe(string simVarName, string unit, DataRefTier tier);

    /// <summary>
    /// Writes a value to an allow-listed SimVar/LVAR (unit "number", FLOAT64). After a write
    /// the sim echoes the value back on the next frame through the normal subscription stream —
    /// the echo is authoritative. Completes when the write has been handed to SimConnect.
    /// </summary>
    /// <exception cref="InvalidOperationException">The name is not on the sim write allow-list,
    /// or MSFS is not connected.</exception>
    Task WriteAsync(string simVarName, double value, CancellationToken cancellationToken = default);
}
