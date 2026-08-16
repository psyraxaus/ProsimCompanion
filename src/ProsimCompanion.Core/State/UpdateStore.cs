namespace ProsimCompanion.Core.State;

/// <summary>Result of the last successful update check. <see cref="UpdateAvailable"/> already
/// accounts for the version comparison; <see cref="Dismissed"/> is a per-run flag set from the
/// banner's close button (not persisted — a new run re-announces).</summary>
public sealed record UpdateSnapshot(
    bool UpdateAvailable,
    string CurrentVersion,
    string LatestVersion,
    string ReleaseUrl,
    bool Dismissed)
{
    public static readonly UpdateSnapshot Empty = new(false, "", "", "", false);
}

/// <summary>
/// Holds the update-check outcome for the web layout's banner. Written by the update check
/// service; <see cref="Changed"/> fires on the writer's thread — UI consumers marshal.
/// </summary>
public sealed class UpdateStore : SnapshotStore<UpdateSnapshot>
{
    public UpdateStore()
        : base(UpdateSnapshot.Empty)
    {
    }

    public void Set(UpdateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        // A dismissal outlives subsequent checks for the same version — the banner must
        // not pop back every interval; a NEWER version un-dismisses.
        Update(current => snapshot with
        {
            Dismissed = current.Dismissed
                && string.Equals(current.LatestVersion, snapshot.LatestVersion, StringComparison.Ordinal),
        });
    }

    public void Dismiss() => Update(current => current with { Dismissed = true });
}
