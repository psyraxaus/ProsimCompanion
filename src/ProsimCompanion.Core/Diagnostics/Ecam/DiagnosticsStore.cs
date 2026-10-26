using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Diagnostics.Ecam;

/// <summary>The published diagnostics snapshot, replaced once per tick by
/// <see cref="DiagnosticsSnapshotService"/>; the Blazor pages, the nav badge, the API and the
/// bundle builder all read it here.</summary>
public sealed class DiagnosticsStore : SnapshotStore<DiagnosticsSnapshot>
{
    public DiagnosticsStore()
        : base(DiagnosticsSnapshot.Empty)
    {
    }

    /// <summary>Replaces the snapshot outright.</summary>
    public void Publish(DiagnosticsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Update(_ => snapshot);
    }
}
