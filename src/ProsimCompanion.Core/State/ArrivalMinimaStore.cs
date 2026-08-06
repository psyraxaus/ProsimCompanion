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
public sealed class ArrivalMinimaStore
{
    private readonly object _gate = new();
    private ArrivalMinima? _minima;

    /// <summary>Raised after any change, on the writer's thread.</summary>
    public event EventHandler? Changed;

    public ArrivalMinima? Current
    {
        get
        {
            lock (_gate)
            {
                return _minima;
            }
        }
    }

    public void Set(ArrivalMinima minima)
    {
        ArgumentNullException.ThrowIfNull(minima);

        lock (_gate)
        {
            _minima = minima;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _minima = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
