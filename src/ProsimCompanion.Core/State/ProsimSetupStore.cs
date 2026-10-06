using ProsimCompanion.Core.Aircraft.Setup;

namespace ProsimCompanion.Core.State;

/// <summary>What the ProSim setup check last saw.</summary>
/// <param name="Items">One row per recommended IOS option (empty until the first read, or
/// when the check is switched off).</param>
/// <param name="ProsimVersion">ProSim's own version string (<c>system.version</c>), null when
/// not read.</param>
/// <param name="CheckedAtUtc">When the rows were last read from ProSim; null before the first read.</param>
/// <param name="Busy">True while a read or an apply is in flight (the card disables its buttons).</param>
public sealed record ProsimSetupSnapshot(
    IReadOnlyList<ProsimSetupItem> Items,
    string? ProsimVersion,
    DateTimeOffset? CheckedAtUtc,
    bool Busy)
{
    public static ProsimSetupSnapshot Empty { get; } = new([], null, null, false);

    /// <summary>Rows that differ from the recommendation and are not ignored.</summary>
    public int MismatchCount => Items.Count(item => item.Status == ProsimSetupStatus.Mismatch);

    /// <summary>True once a read has happened.</summary>
    public bool HasReading => CheckedAtUtc is not null;
}

/// <summary>
/// Observable result of the ProSim setup check (owner request 2026-10-06): written by the
/// ProSim pillar's check service after each read or apply, read by the Setup page card and
/// the Getting Started card. Record equality over the row list compares by reference, so a
/// fresh read with identical values still notifies — the card shows the new "checked at" time.
/// </summary>
public sealed class ProsimSetupStore : SnapshotStore<ProsimSetupSnapshot>
{
    public ProsimSetupStore() : base(ProsimSetupSnapshot.Empty)
    {
    }
}

/// <summary>Actions the Setup page card can ask of the check service.</summary>
public interface IProsimSetupCheck
{
    /// <summary>Reads every recommended option from ProSim again and updates the store.
    /// Returns false when ProSim is not connected (the store is then left as it was).</summary>
    Task<bool> RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>Writes the recommended value to every mismatched, non-ignored row, then reads
    /// the rows back. Only ever runs on a click — never on its own.</summary>
    Task<ProsimSetupApplyResult> ApplyRecommendedAsync(CancellationToken cancellationToken = default);
}
