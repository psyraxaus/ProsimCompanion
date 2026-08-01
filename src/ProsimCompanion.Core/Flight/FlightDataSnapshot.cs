namespace ProsimCompanion.Core.Flight;

/// <summary>
/// One sampled view of the aircraft, taken every engine tick. This record is the single seam
/// between data sources and phase derivation — a replay source can substitute the live one.
/// The "valid or hold previous decision" rule applies: when <see cref="IsValid"/> is false the
/// engine keeps its current phase rather than reacting to garbage.
/// </summary>
public sealed record FlightDataSnapshot
{
    /// <summary>False while the source is disconnected or values are stale.</summary>
    public bool IsValid { get; init; }

    public bool OnGround { get; init; }
    public double IndicatedAirspeedKt { get; init; }
    public double GroundSpeedKt { get; init; }
    public double AltitudeFt { get; init; }
    public double RadioAltitudeFt { get; init; }
    public double VerticalSpeedFpm { get; init; }

    /// <summary>Electrical power available (battery/external) — cold-and-dark discriminator.</summary>
    public bool AircraftPowered { get; init; }

    public bool AnyEngineRunning { get; init; }
    public bool EngineStarting { get; init; }
    public bool PushbackActive { get; init; }
    public bool ParkBrakeSet { get; init; }
    public bool GearDown { get; init; }

    /// <summary>Take-off thrust (FLEX/TOGA) commanded.</summary>
    public bool TakeoffThrustSet { get; init; }
}

/// <summary>
/// Supplies <see cref="FlightDataSnapshot"/>s to the flight state engine. Implementations:
/// the live ProSim dataref source; later a replay source over the JSONL event log.
/// </summary>
public interface IFlightDataSource
{
    /// <summary>Returns the current sample. Must be cheap (cached reads, never a network call).</summary>
    FlightDataSnapshot Sample();
}
