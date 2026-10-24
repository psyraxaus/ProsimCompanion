namespace ProsimCompanion.Core.State;

/// <summary>The runway condition the crew selected for the landing (issue #182): dry, wet, or
/// a GRF runway condition code 5 … 1. The ProSim landing calculator takes the same scale as its
/// <c>BreakAction</c> (6 = dry … 1 = poor); "wet" rides as code 5 for the calculation but
/// carries the wet-runway crosswind limit.</summary>
public enum RunwayCondition
{
    Dry,
    Wet,
    Rwycc5,
    Rwycc4,
    Rwycc3,
    Rwycc2,
    Rwycc1,
}

/// <summary>Pure mappings of <see cref="RunwayCondition"/> (issue #182).</summary>
public static class RunwayConditions
{
    /// <summary>The ProSim landing calculator's <c>BreakAction</c> code for a condition.</summary>
    public static int BreakAction(RunwayCondition condition) => condition switch
    {
        RunwayCondition.Dry => 6,
        RunwayCondition.Wet => 5,
        RunwayCondition.Rwycc5 => 5,
        RunwayCondition.Rwycc4 => 4,
        RunwayCondition.Rwycc3 => 3,
        RunwayCondition.Rwycc2 => 2,
        _ => 1,
    };

    /// <summary>The GRF code the condition stands for: 6 dry, 5 wet / good … 1 poor.</summary>
    public static int Rwycc(RunwayCondition condition) => condition == RunwayCondition.Wet ? 5 : BreakAction(condition);

    /// <summary>The spoken name ("dry", "wet", "runway condition code 3").</summary>
    public static string Spoken(RunwayCondition condition) => condition switch
    {
        RunwayCondition.Dry => "dry",
        RunwayCondition.Wet => "wet",
        _ => $"runway condition code {Rwycc(condition)}",
    };

    /// <summary>The limit name used in the wind verdict ("dry", "wet-runway", "RWYCC 3").</summary>
    public static string LimitName(RunwayCondition? condition) => condition switch
    {
        null or RunwayCondition.Dry => "dry",
        RunwayCondition.Wet => "wet-runway",
        var c => $"RWYCC {Rwycc(c!.Value)}",
    };
}

/// <summary>
/// The landing runway condition for THIS flight (issue #182): set on the Landing page's
/// selector (and seeded back into it), read by the wind-limit check, the arrival briefing's
/// wind clause, the FO's "runway condition" answer and the approach advisory. Null = not
/// selected (dry limits apply, said as such). Resets with the flight cycle.
/// </summary>
public sealed class RunwayConditionStore : SnapshotStore<RunwayCondition?>, IDisposable
{
    private readonly GroundOpsSignals _signals;

    public RunwayConditionStore(GroundOpsSignals signals)
        : base(null)
    {
        ArgumentNullException.ThrowIfNull(signals);
        _signals = signals;
        _signals.FlightCycleReset += Clear;
    }

    public RunwayCondition? Current => Snapshot();

    public void Set(RunwayCondition condition) => Update(_ => condition);

    public void Clear() => Update(_ => null);

    public void Dispose()
    {
        _signals.FlightCycleReset -= Clear;
        GC.SuppressFinalize(this);
    }
}
