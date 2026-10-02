namespace ProsimCompanion.Core.State;

/// <summary>The last takeoff performance calculation (issue #148). Null fields = the
/// calculator did not supply them (a TOGA result has no flex).</summary>
/// <param name="FlapsConf">CONF as the FMS codes it: 1 = 1+F, 2, 3.</param>
/// <param name="FlexTempC">Flex temperature, null for TOGA.</param>
/// <param name="TowKg">The takeoff weight the figures were computed for.</param>
/// <param name="Runway">Runway the figures are for ("27R"), when known.</param>
public sealed record TakeoffPerfSnapshot(
    int FlapsConf,
    int? FlexTempC,
    int V1,
    int Vr,
    int V2,
    double? TowKg,
    string? Runway,
    DateTimeOffset CalculatedAtUtc);

/// <summary>
/// Remembers the last takeoff performance result so the gross-error check can compare the FMS
/// PERF page against it (issue #148). Nothing stored a result before: the Takeoff page kept it
/// in its own component state and the FMS uplink wrote it and forgot it. Written by the
/// Takeoff page on every successful calculation; cleared by the flight-cycle reset.
/// </summary>
public sealed class TakeoffPerfStore : SnapshotStore<TakeoffPerfSnapshot?>
{
    public TakeoffPerfStore()
        : base(null)
    {
    }

    public void Set(TakeoffPerfSnapshot result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Update(_ => result);
    }

    public void Clear() => Update(_ => null);
}
