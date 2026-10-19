using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using Xunit;

namespace ProsimCompanion.Core.Tests.Flight;

/// <summary>
/// The committed ground facts' pure core: every edge and the one hold it owns (the parked
/// spell). The switch facts commit on one live sample by design — see the decision on
/// <see cref="GroundFactsTracker"/> — so these tables assert exactly one edge per flip.
/// </summary>
public sealed class GroundFactsTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);
    private static readonly FlightStateOptions Options = FlightStateOptions.Default;

    /// <summary>Parked at the gate, engines off, brake set, everything else off.</summary>
    private static FlightDataSnapshot AtGate(
        bool beacon = false, bool engines = false, bool brake = true, bool extPower = false,
        bool onGround = true, double gs = 0) => new()
    {
        IsValid = true,
        IsReady = true,
        OnGround = onGround,
        BeaconOn = beacon,
        AnyEngineRunning = engines,
        ParkBrakeSet = brake,
        ExternalPowerOn = extPower,
        GroundSpeedKt = gs,
    };

    private static GroundFactEdge[] Edges(IEnumerable<GroundFactChange> changes) => changes.Select(c => c.Edge).ToArray();

    [Fact]
    public void FirstSample_CommitsAgainstNone_LikeTheRawSubscriptionsStartedAtTheirFallbacks()
    {
        var tracker = new GroundFactsTracker();

        var edges = tracker.Observe(AtGate(beacon: true), T0, Options);

        // Beacon false -> true, engines-off false -> true, brake false -> true, parked
        // false -> true: the first raw push with value 1 used to be a rising edge too.
        Assert.Equal(
            [GroundFactEdge.BeaconOn, GroundFactEdge.EnginesOff, GroundFactEdge.ParkBrakeSet, GroundFactEdge.Parked],
            Edges(edges));
        Assert.True(tracker.Facts.BeaconOn);
        Assert.Equal(T0, tracker.Facts.ParkedSince);
        Assert.All(edges, e => Assert.Equal(T0, e.At));
        Assert.All(edges, e => Assert.Same(tracker.Facts, e.Facts));
    }

    [Fact]
    public void UnchangedSample_RaisesNothing()
    {
        var tracker = new GroundFactsTracker();
        tracker.Observe(AtGate(), T0, Options);

        var edges = tracker.Observe(AtGate(), T0.AddSeconds(1), Options);

        Assert.Empty(edges);
    }

    public static TheoryData<string, FlightDataSnapshot, FlightDataSnapshot, GroundFactEdge[]> Flips => new()
    {
        { "beacon on", AtGate(), AtGate(beacon: true), [GroundFactEdge.BeaconOn] },
        { "beacon off", AtGate(beacon: true), AtGate(), [GroundFactEdge.BeaconOff] },
        // An engine start ends the parked spell in the same tick, engines first.
        { "engine start", AtGate(), AtGate(engines: true), [GroundFactEdge.EngineRunning, GroundFactEdge.Unparked] },
        { "engines cut", AtGate(engines: true), AtGate(), [GroundFactEdge.EnginesOff, GroundFactEdge.Parked] },
        { "brake released", AtGate(), AtGate(brake: false), [GroundFactEdge.ParkBrakeReleased, GroundFactEdge.Unparked] },
        { "brake set", AtGate(brake: false), AtGate(), [GroundFactEdge.ParkBrakeSet, GroundFactEdge.Parked] },
        { "external power on", AtGate(), AtGate(extPower: true), [GroundFactEdge.ExternalPowerOn] },
        { "external power off", AtGate(extPower: true), AtGate(), [GroundFactEdge.ExternalPowerOff] },
        // Rolling with the brake set (a tow) is not parked; stopping again is.
        { "towed", AtGate(), AtGate(gs: 5), [GroundFactEdge.Unparked] },
        { "stopped", AtGate(gs: 5), AtGate(), [GroundFactEdge.Parked] },
        // Airborne with engines off and the brake set is a data shape, not a parked aircraft.
        { "lifted", AtGate(), AtGate(onGround: false), [GroundFactEdge.Unparked] },
    };

    [Theory]
    [MemberData(nameof(Flips))]
    public void Flip_RaisesExactlyItsEdges(string name, FlightDataSnapshot before, FlightDataSnapshot after, GroundFactEdge[] expected)
    {
        var tracker = new GroundFactsTracker();
        tracker.Observe(before, T0, Options);

        var edges = tracker.Observe(after, T0.AddSeconds(1), Options);

        Assert.True(expected.SequenceEqual(Edges(edges)), $"{name}: expected [{string.Join(", ", expected)}] got [{string.Join(", ", Edges(edges))}]");
    }

    [Fact]
    public void OneSampleCommits_NoAgreementCount()
    {
        // The switch facts have no sample-agreement debounce (see the tracker's decision):
        // a single live sample with the beacon on is the edge, the very next sample off is
        // the falling edge.
        var tracker = new GroundFactsTracker();
        tracker.Observe(AtGate(), T0, Options);

        var on = tracker.Observe(AtGate(beacon: true), T0.AddMilliseconds(250), Options);
        var off = tracker.Observe(AtGate(), T0.AddMilliseconds(500), Options);

        Assert.Equal([GroundFactEdge.BeaconOn], Edges(on));
        Assert.Equal([GroundFactEdge.BeaconOff], Edges(off));
    }

    [Fact]
    public void ParkedSpell_BecomesStable_AtTheTurnaroundHold()
    {
        var tracker = new GroundFactsTracker();
        var options = new FlightStateOptions { TurnaroundHoldSeconds = 30 };
        tracker.Observe(AtGate(), T0, options);

        Assert.Empty(tracker.Observe(AtGate(), T0.AddSeconds(29.75), options));
        Assert.False(tracker.Facts.StableParked);

        var edges = tracker.Observe(AtGate(), T0.AddSeconds(30), options);

        Assert.Equal([GroundFactEdge.StableParked], Edges(edges));
        Assert.True(tracker.Facts.StableParked);
        Assert.Equal(T0, tracker.Facts.ParkedSince);
        // Once per spell: the next tick is quiet.
        Assert.Empty(tracker.Observe(AtGate(), T0.AddSeconds(31), options));
    }

    [Fact]
    public void Movement_EndsTheSpell_AndTheNextStopStartsANewOne()
    {
        var tracker = new GroundFactsTracker();
        var options = new FlightStateOptions { TurnaroundHoldSeconds = 30 };
        tracker.Observe(AtGate(), T0, options);
        tracker.Observe(AtGate(), T0.AddSeconds(30), options);
        Assert.True(tracker.Facts.StableParked);

        var moved = tracker.Observe(AtGate(gs: 3), T0.AddSeconds(31), options);
        Assert.Equal([GroundFactEdge.Unparked], Edges(moved));
        Assert.False(tracker.Facts.StableParked);
        Assert.Null(tracker.Facts.ParkedSince);

        var stopped = tracker.Observe(AtGate(), T0.AddSeconds(40), options);
        Assert.Equal([GroundFactEdge.Parked], Edges(stopped));
        Assert.Equal(T0.AddSeconds(40), tracker.Facts.ParkedSince);
        // The hold counts from the new stop, not the old one.
        Assert.Empty(tracker.Observe(AtGate(), T0.AddSeconds(69), options));
        Assert.Equal([GroundFactEdge.StableParked], Edges(tracker.Observe(AtGate(), T0.AddSeconds(70), options)));
    }

    [Fact]
    public void ParkedUsesTheConfiguredGroundSpeedThreshold()
    {
        var tracker = new GroundFactsTracker();
        var options = new FlightStateOptions { ParkedGroundSpeedKt = 2 };

        tracker.Observe(AtGate(gs: 1.9), T0, options);
        Assert.True(tracker.Facts.Parked);

        tracker.Observe(AtGate(gs: 2.0), T0.AddSeconds(1), options);
        Assert.False(tracker.Facts.Parked);
    }

    [Fact]
    public void EndParkedSpell_KeepsTheDiscreteFacts()
    {
        // A session end: the beacon that stays on is not a new edge afterwards, but the
        // parked spell restarts with the next live sample.
        var tracker = new GroundFactsTracker();
        tracker.Observe(AtGate(beacon: true, extPower: true), T0, Options);
        tracker.Observe(AtGate(beacon: true, extPower: true), T0.AddSeconds(30), Options);

        tracker.EndParkedSpell();

        Assert.True(tracker.Facts.BeaconOn);
        Assert.True(tracker.Facts.ExternalPowerOn);
        Assert.False(tracker.Facts.Parked);
        Assert.False(tracker.Facts.StableParked);
        Assert.Null(tracker.Facts.ParkedSince);

        var edges = tracker.Observe(AtGate(beacon: true, extPower: true), T0.AddSeconds(60), Options);
        Assert.Equal([GroundFactEdge.Parked], Edges(edges));
        Assert.Equal(T0.AddSeconds(60), tracker.Facts.ParkedSince);
    }
}
