using ProsimCompanion.Core.Aircraft.Ofp;

namespace ProsimCompanion.Core.State;

/// <summary>Why the crew is diverting (issue #184).</summary>
public enum DiversionReason
{
    Weather,
    Technical,
    Medical,
    Fuel,
    Other,
}

/// <summary>The diversion for this flight (issue #184). <see cref="Active"/> false = the OFP's
/// destination stands.</summary>
/// <param name="Active">A diversion is declared.</param>
/// <param name="NewDestinationIcao">Where the aircraft is going now.</param>
/// <param name="NewDestinationName">Its spoken name, when known.</param>
/// <param name="OriginalDestinationIcao">The OFP's destination the flight was planned to.</param>
/// <param name="OriginalDestinationName">Its SimBrief name.</param>
/// <param name="Reason">Why.</param>
/// <param name="DeclaredAtUtc">When (flight clock).</param>
/// <param name="Offers">The FO's standing offers after the declaration ("arrival briefing", "rad nav", "atc request"), for the card.</param>
public sealed record DiversionSnapshot(
    bool Active,
    string? NewDestinationIcao,
    string? NewDestinationName,
    string? OriginalDestinationIcao,
    string? OriginalDestinationName,
    DiversionReason? Reason,
    DateTimeOffset? DeclaredAtUtc,
    IReadOnlyList<string> Offers)
{
    public static DiversionSnapshot None { get; } = new(false, null, null, null, null, null, null, []);
}

/// <summary>
/// The diversion for THIS flight (issue #184) and the one seam every destination consumer
/// already reads: declaring a diversion overlays the OFP in <see cref="OfpStore"/> (the
/// destination, its name; the navlog, planned arrival runway and alternate dropped because they
/// belong to the old destination), so flight progress, the weather cards and watch, the
/// briefing's OFP tier, the arrival-gate chain, the fuel check's fallback and the day engine
/// follow the aircraft through the <see cref="OfpStore.Changed"/> edge they already observe.
/// Cancelling restores the planned OFP. Resets with the flight cycle.
/// </summary>
public sealed class DiversionStore : SnapshotStore<DiversionSnapshot>, IDisposable
{
    private readonly OfpStore _ofp;
    private readonly GroundOpsSignals _signals;

    public DiversionStore(OfpStore ofp, GroundOpsSignals signals)
        : base(DiversionSnapshot.None)
    {
        ArgumentNullException.ThrowIfNull(ofp);
        ArgumentNullException.ThrowIfNull(signals);
        _ofp = ofp;
        _signals = signals;
        _signals.FlightCycleReset += OnFlightCycleReset;
    }

    public DiversionSnapshot Current => Snapshot();

    /// <summary>Declares (or re-declares, to another airport) the diversion and overlays the
    /// OFP. False when the ICAO is blank or equals the planned destination.</summary>
    public bool Declare(string icao, string? name, DiversionReason reason, DateTimeOffset nowUtc)
    {
        var id = (icao ?? "").Trim().ToUpperInvariant();
        var planned = _ofp.Planned;
        if (id.Length != 4 || string.Equals(id, planned?.DestinationIcao, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _ofp.ApplyDiversion(id, name);
        Update(s => new DiversionSnapshot(
            true, id, name,
            s.Active ? s.OriginalDestinationIcao : planned?.DestinationIcao,
            s.Active ? s.OriginalDestinationName : planned?.DestinationName,
            reason, nowUtc, []));
        return true;
    }

    /// <summary>Back to the planned destination. False when none was active.</summary>
    public bool Cancel()
    {
        if (!Current.Active)
        {
            return false;
        }

        _ofp.ClearDiversion();
        Update(_ => DiversionSnapshot.None);
        return true;
    }

    /// <summary>Records a standing offer of the FO's ("arrival briefing", "rad nav", "atc request") for the card.</summary>
    public void AddOffer(string offer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(offer);
        Update(s => s.Active && !s.Offers.Contains(offer, StringComparer.Ordinal) ? s with { Offers = [.. s.Offers, offer] } : s);
    }

    private void OnFlightCycleReset()
    {
        if (Current.Active)
        {
            _ofp.ClearDiversion();
        }

        Update(_ => DiversionSnapshot.None);
    }

    public void Dispose()
    {
        _signals.FlightCycleReset -= OnFlightCycleReset;
        GC.SuppressFinalize(this);
    }
}
