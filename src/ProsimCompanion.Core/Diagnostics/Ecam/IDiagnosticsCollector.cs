namespace ProsimCompanion.Core.Diagnostics.Ecam;

/// <summary>
/// One feature project's contribution to the diagnostics pages. Collectors are the only code
/// that knows what "degraded" means for their hop: they read their own stores (never another
/// feature's) and write nodes, links, gauges, readouts and cautions through the builders. A
/// collector contributes to ONE page (<see cref="Page"/>); several collectors may share a page
/// and the snapshot service merges them. A throwing collector costs its own nodes (they render
/// amber XX), never the page.
/// </summary>
public interface IDiagnosticsCollector
{
    /// <summary>One of <see cref="DiagPageIds.Drawing"/>.</summary>
    string Page { get; }

    /// <summary>Called once per tick on the snapshot service's thread. Must be cheap: read
    /// cached store snapshots, never a network round-trip.</summary>
    void Contribute(DiagPageBuilder page, DiagCautionSink cautions);
}
