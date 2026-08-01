namespace ProsimCompanion.Prosim.DataRefs;

/// <summary>
/// The transport a connected SDK session provides to <see cref="ProsimDataRefService"/>.
/// Attached on connect, detached on disconnect — the service works (serving stale cache,
/// rejecting writes) while no backend is attached.
/// </summary>
internal interface IDataRefBackend
{
    /// <summary>Registers (or re-registers at a faster cadence) a push subscription.</summary>
    void EnsureRegistered(string name, int intervalMs);

    /// <summary>Removes a push subscription.</summary>
    void Unregister(string name);

    /// <summary>Writes a value to a dataref. Called only after the write gate has passed.</summary>
    Task WriteValueAsync(string name, object? value, CancellationToken cancellationToken);
}
