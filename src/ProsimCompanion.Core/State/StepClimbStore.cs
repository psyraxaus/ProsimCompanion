namespace ProsimCompanion.Core.State;

/// <summary>The next planned step as the advisor last saw it (issue #189) — the ATC request
/// template's <c>{stepLevel}</c> reads it.</summary>
/// <param name="NextFlightLevel">The planned level at the next step; null = none ahead.</param>
/// <param name="NextFix">The fix the step is planned at.</param>
/// <param name="DistanceNm">Direct distance to that fix; null without a position.</param>
/// <param name="Advised">The FO has spoken the advice for this step.</param>
public sealed record StepClimbSnapshot(int? NextFlightLevel, string? NextFix, double? DistanceNm, bool Advised)
{
    public static StepClimbSnapshot Empty { get; } = new(null, null, null, false);
}

/// <summary>Written by the Speech step-climb advisor; read by the SayIntentions request filler.</summary>
public sealed class StepClimbStore : SnapshotStore<StepClimbSnapshot>
{
    public StepClimbStore()
        : base(StepClimbSnapshot.Empty)
    {
    }
}
