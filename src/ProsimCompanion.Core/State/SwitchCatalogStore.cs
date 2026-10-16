using ProsimCompanion.Core.Aircraft.Switches;

namespace ProsimCompanion.Core.State;

/// <summary>
/// The switch list behind the smart-button picker (hybrid, owner pick 2026-10-17): starts as
/// the shipped CSV so the dropdown works with ProSim off, and is replaced by the live
/// dataref list the ProSim pillar reads on every connect. Observed by the GSX settings page.
/// </summary>
public sealed class SwitchCatalogStore : SnapshotStore<SwitchCatalogSnapshot>
{
    public SwitchCatalogStore() : base(SwitchCatalog.LoadShipped())
    {
    }
}
