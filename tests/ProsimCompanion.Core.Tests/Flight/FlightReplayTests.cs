using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using Xunit;
using Xunit.Abstractions;

namespace ProsimCompanion.Core.Tests.Flight;

/// <summary>
/// The replay harness: a synthetic recording drives the engine through a whole flight, and
/// every <c>Flight/Recordings/*.jsonl</c> checked into the repo replays to its
/// <c>.expected.txt</c> timeline — real flights as regression tests.
/// </summary>
public sealed class FlightReplayTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 29, 9, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The sample-only synthetic flight: (seconds from T0, sample). Steady states
    /// are written once — the replay holds the last sample between lines exactly as the
    /// recorder's dedupe implies.</summary>
    internal static IReadOnlyList<(double At, FlightSample Sample)> SyntheticFlight() =>
    [
        (0, Ground(phase: "Unknown")),
        (10, Ground(phase: "Preflight", beacon: true, apu: true, brake: false)),
        (20, Ground(phase: "PushbackAndStart", beacon: true, apu: true, brake: false, engines: true, gs: 15, ias: 12)),
        (40, Ground(phase: "TaxiOut", beacon: true, brake: false, engines: true, gs: 60, ias: 60, thrust: true, n1: 88)),
        (50, Air(phase: "TakeoffRoll", ias: 165, gs: 170, ra: 500, alt: 1200, vs: 2500)),
        (60, Air(phase: "InitialClimb", ias: 250, gs: 300, ra: 5000, alt: 10000, vs: 2000)),
        (90, Air(phase: "Climb", ias: 260, gs: 450, ra: 30000, alt: 36000, vs: 0, fms: 36000)),
        (120, Air(phase: "Cruise", ias: 270, gs: 430, ra: 15000, alt: 18000, vs: -2000, fms: 36000)),
        (150, Air(phase: "Descent", ias: 150, gs: 160, ra: 1500, alt: 2000, vs: -700, gear: true, fms: 36000)),
        (170, Ground(phase: "Approach", beacon: true, brake: false, engines: true, gs: 130, ias: 130, gear: true)),
        (180, Ground(phase: "LandingRollout", beacon: true, brake: false, engines: true, gs: 15, ias: 15, gear: true)),
        (200, Ground(phase: "TaxiIn", beacon: false, brake: true, engines: false, gs: 0, ias: 0, gear: true)),
        (215, Ground(phase: "Shutdown", beacon: false, brake: true, engines: false, gs: 0, ias: 0, gear: true)),
    ];

    internal static readonly string[] SyntheticTimeline =
    [
        "Unknown -> Preflight",
        "Preflight -> PushbackAndStart",
        "PushbackAndStart -> TaxiOut",
        "TaxiOut -> TakeoffRoll",
        "TakeoffRoll -> InitialClimb",
        "InitialClimb -> Climb",
        "Climb -> Cruise",
        "Cruise -> Descent",
        "Descent -> Approach",
        "Approach -> LandingRollout",
        "LandingRollout -> TaxiIn",
        "TaxiIn -> Shutdown",
    ];

    [Fact]
    public void SyntheticFlight_ReplaysToTheFullTimeline()
    {
        var result = FlightReplay.Run(Lines(SyntheticFlight()));
        output.WriteLine(result.Describe());

        Assert.Equal(SyntheticFlight().Count, result.SampleCount);
        Assert.Equal(SyntheticTimeline, result.Commits.Select(c => c.Edge));
        Assert.All(result.Commits, c => Assert.False(string.IsNullOrWhiteSpace(c.RuleId)));
    }

    [Fact]
    public void Replay_HonoursTheDebounces_AtTheRecordedTimes()
    {
        var result = FlightReplay.Run(Lines(SyntheticFlight()));

        // Climb -> Cruise carries the 5 s cruise settle: level at 90 s, committed at 95 s.
        var cruise = Assert.Single(result.Commits, c => c.Current == FlightPhase.Cruise);
        Assert.Equal(T0.AddSeconds(95), cruise.At);
        Assert.Equal("cruise", cruise.RuleId);
    }

    [Fact]
    public void Replay_ReportsAgreementWithTheRecordedTransitions()
    {
        var recorded = SyntheticTimeline.Select((edge, i) => (At: 5.0 + i * 15, Edge: edge)).ToList();
        var lines = Lines(SyntheticFlight()).Concat(recorded.Select(r => PhaseChanged(r.At, r.Edge)));

        var result = FlightReplay.Run(lines);

        Assert.Null(result.FirstDivergence);
        Assert.Equal(SyntheticTimeline.Length, result.RecordedCommits.Count);
        Assert.Contains("matches the recording", result.Describe());
    }

    [Fact]
    public void Replay_NamesTheFirstDivergence()
    {
        var recorded = SyntheticTimeline.ToArray();
        recorded[6] = "Climb -> Descent"; // the live engine (an older build) skipped Cruise
        var lines = Lines(SyntheticFlight())
            .Concat(recorded.Select((edge, i) => PhaseChanged(5.0 + i * 15, edge)));

        var result = FlightReplay.Run(lines);

        Assert.Equal(6, result.FirstDivergence);
        Assert.Contains("DIVERGES", result.Describe());
    }

    [Fact]
    public void Replay_WithOptions_ChangesTheVerdict()
    {
        // A 60 s cruise settle: level from 90 s to 120 s never commits Cruise.
        var result = FlightReplay.Run(Lines(SyntheticFlight()), new FlightStateOptions { CruiseSettleSeconds = 60 });

        Assert.DoesNotContain(result.Commits, c => c.Current == FlightPhase.Cruise);
    }

    [Fact]
    public void Replay_SkipsTornAndForeignLines()
    {
        var lines = new[] { "{\"type\":\"session-started\",\"timestamp\":\"2026-08-29T09:00:00Z\"}", "{not json" }
            .Concat(Lines(SyntheticFlight()));

        var result = FlightReplay.Run(lines);

        Assert.Equal(SyntheticFlight().Count, result.SampleCount);
    }

    [Fact]
    public void CheckedInRecordings_ReplayToTheirExpectedTimelines()
    {
        // Drop a session-*.jsonl from a real flight into Flight/Recordings with a sidecar
        // <name>.expected.txt (one "From -> To" per line, e.g. copied from a passing
        // Describe()); the flight then guards every future rule change.
        var directory = Path.Combine(AppContext.BaseDirectory, "Flight", "Recordings");
        Assert.True(Directory.Exists(directory), $"missing recordings directory {directory}");

        var recordings = Directory.GetFiles(directory, "*.jsonl");
        Assert.NotEmpty(recordings);
        foreach (var recording in recordings)
        {
            var result = FlightReplay.RunFile(recording);
            output.WriteLine($"== {Path.GetFileName(recording)}");
            output.WriteLine(result.Describe());

            var sidecar = Path.ChangeExtension(recording, ".expected.txt");
            Assert.True(File.Exists(sidecar),
                $"{Path.GetFileName(recording)} has no expected timeline. Create {Path.GetFileName(sidecar)} with:\n{result.Timeline}");

            var expected = File.ReadAllLines(sidecar)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('#'))
                .ToArray();
            Assert.Equal(expected, result.Commits.Select(c => c.Edge).ToArray());
        }
    }

    // ---- synthetic recording helpers ----

    internal static IEnumerable<string> Lines(IEnumerable<(double At, FlightSample Sample)> samples)
        => samples.Select(s => Envelope(s.At, "flight-sample", s.Sample));

    private static string PhaseChanged(double at, string edge)
    {
        var parts = edge.Split(" -> ");
        return Envelope(at, "phase-changed", new { previous = parts[0], current = parts[1], reason = "recorded" });
    }

    private static string Envelope(double at, string type, object payload)
        => JsonSerializer.Serialize(new
        {
            timestamp = T0.AddSeconds(at).ToString("o", CultureInfo.InvariantCulture),
            type,
            payload,
        }, Json);

    private static FlightSample Ground(
        string phase, bool beacon = false, bool apu = false, bool brake = true, bool engines = false,
        double gs = 0, double ias = 0, bool thrust = false, double n1 = 0, bool gear = true) => new()
    {
        Phase = phase,
        OnGround = true,
        Powered = true,
        BeaconOn = beacon,
        ApuRunning = apu,
        ParkBrakeSet = brake,
        EnginesRunning = engines,
        EnginesRunningRaw = engines,
        GroundSpeedKt = gs,
        IasKt = ias,
        TakeoffThrustSet = thrust,
        MaxN1Percent = n1,
        GearDown = gear,
        RawPushbackState = 3,
    };

    private static FlightSample Air(
        string phase, double ias, double gs, double ra, double alt, double vs, bool gear = false, double fms = 0) => new()
    {
        Phase = phase,
        OnGround = false,
        Powered = true,
        EnginesRunning = true,
        EnginesRunningRaw = true,
        BeaconOn = true,
        IasKt = ias,
        GroundSpeedKt = gs,
        RadioAltitudeFt = ra,
        AltitudeAglFt = ra,
        AltitudeFt = alt,
        VerticalSpeedFpm = vs,
        GearDown = gear,
        FmsCruiseAltFt = fms,
        RawPushbackState = 3,
    };
}
