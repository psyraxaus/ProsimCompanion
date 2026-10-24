using ProsimCompanion.Core.Flight;

namespace ProsimCompanion.Speech.Cabin;

/// <summary>The purser's own PAs (issue #186).</summary>
public enum PurserPaKind
{
    /// <summary>"Cabin crew, arm doors and cross-check" — the beacon-on edge before departure.</summary>
    ArmDoors,

    /// <summary>"Cabin crew, doors to manual" — engines off on stand after the flight.</summary>
    DoorsManual,

    /// <summary>"Cabin crew, prepare the cabin for landing" — 10,000 ft in the descent.</summary>
    PrepareLanding,
}

/// <summary>One tick's inputs.</summary>
public sealed record PurserPaSample(
    FlightPhase Phase,
    bool BeaconOn,
    bool EnginesOff,
    bool Parked,
    bool HasBeenAirborne,
    double AltitudeFt);

/// <summary>
/// Pure edge rules for the purser's PAs: each once per cycle, each on an edge (beacon OFF→ON on
/// the ground before the flight; engines running→off while parked after the flight; altitude
/// crossing 10,000 ft downwards in Descent/Approach). The first tick only primes the edges.
/// </summary>
public sealed class PurserPaCore
{
    public const double PrepareLandingAltFt = 10_000;

    private readonly HashSet<PurserPaKind> _done = [];
    private bool? _lastBeacon;
    private bool? _lastEnginesOff;
    private double? _lastAltitude;

    public IReadOnlySet<PurserPaKind> Done => _done;

    public PurserPaKind? Evaluate(PurserPaSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var beaconEdge = _lastBeacon == false && sample.BeaconOn;
        var enginesOffEdge = _lastEnginesOff == false && sample.EnginesOff;
        var crossed10k = _lastAltitude is { } previous && previous >= PrepareLandingAltFt && sample.AltitudeFt < PrepareLandingAltFt;
        _lastBeacon = sample.BeaconOn;
        _lastEnginesOff = sample.EnginesOff;
        _lastAltitude = sample.AltitudeFt;

        if (beaconEdge && !sample.HasBeenAirborne && sample.Phase.IsBeforeTaxiOut() && _done.Add(PurserPaKind.ArmDoors))
        {
            return PurserPaKind.ArmDoors;
        }

        if (enginesOffEdge && sample.Parked && sample.HasBeenAirborne && _done.Add(PurserPaKind.DoorsManual))
        {
            return PurserPaKind.DoorsManual;
        }

        if (crossed10k && sample.Phase is FlightPhase.Descent or FlightPhase.Approach && _done.Add(PurserPaKind.PrepareLanding))
        {
            return PurserPaKind.PrepareLanding;
        }

        return null;
    }

    /// <summary>A new cycle: the latches clear, the edges re-prime.</summary>
    public void Reset()
    {
        _done.Clear();
        _lastBeacon = null;
        _lastEnginesOff = null;
        _lastAltitude = null;
    }
}
