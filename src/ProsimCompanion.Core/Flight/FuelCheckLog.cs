using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Flight;

/// <summary>One fuel check the First Officer spoke (issue #148), kept for the Fuel Log page
/// (issue #154): when, why, and the sentence itself.</summary>
public sealed record FuelCheckRecord(
    DateTimeOffset AtUtc,
    string Trigger,
    string Method,
    string? Fix,
    double FuelOnBoardKg,
    double? PlannedFuelOnBoardKg,
    double? DifferenceKg,
    double? EstimatedLandingKg,
    double? PlannedLandingKg,
    bool Shortfall,
    string Text);

public sealed record FuelCheckLogSnapshot(IReadOnlyList<FuelCheckRecord> Checks)
{
    public static FuelCheckLogSnapshot Empty { get; } = new([]);

    public FuelCheckRecord? Latest => Checks.Count == 0 ? null : Checks[0];
}

/// <summary>The last twenty fuel checks, newest first. The voice monitor writes; the page reads.</summary>
public sealed class FuelCheckLogStore : SnapshotStore<FuelCheckLogSnapshot>
{
    public const int Keep = 20;

    public FuelCheckLogStore()
        : base(FuelCheckLogSnapshot.Empty)
    {
    }

    public void Add(FuelCheckRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Update(s => new FuelCheckLogSnapshot([record, .. s.Checks.Take(Keep - 1)]));
    }

    public void Clear() => Update(_ => FuelCheckLogSnapshot.Empty);
}

/// <summary>The page's "Fuel check now" button, without the Web project knowing the voice
/// monitor: the Speech pillar registers the implementation.</summary>
public interface IFuelCheckRequests
{
    /// <summary>Speaks a fuel check now, as if the pilot had said "fuel check". Returns the
    /// reason it could not when it did not (no flight data, no plan), null when it did.</summary>
    string? RequestNow(string source);
}
