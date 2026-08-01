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
    /// Subscribes to a SimVar (e.g. "PLANE ALTITUDE") in the given unit (e.g. "feet").
    /// The first subscription for a name fixes its unit; the cadence tier maps to a SimConnect
    /// request period. Dispose the handle to release the registration.
    /// </summary>
    IDataRefSubscription Subscribe(string simVarName, string unit, DataRefTier tier);
}
