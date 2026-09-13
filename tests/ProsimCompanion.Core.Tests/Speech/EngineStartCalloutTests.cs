using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Callouts;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Issue #131: the PM engine-start monitoring edges — starting on the upward N2
/// crossing, avail on running-at-idle, one "stabilized" per both-engines episode, silent on
/// shutdown, in flight, and on a reconnect with engines already turning.</summary>
public sealed class EngineStartCalloutCoreTests
{
    private static FlightDataSnapshot S(
        double n2One = 0, bool runOne = false, double n2Two = 0, bool runTwo = false,
        bool onGround = true, bool valid = true)
        => new()
        {
            IsValid = valid,
            OnGround = onGround,
            Engine1N2Percent = n2One,
            Engine1Running = runOne,
            Engine2N2Percent = n2Two,
            Engine2Running = runTwo,
        };

    private static IReadOnlyList<string> Texts(IReadOnlyList<EngineStartCall> calls)
        => [.. calls.Select(c => c.Text)];

    [Fact]
    public void FullStartSequence_TwoThenOne_SpeaksEachEdgeOnce()
    {
        var core = new EngineStartCalloutCore();
        Assert.Empty(core.ProcessSample(S())); // prime, both off

        // Engine two: N2 comes up, then lights and reaches idle.
        Assert.Equal(["Engine two, starting."], Texts(core.ProcessSample(S(n2Two: 12))));
        Assert.Empty(core.ProcessSample(S(n2Two: 30)));
        Assert.Empty(core.ProcessSample(S(n2Two: 45, runTwo: true))); // flag early, N2 not at idle yet
        Assert.Equal(["Engine two, avail."], Texts(core.ProcessSample(S(n2Two: 58, runTwo: true))));
        Assert.Empty(core.ProcessSample(S(n2Two: 59, runTwo: true)));

        // Engine one: same, and the avail tick leaves both at idle -> stabilized.
        Assert.Equal(["Engine one, starting."], Texts(core.ProcessSample(S(n2One: 15, n2Two: 59, runTwo: true))));
        Assert.Equal(
            ["Engine one, avail.", "Both engines stabilized."],
            Texts(core.ProcessSample(S(n2One: 58, runOne: true, n2Two: 59, runTwo: true))));

        // Steady state is silent.
        Assert.Empty(core.ProcessSample(S(n2One: 59, runOne: true, n2Two: 59, runTwo: true)));
    }

    [Fact]
    public void StateFlagFlicker_MidStart_DoesNotRepeatStarting()
    {
        var core = new EngineStartCalloutCore();
        core.ProcessSample(S());
        Assert.Single(core.ProcessSample(S(n2Two: 12)));
        // Issue #59-style flicker: running toggles while N2 is still climbing.
        Assert.Empty(core.ProcessSample(S(n2Two: 25, runTwo: true)));
        Assert.Empty(core.ProcessSample(S(n2Two: 30, runTwo: false)));
        Assert.Empty(core.ProcessSample(S(n2Two: 35)));
    }

    [Fact]
    public void Shutdown_SpoolingDownThroughTheStartLine_IsSilent_ThenRearms()
    {
        var core = new EngineStartCalloutCore();
        core.ProcessSample(S(n2One: 59, runOne: true, n2Two: 59, runTwo: true)); // prime, both running
        Assert.Empty(core.ProcessSample(S(n2One: 40, n2Two: 40)));
        Assert.Empty(core.ProcessSample(S(n2One: 8, n2Two: 8)));
        Assert.Empty(core.ProcessSample(S()));

        // A fresh start after the shutdown speaks again.
        Assert.Equal(["Engine two, starting."], Texts(core.ProcessSample(S(n2Two: 12))));
    }

    [Fact]
    public void ReconnectWithEnginesRunning_FabricatesNothing()
    {
        var core = new EngineStartCalloutCore();
        Assert.Empty(core.ProcessSample(S(n2One: 59, runOne: true, n2Two: 59, runTwo: true)));
        Assert.Empty(core.ProcessSample(S(n2One: 59, runOne: true, n2Two: 59, runTwo: true)));
    }

    [Fact]
    public void InvalidSample_DropsCrossingMemory()
    {
        var core = new EngineStartCalloutCore();
        core.ProcessSample(S());
        Assert.Empty(core.ProcessSample(S(valid: false)));
        // First valid sample after the gap only primes — even though N2 is above the line.
        Assert.Empty(core.ProcessSample(S(n2Two: 30)));
    }

    [Fact]
    public void Airborne_Relight_IsSilent()
    {
        var core = new EngineStartCalloutCore();
        core.ProcessSample(S(n2One: 59, runOne: true, onGround: false));
        Assert.Empty(core.ProcessSample(S(n2One: 59, runOne: true, n2Two: 12, onGround: false)));
        Assert.Empty(core.ProcessSample(S(n2One: 59, runOne: true, n2Two: 58, runTwo: true, onGround: false)));
    }

    [Fact]
    public void Stabilized_OncePerEpisode_ResetsOnlyWhenBothOff()
    {
        var core = new EngineStartCalloutCore();
        core.ProcessSample(S());
        core.ProcessSample(S(n2Two: 12));
        core.ProcessSample(S(n2Two: 58, runTwo: true));
        core.ProcessSample(S(n2One: 12, n2Two: 58, runTwo: true));
        Assert.Contains(
            "Both engines stabilized.",
            Texts(core.ProcessSample(S(n2One: 58, runOne: true, n2Two: 58, runTwo: true))));

        // Single-engine shutdown + restart (still one episode): avail, but no second stabilized.
        core.ProcessSample(S(n2One: 5, n2Two: 58, runTwo: true));
        core.ProcessSample(S(n2One: 12, n2Two: 58, runTwo: true));
        var restart = Texts(core.ProcessSample(S(n2One: 58, runOne: true, n2Two: 58, runTwo: true)));
        Assert.Equal(["Engine one, avail."], restart);
    }
}

/// <summary>The timer host: option and flight-live gates withhold speech but keep the core's
/// crossing memory honest; fired calls carry the callout tag/id shape.</summary>
public sealed class EngineStartMonitorTests : IDisposable
{
    private readonly SpeechOptions _speech = new();
    private readonly FakeArbiter _arbiter = new();
    private readonly FakePhaseSource _phase = new();
    private readonly FakeFlightSource _source = new();
    private readonly EngineStartMonitor _monitor;

    public EngineStartMonitorTests()
    {
        _monitor = new EngineStartMonitor(
            SpeechTestSupport.SpeechMonitor(_speech),
            _arbiter,
            _source,
            _phase,
            SpeechTestSupport.TempEventLog(),
            NullLogger<EngineStartMonitor>.Instance);
    }

    public void Dispose() => _monitor.Dispose();

    private static FlightDataSnapshot Ground(double n2Two, bool runTwo = false)
        => new() { IsValid = true, OnGround = true, Engine2N2Percent = n2Two, Engine2Running = runTwo };

    [Fact]
    public void Live_SpeaksWithTheCallId()
    {
        _monitor.ProcessSample(Ground(0));
        _monitor.ProcessSample(Ground(12));

        var request = Assert.Single(_arbiter.Requests);
        Assert.Equal("Engine two, starting.", request.Text);
        Assert.Equal(SpeechPriority.Normal, request.Priority);
        Assert.Equal("engineStart.starting", request.Tag);
    }

    [Fact]
    public void OptionOff_IsSilent()
    {
        _speech.EngineStartCallouts = false;
        _monitor.ProcessSample(Ground(0));
        _monitor.ProcessSample(Ground(12));
        Assert.Empty(_arbiter.Requests);
    }

    [Fact]
    public void NotLive_IsSilent_ButMemoryAdvances()
    {
        _phase.SetLive(false);
        _monitor.ProcessSample(Ground(0));
        _monitor.ProcessSample(Ground(12));
        Assert.Empty(_arbiter.Requests);

        // Going live mid-start does not replay the missed edge — only NEW edges speak.
        _phase.SetLive(true);
        _monitor.ProcessSample(Ground(30));
        Assert.Empty(_arbiter.Requests);
        _monitor.ProcessSample(Ground(58, runTwo: true));
        Assert.Equal("Engine two, avail.", Assert.Single(_arbiter.Requests).Text);
    }
}
