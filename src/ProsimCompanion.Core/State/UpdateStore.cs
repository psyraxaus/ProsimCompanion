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
public sealed class UpdateStore
{
    private readonly object _lock = new();
    private UpdateSnapshot _snapshot = UpdateSnapshot.Empty;

    public event EventHandler? Changed;

    public UpdateSnapshot Snapshot()
    {
        lock (_lock)
        {
            return _snapshot;
        }
    }

    public void Set(UpdateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_lock)
        {
            // A dismissal outlives subsequent checks for the same version — the banner must
            // not pop back every interval; a NEWER version un-dismisses.
            var dismissed = _snapshot.Dismissed
                && string.Equals(_snapshot.LatestVersion, snapshot.LatestVersion, StringComparison.Ordinal);
            _snapshot = snapshot with { Dismissed = dismissed };
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dismiss()
    {
        lock (_lock)
        {
            _snapshot = _snapshot with { Dismissed = true };
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
