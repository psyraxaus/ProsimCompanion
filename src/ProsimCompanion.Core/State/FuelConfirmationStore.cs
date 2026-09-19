using ProsimCompanion.Core.Aircraft.Ofp;

namespace ProsimCompanion.Core.State;

/// <summary>The pilot's fuel-figure confirmation for the current departure cycle.</summary>
/// <param name="Confirmed">True once the crew confirmed the block fuel (INIT page button,
/// "fuel confirmed" by voice, a direct refuel request, or the command API).</param>
/// <param name="ConfirmedKg">The figure that was confirmed (rounded up to 100 kg).</param>
/// <param name="ConfirmedAtUtc">When it was confirmed.</param>
/// <param name="Source">Which surface confirmed it, for the decision log.</param>
public sealed record FuelConfirmationSnapshot(
    bool Confirmed,
    double ConfirmedKg,
    DateTimeOffset? ConfirmedAtUtc,
    string? Source)
{
    public static FuelConfirmationSnapshot Empty { get; } = new(false, 0, null, null);
}

/// <summary>
/// Real-world SOP seam (2026-09-19): with <c>gsx.refuelCall = "onFuelConfirmed"</c> the
/// departure sequence holds the Refueling step until the crew confirms the block fuel — the
/// OFP figure is a proposal the captain adjusts after their own considerations, and the
/// truck must not be ordered on the proposal. Written by the GSX service control (every
/// confirmation path funnels through it), read by the departure automation's hold rule and
/// the web pages. Resets on the flight-cycle reset and whenever a NEW OFP arrives (a new
/// plan is a new proposal; a re-import of the same OFP keeps the confirmation).
/// </summary>
public sealed class FuelConfirmationStore : SnapshotStore<FuelConfirmationSnapshot>, IDisposable
{
    private readonly OfpStore _ofpStore;
    private readonly GroundOpsSignals _signals;
    private string? _lastOfpRequestId;

    public FuelConfirmationStore(OfpStore ofpStore, GroundOpsSignals signals)
        : base(FuelConfirmationSnapshot.Empty)
    {
        ArgumentNullException.ThrowIfNull(ofpStore);
        ArgumentNullException.ThrowIfNull(signals);
        _ofpStore = ofpStore;
        _signals = signals;
        _lastOfpRequestId = ofpStore.Current?.RequestId;
        _ofpStore.Changed += OnOfpChanged;
        _signals.FlightCycleReset += Reset;
    }

    public void Dispose()
    {
        _ofpStore.Changed -= OnOfpChanged;
        _signals.FlightCycleReset -= Reset;
    }

    /// <summary>True once confirmed this cycle.</summary>
    public bool Confirmed => Snapshot().Confirmed;

    /// <summary>Records the confirmation (idempotent for the same figure; a re-confirmation
    /// with a new figure replaces it — the crew changed their mind before the truck came).</summary>
    public void Confirm(double kg, string source)
        => Update(_ => new FuelConfirmationSnapshot(true, kg, DateTimeOffset.UtcNow, source));

    /// <summary>Clears the confirmation (new cycle / new OFP).</summary>
    public void Reset() => Update(_ => FuelConfirmationSnapshot.Empty);

    private void OnOfpChanged(object? sender, EventArgs e)
    {
        var requestId = _ofpStore.Current?.RequestId;
        if (string.Equals(requestId, _lastOfpRequestId, StringComparison.Ordinal))
        {
            return;
        }
        _lastOfpRequestId = requestId;
        Reset();
    }
}
