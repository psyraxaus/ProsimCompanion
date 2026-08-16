namespace ProsimCompanion.Core.State;

/// <summary>How the arrival minimum is referenced. DA/MDA are MSL (checked against baro
/// altitude); DH is a radio-altimeter height.</summary>
public enum ArrivalMinimumKind
{
    DecisionAltitude,
    DecisionHeight,
    MinimumDescentAltitude,
}

/// <summary>Crew-entered arrival minima in feet.</summary>
public sealed record ArrivalMinima(ArrivalMinimumKind Kind, double AltitudeFt);

/// <summary>
/// The briefed/confirmed arrival minima for this flight. There is NO DH/MDA dataref in ProSim,
/// and minima must never be guessed — the "minimums"/"one hundred above" callouts stay
/// disabled (with a logged reason) until the crew enters them here (/speech page; later the
/// briefing flow). Cleared on turnaround by the crew or a new entry.
/// </summary>
public sealed class ArrivalMinimaStore : SnapshotStore<ArrivalMinima?>
{
    public ArrivalMinimaStore()
        : base(null)
    {
    }

    public ArrivalMinima? Current => Snapshot();

    public void Set(ArrivalMinima minima)
    {
        ArgumentNullException.ThrowIfNull(minima);
        Update(_ => minima);
    }

    public void Clear() => Update(_ => null);
}
