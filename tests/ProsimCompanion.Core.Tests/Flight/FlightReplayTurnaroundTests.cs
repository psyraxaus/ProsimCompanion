using System.Text.Json;
using ProsimCompanion.Core.Flight;
using Xunit;
using Xunit.Abstractions;

namespace ProsimCompanion.Core.Tests.Flight;

/// <summary>The replay harness re-derives the arrival-complete signal from the session's
/// "Deboarding: Completed" GSX event (ADR-0019; audit 2026-11-02 §1), so the
/// <c>turnaround-arrival-complete</c> rule — Shutdown → Preflight at a turnaround — can be
/// judged offline like every other edge. The synthetic full flight (which ends in Shutdown)
/// is extended by a parked wait with and without the deboarding event.</summary>
public sealed class FlightReplayTurnaroundTests(ITestOutputHelper output)
{
    /// <summary>The synthetic recording's last sample (09:03:35Z, Shutdown, parked, beacon off).</summary>
    private static readonly DateTimeOffset LastSample = new(2026, 8, 29, 9, 3, 35, TimeSpan.Zero);

    [Fact]
    public void Replay_RaisesArrivalCompleteFromTheSessionsDeboardingEvent()
    {
        var lines = SyntheticFlight();
        lines.Add(Envelope(5, "gsx-service", new { service = "Deboarding", @event = "Completed" }));
        for (var s = 10; s <= 90; s += 5)
        {
            lines.Add(Envelope(s, "flight-sample", ParkedShutdown()));
        }

        var result = FlightReplay.Run(lines);
        output.WriteLine(result.Describe());

        var preflight = Assert.Single(result.Commits, c => c.Previous == FlightPhase.Shutdown && c.Current == FlightPhase.Preflight);
        Assert.Equal("turnaround-arrival-complete", preflight.RuleId);
        Assert.True(preflight.At >= LastSample.AddSeconds(5 + 30), "Preflight must wait for the deboarding event plus the 30 s turnaround hold");
    }

    [Fact]
    public void Replay_WithoutTheDeboardingEvent_StaysInShutdown()
    {
        var lines = SyntheticFlight();
        for (var s = 10; s <= 90; s += 5)
        {
            lines.Add(Envelope(s, "flight-sample", ParkedShutdown()));
        }

        var result = FlightReplay.Run(lines);
        output.WriteLine(result.Describe());

        Assert.DoesNotContain(result.Commits, c => c.Previous == FlightPhase.Shutdown);
        Assert.Equal(FlightPhase.Shutdown, result.Commits[^1].Current);
    }

    [Fact]
    public void TheEhamTurnaround_ReplaysShutdownToPreflight_OnTheDeboardingEvent()
    {
        // EGLL→EHAM 2026-11-01: Deboarding Completed 01:50:03Z, live Shutdown → Preflight 01:50:33Z.
        var path = Path.Combine(AppContext.BaseDirectory, "Flight", "Recordings", "egll-eham-2026-11-01-1.0.0-rc1.jsonl");
        var result = FlightReplay.RunFile(path);

        var turnaround = Assert.Single(result.Commits, c => c.Previous == FlightPhase.Shutdown && c.Current == FlightPhase.Preflight);
        Assert.Equal("turnaround-arrival-complete", turnaround.RuleId);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 1, 50, 33, TimeSpan.Zero), new DateTimeOffset(turnaround.At.Year, turnaround.At.Month, turnaround.At.Day, turnaround.At.Hour, turnaround.At.Minute, turnaround.At.Second, TimeSpan.Zero));
    }

    private static List<string> SyntheticFlight()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Flight", "Recordings", "synthetic-full-flight.jsonl");
        return File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
    }

    private static string Envelope(int secondsAfterLastSample, string type, object payload)
        => JsonSerializer.Serialize(new { timestamp = LastSample.AddSeconds(secondsAfterLastSample), type, payload });

    /// <summary>The synthetic recording's own final sample, repeated: on the ground, stopped,
    /// brake set, powered, engines off, beacon off.</summary>
    private static object ParkedShutdown() => new
    {
        ph = "Shutdown",
        og = true,
        ias = 0,
        gs = 0,
        pwr = true,
        eng = false,
        engRaw = false,
        pbRaw = 3,
        brk = true,
        gear = true,
        apu = false,
        bcn = false,
        thr = false,
        n1 = 0,
    };
}
