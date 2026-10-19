using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace ProsimCompanion.Core.Tests.Flight;

/// <summary>
/// The committed ground facts under the same regression mechanism as the phase commits
/// (ADR-0008): a recording replayed through the engine must publish the beacon and parked
/// edges at the sample times the flight shows — synthetic first, then the real flights.
/// </summary>
public sealed class GroundFactsReplayTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 29, 9, 0, 0, TimeSpan.Zero);

    private static string RecordingPath(string name)
        => Path.Combine(AppContext.BaseDirectory, "Flight", "Recordings", name);

    private static List<GroundFactChange> BeaconEdges(FlightReplayResult result)
        => result.GroundFactEdges.Where(e => e.Edge is GroundFactEdge.BeaconOn or GroundFactEdge.BeaconOff).ToList();

    [Fact]
    public void SyntheticFlight_PublishesTheBeaconAndParkedEdges_AtTheSampleTimes()
    {
        var result = FlightReplay.Run(FlightReplayTests.Lines(ParkedLonger()));
        output.WriteLine(result.Describe());

        // Beacon: on in the 10 s Preflight sample, off in the 200 s TaxiIn sample.
        Assert.Equal(
            [(GroundFactEdge.BeaconOn, T0.AddSeconds(10)), (GroundFactEdge.BeaconOff, T0.AddSeconds(200))],
            BeaconEdges(result).Select(e => (e.Edge, e.At)));

        // Parked: the first sample is parked at the gate (brake set, engines off, stopped);
        // the brake comes off at 10 s; the aircraft is parked again at 200 s (engines cut,
        // brake set, stopped) and the spell is stable 30 s later — on the tick-filled
        // cadence between the 215 s and 240 s samples.
        var parked = result.GroundFactEdges.Where(e => e.Edge is GroundFactEdge.Parked or GroundFactEdge.Unparked or GroundFactEdge.StableParked).ToList();
        Assert.Equal(
            [
                (GroundFactEdge.Parked, T0),
                (GroundFactEdge.Unparked, T0.AddSeconds(10)),
                (GroundFactEdge.Parked, T0.AddSeconds(200)),
                (GroundFactEdge.StableParked, T0.AddSeconds(230)),
            ],
            parked.Select(e => (e.Edge, e.At)));
        Assert.True(parked[^1].Facts.StableParked);
        Assert.Equal(T0.AddSeconds(200), parked[^1].Facts.ParkedSince);
    }

    [Fact]
    public void SyntheticFlight_EngineEdgesFollowTheStartAndTheShutdown()
    {
        var result = FlightReplay.Run(FlightReplayTests.Lines(FlightReplayTests.SyntheticFlight()));

        var engines = result.GroundFactEdges.Where(e => e.Edge is GroundFactEdge.EnginesOff or GroundFactEdge.EngineRunning).ToList();
        Assert.Equal(
            [
                (GroundFactEdge.EnginesOff, T0),              // first sample, engines off at the gate
                (GroundFactEdge.EngineRunning, T0.AddSeconds(20)), // the start
                (GroundFactEdge.EnginesOff, T0.AddSeconds(200)),   // the shutdown
            ],
            engines.Select(e => (e.Edge, e.At)));
    }

    [Fact]
    public void ReplayWithOptions_StableParkedHonoursTheTurnaroundHold()
    {
        var result = FlightReplay.Run(FlightReplayTests.Lines(ParkedLonger()), new FlightStateOptions { TurnaroundHoldSeconds = 60 });

        var stable = Assert.Single(result.GroundFactEdges, e => e.Edge == GroundFactEdge.StableParked);
        Assert.Equal(T0.AddSeconds(260), stable.At);
    }

    /// <summary>The synthetic flight with the parked shutdown held to 270 s, in samples no
    /// more than 30 s apart — the replay treats a longer gap as an app restart and does not
    /// tick-fill it, exactly as it would not for a live recording's keep-alive cadence.</summary>
    private static List<(double At, FlightSample Sample)> ParkedLonger()
    {
        var samples = FlightReplayTests.SyntheticFlight().ToList();
        var parked = samples[^1].Sample;
        samples.Add((240, parked));
        samples.Add((270, parked));
        return samples;
    }

    [Fact]
    public void EfhkRecording_BeaconOnBeforePushAndOffAtTheStand()
    {
        // EFHK → EGCC 2026-10-10 (rc.1): the crew switched the beacon on at 04:07:50Z in
        // Departure and off at 07:15:19Z during taxi-in — the only two flips in 10,998 samples.
        var result = FlightReplay.RunFile(RecordingPath("efhk-egcc-2026-10-10-0.7.0-rc1.jsonl"));
        output.WriteLine(result.Describe());

        var beacon = BeaconEdges(result);
        Assert.Equal([GroundFactEdge.BeaconOn, GroundFactEdge.BeaconOff], beacon.Select(e => e.Edge));
        Assert.Equal(DateTimeOffset.Parse("2026-10-10T04:07:50.5489038+00:00", System.Globalization.CultureInfo.InvariantCulture), beacon[0].At);
        Assert.Equal(DateTimeOffset.Parse("2026-10-10T07:15:19.0977761+00:00", System.Globalization.CultureInfo.InvariantCulture), beacon[1].At);
    }

    [Fact]
    public void EgccRecording_CrewToggledTheBeaconTwice_EveryFlipIsAnEdge()
    {
        // EGCC → EFHK 2026-10-11 (rc.4): on 23:49:26Z, off 23:55:53Z, on again 23:57:48Z,
        // off 02:51:08Z — a real crew toggle minutes apart, not a flicker; each flip commits.
        var result = FlightReplay.RunFile(RecordingPath("egcc-efhk-2026-10-11-0.7.0-rc4.jsonl"));
        output.WriteLine(result.Describe());

        var beacon = BeaconEdges(result);
        Assert.Equal(
            [GroundFactEdge.BeaconOn, GroundFactEdge.BeaconOff, GroundFactEdge.BeaconOn, GroundFactEdge.BeaconOff],
            beacon.Select(e => e.Edge));
        Assert.Equal(new DateTimeOffset(2026, 10, 10, 23, 49, 26, TimeSpan.Zero), Truncate(beacon[0].At));
        Assert.Equal(new DateTimeOffset(2026, 10, 10, 23, 55, 53, TimeSpan.Zero), Truncate(beacon[1].At));
        Assert.Equal(new DateTimeOffset(2026, 10, 10, 23, 57, 48, TimeSpan.Zero), Truncate(beacon[2].At));
        Assert.Equal(new DateTimeOffset(2026, 10, 11, 2, 51, 8, TimeSpan.Zero), Truncate(beacon[3].At));
    }

    [Fact]
    public void CheckedInRecordings_ParkAtTheGateAndReachStableParkedAfterTheHold()
    {
        // Every real recording starts parked at the gate: the parked spell opens on the
        // first sample and is stable no sooner than one turnaround hold later. Not "exactly
        // one tick later": the EFHK recording has a 2.9-minute not-live gap after its second
        // sample that the replay does not tick-fill (an app-restart heuristic), so the hold
        // elapses on the ticks that exist. (The synthetic recording pushes back 10 s in and
        // ends 15 s after shutdown — it never holds a spell that long.)
        var recordings = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Flight", "Recordings"), "*.jsonl")
            .Where(path => !Path.GetFileName(path).StartsWith("synthetic-", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(recordings);
        foreach (var recording in recordings)
        {
            var result = FlightReplay.RunFile(recording);
            var hold = TimeSpan.FromSeconds(FlightStateOptions.Default.TurnaroundHoldSeconds);

            var first = result.GroundFactEdges.First(e => e.Edge == GroundFactEdge.Parked);
            Assert.Equal(result.FirstSampleAt, first.At);
            var stable = result.GroundFactEdges.First(e => e.Edge == GroundFactEdge.StableParked);
            Assert.Equal(first.At, stable.Facts.ParkedSince);
            Assert.True(stable.At - first.At >= hold,
                $"{Path.GetFileName(recording)}: stable parked {(stable.At - first.At).TotalSeconds:F1} s into a {hold.TotalSeconds:F0} s hold");
            Assert.DoesNotContain(result.GroundFactEdges, e => e.Edge == GroundFactEdge.Unparked && e.At < stable.At);
        }
    }

    [Fact]
    public void Engine_HoldsTheFactsAcrossASessionGap_AndRestartsTheParkedSpell()
    {
        // Beacon on at the stand, the session ends, the session comes back with the beacon
        // still on: no second BeaconOn edge (a raw subscription would not have pushed one
        // either), but the parked spell is a new one.
        var session = new SimSessionStore();
        using var engine = new FlightStateEngine(new HeldSource(), session, NullLogger<FlightStateEngine>.Instance);
        var edges = new List<GroundFactChange>();
        engine.GroundFactChanged += edges.Add;
        var parked = new FlightDataSnapshot
        {
            IsValid = true, IsReady = true, OnGround = true, AircraftPowered = true,
            ParkBrakeSet = true, GearDown = true, BeaconOn = true,
        };

        session.Publish(new SimSessionSnapshot(SimSessionPhase.InSession, true, false, null, null));
        engine.ProcessTick(parked, T0);
        Assert.Contains(edges, e => e.Edge == GroundFactEdge.BeaconOn);
        Assert.Contains(edges, e => e.Edge == GroundFactEdge.Parked);

        session.Publish(new SimSessionSnapshot(SimSessionPhase.NotInSession, true, false, null, null));
        engine.ProcessTick(parked, T0.AddSeconds(5));
        Assert.True(engine.Facts.BeaconOn, "the fact holds while the session is gone");
        Assert.False(engine.Facts.Parked, "the parked spell ended with the session");

        edges.Clear();
        session.Publish(new SimSessionSnapshot(SimSessionPhase.InSession, true, false, null, null));
        engine.ProcessTick(parked, T0.AddSeconds(10));
        Assert.Equal([GroundFactEdge.Parked], edges.Select(e => e.Edge));
        Assert.Equal(T0.AddSeconds(10), engine.Facts.ParkedSince);
    }

    private static DateTimeOffset Truncate(DateTimeOffset at)
        => new(at.Year, at.Month, at.Day, at.Hour, at.Minute, at.Second, at.Offset);

    private sealed class HeldSource : IFlightDataSource
    {
        public FlightDataSnapshot Sample() => new() { IsValid = false };
    }
}
