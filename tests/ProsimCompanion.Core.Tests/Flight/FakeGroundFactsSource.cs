using ProsimCompanion.Core.Flight;

namespace ProsimCompanion.Core.Tests.Flight;

/// <summary>Settable ground-facts source so the ground-ops consumers (equipment removal,
/// pushback gatherer, arrival chain, cabin crew) are tested by driving committed edges
/// directly, the way <c>FakePhaseSource</c> drives phases.</summary>
internal sealed class FakeGroundFactsSource : IGroundFactsSource
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    public GroundFacts Facts { get; set; } = GroundFacts.None;

    public event Action<GroundFactChange>? GroundFactChanged;

    /// <summary>Sets the facts and raises the given edge, as the engine does after a commit.</summary>
    public void Raise(GroundFactEdge edge, GroundFacts facts, DateTimeOffset? at = null)
    {
        Facts = facts;
        GroundFactChanged?.Invoke(new GroundFactChange(edge, facts, at ?? T0));
    }

    /// <summary>The beacon switched on: the edge the equipment removal and the sequencer wait for.</summary>
    public void BeaconOn() => Raise(GroundFactEdge.BeaconOn, Facts with { BeaconOn = true });

    public void BeaconOff() => Raise(GroundFactEdge.BeaconOff, Facts with { BeaconOn = false });
}
