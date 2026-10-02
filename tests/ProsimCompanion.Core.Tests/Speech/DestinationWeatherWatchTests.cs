using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Speech;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Weather;
using ProsimCompanion.Speech.Monitoring;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Issue #148, module 3: destination weather watch — edges, rate limit, baseline,
/// tailwind maths, and the shell's gating on the hero weather store.</summary>
public sealed class DestinationWeatherWatchTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly DestinationWeatherWatchOptions Options = new() { Enabled = true, MinutesBetweenAnnouncements = 15 };

    private static WxFacts Facts(int? vis = 9999, int? ceiling = 2500, string? atis = "A", int? windDir = 270, int? windKt = 10)
        => new("METAR …", windDir, windKt, vis, 1013, 15, atis, "27R", ceiling);

    [Fact]
    public void FirstObservation_IsABaseline_UnlessAlreadyBelowALimit()
    {
        var quiet = new DestinationWeatherWatchCore().Observe(Facts(), "27R", Options, T0);
        var fog = new DestinationWeatherWatchCore().Observe(Facts(vis: 800, ceiling: 200), "27R", Options, T0);

        Assert.Empty(quiet);
        Assert.Equal([WeatherTrigger.Visibility, WeatherTrigger.Ceiling], fog.Select(a => a.Trigger));
        Assert.Equal("Destination visibility now 800 metres, below 1500 metres.", fog[0].Text);
        Assert.Equal("Destination ceiling now 200 feet, below 500.", fog[1].Text);
        Assert.All(fog, a => Assert.True(a.BelowThreshold));
    }

    [Fact]
    public void Edges_SpeakOnTheCrossing_BothWays_NotWhileItStaysThere()
    {
        var core = new DestinationWeatherWatchCore();
        core.Observe(Facts(), "27R", Options, T0);

        var down = core.Observe(Facts(vis: 1200), "27R", Options, T0.AddMinutes(20));
        var still = core.Observe(Facts(vis: 1000), "27R", Options, T0.AddMinutes(40));
        var up = core.Observe(Facts(vis: 3000), "27R", Options, T0.AddMinutes(60));

        Assert.Equal("Destination visibility now 1200 metres, below 1500 metres.", Assert.Single(down).Text);
        Assert.Empty(still);
        var improved = Assert.Single(up);
        Assert.Equal("Destination visibility improved to 3000 metres.", improved.Text);
        Assert.False(improved.BelowThreshold);
    }

    [Fact]
    public void Ceiling_NoCeilingReported_CountsAsAbove_AndLiftingSaysSo()
    {
        var core = new DestinationWeatherWatchCore();
        core.Observe(Facts(ceiling: 300), "27R", Options, T0);                       // baseline: below, announced
        var clear = core.Observe(Facts(ceiling: null), "27R", Options, T0.AddMinutes(20));
        var back = core.Observe(Facts(ceiling: 400), "27R", Options, T0.AddMinutes(40));

        Assert.Equal("Destination ceiling has lifted.", Assert.Single(clear).Text);
        Assert.Equal("Destination ceiling now 400 feet, below 500.", Assert.Single(back).Text);
    }

    [Fact]
    public void Atis_NewLetter_IsAnnounced_WhenEnabled()
    {
        var core = new DestinationWeatherWatchCore();
        core.Observe(Facts(atis: "A"), "27R", Options, T0);
        var changed = core.Observe(Facts(atis: "B"), "27R", Options, T0.AddMinutes(20));
        var same = core.Observe(Facts(atis: "b"), "27R", Options, T0.AddMinutes(40));

        Assert.Equal("Destination ATIS now information Bravo.", Assert.Single(changed).Text);
        Assert.Empty(same);

        var off = new DestinationWeatherWatchCore();
        var noAtis = new DestinationWeatherWatchOptions { AnnounceAtisChange = false };
        off.Observe(Facts(atis: "A"), "27R", noAtis, T0);
        Assert.Empty(off.Observe(Facts(atis: "C"), "27R", noAtis, T0.AddMinutes(20)));
    }

    [Fact]
    public void Tailwind_OnThePlannedRunway_AppearsAndGoesAway()
    {
        var core = new DestinationWeatherWatchCore();
        core.Observe(Facts(windDir: 270, windKt: 10), "27R", Options, T0);           // headwind baseline
        var tail = core.Observe(Facts(windDir: 90, windKt: 8), "27R", Options, T0.AddMinutes(20));
        var gone = core.Observe(Facts(windDir: 180, windKt: 8), "27R", Options, T0.AddMinutes(40));

        Assert.Equal("Wind at destination 090 at 8 knots: a 8 knot tailwind on runway two seven right.", Assert.Single(tail).Text);
        Assert.True(tail[0].BelowThreshold);
        Assert.Equal("Wind at destination 180 at 8 knots: no longer a tailwind on runway two seven right.", Assert.Single(gone).Text);

        // No planned runway: nothing to measure against.
        var noRunway = new DestinationWeatherWatchCore();
        noRunway.Observe(Facts(windDir: 270), null, Options, T0);
        Assert.Empty(noRunway.Observe(Facts(windDir: 90, windKt: 20), null, Options, T0.AddMinutes(20)));
    }

    [Fact]
    public void RateLimit_IsPerTrigger()
    {
        var core = new DestinationWeatherWatchCore();
        core.Observe(Facts(), "27R", Options, T0);
        core.Observe(Facts(vis: 1000), "27R", Options, T0.AddMinutes(1));           // visibility spoken
        var tooSoon = core.Observe(Facts(vis: 3000, atis: "B"), "27R", Options, T0.AddMinutes(5));
        var later = core.Observe(Facts(vis: 1000, atis: "B"), "27R", Options, T0.AddMinutes(17));

        Assert.Equal([WeatherTrigger.Atis], tooSoon.Select(a => a.Trigger));      // visibility muted, ATIS not
        Assert.Equal([WeatherTrigger.Visibility], later.Select(a => a.Trigger));
    }

    [Fact]
    public void Reset_ForgetsTheBaseline()
    {
        var core = new DestinationWeatherWatchCore();
        core.Observe(Facts(vis: 800), "27R", Options, T0);
        core.Reset();

        // The same fog is a fresh below-limit baseline again, and the rate limit is gone.
        Assert.Single(core.Observe(Facts(vis: 800), "27R", Options, T0.AddMinutes(1)));
    }

    [Theory]
    [InlineData(270, 10, 270, -10)]   // straight headwind
    [InlineData(90, 10, 270, 10)]     // straight tailwind
    [InlineData(0, 10, 270, 0)]       // pure crosswind
    [InlineData(120, 10, 270, 8.66)]
    public void TailwindComponent(int windDir, int windKt, double runwayHeading, double expected)
        => Assert.Equal(expected, DestinationWeatherWatchCore.TailwindComponentKt(Facts(windDir: windDir, windKt: windKt), runwayHeading)!.Value, 2);

    [Fact]
    public void TailwindComponent_CalmIsZero_VariableIsUnknown_NoWindIsNull()
    {
        Assert.Equal(0, DestinationWeatherWatchCore.TailwindComponentKt(Facts(windDir: null, windKt: 0), 270));
        Assert.Null(DestinationWeatherWatchCore.TailwindComponentKt(Facts(windDir: null, windKt: 5), 270));
        Assert.Null(DestinationWeatherWatchCore.TailwindComponentKt(Facts(windDir: null, windKt: null), 270));
    }

    [Theory]
    [InlineData("27R", 270.0)]
    [InlineData("04", 40.0)]
    [InlineData("RW16L", 160.0)]
    [InlineData("9", 90.0)]
    [InlineData("", null)]
    [InlineData("RWY", null)]
    [InlineData("40", null)]
    public void RunwayHeading(string runway, double? expected)
        => Assert.Equal(expected, DestinationWeatherWatchCore.RunwayHeadingDeg(runway));

    [Fact]
    public void AlternateLine_ReadsWhatItHas()
    {
        Assert.Equal(
            "Alternate Schiphol: visibility 10 kilometres, ceiling 2500 feet, wind 270 at 10 knots.",
            DestinationWeatherWatchCore.AlternateLine("Schiphol", Facts()));
        Assert.Equal(
            "Alternate Schiphol: visibility 4000 metres, no ceiling.",
            DestinationWeatherWatchCore.AlternateLine("Schiphol", Facts(vis: 4000, ceiling: null, windDir: null, windKt: null)));
        Assert.Null(DestinationWeatherWatchCore.AlternateLine("Schiphol", WxFacts.None));
        Assert.Null(DestinationWeatherWatchCore.AlternateLine("", Facts()));
        Assert.Null(DestinationWeatherWatchCore.AlternateLine("Schiphol", null));
    }

    // ---- the shell ----

    private sealed class Harness
    {
        public FakePhaseSource Phases { get; } = new();
        public FakeArbiter Arbiter { get; } = new();
        public OfpStore Ofp { get; } = new();
        public GroundOpsSignals Signals { get; } = new();
        public SopOptions Sop { get; } = new();
        public DestinationWeatherWatch Watch { get; }

        public Harness()
        {
            Sop.Monitoring.DestinationWeather.Enabled = true;
            Ofp.Set(new OfpData { DestinationIcao = "EHAM", AlternateIcao = "EBBR", PlannedRunwayIn = "27" });
            Phases.SetPhase(FlightPhase.Cruise);
            Watch = new DestinationWeatherWatch(
                SpeechTestSupport.SopMonitor(Sop), Phases, new HeroWeatherStore(), Ofp, Signals, Arbiter,
                SpeechTestSupport.TempEventLog(), NullLogger<DestinationWeatherWatch>.Instance, new SpokenText());
        }

        public static HeroWeatherSnapshot Snapshot(WxFacts destination, WxFacts? alternate = null, WxProbeStatus status = WxProbeStatus.Found)
        {
            var dest = WeatherCard.From(WeatherCardRole.Destination, "EHAM", "Schiphol",
                status == WxProbeStatus.Found ? WxProbe.Found(destination) : new WxProbe(status, WxFacts.None, null));
            var alt = alternate is null
                ? WeatherCard.Empty(WeatherCardRole.Local)
                : WeatherCard.From(WeatherCardRole.Alternate, "EBBR", "Brussels", WxProbe.Found(alternate));
            return new HeroWeatherSnapshot(alt, dest, T0, false);
        }
    }

    [Fact]
    public void Shell_SpeaksTheEdge_WithTheAlternate_WhenBelowALimit()
    {
        var h = new Harness();
        h.Watch.Observe(Harness.Snapshot(Facts()), T0);
        var spoken = h.Watch.Observe(Harness.Snapshot(Facts(vis: 600), Facts(vis: 8000, ceiling: 3000)), T0.AddMinutes(20));

        Assert.Single(spoken);
        var request = Assert.Single(h.Arbiter.Requests);
        Assert.Equal("fo.destination-weather", request.Tag);
        // No airport-name source in the harness: the alternate is spelt phonetically.
        Assert.Equal(
            $"Destination visibility now 600 metres, below 1500 metres. Alternate {new SpokenText().Airport("EBBR")}: visibility 8000 metres, ceiling 3000 feet, wind 270 at 10 knots.",
            request.Text);
    }

    [Fact]
    public void Shell_StaysSilent_WhenDisabled_NotLive_OnTheGround_NoDestination_OrNoObservation()
    {
        var h = new Harness();
        var fog = Harness.Snapshot(Facts(vis: 600));

        h.Sop.Monitoring.DestinationWeather.Enabled = false;
        Assert.Empty(h.Watch.Observe(fog, T0));
        h.Sop.Monitoring.DestinationWeather.Enabled = true;

        h.Phases.SetLive(false);
        Assert.Empty(h.Watch.Observe(fog, T0));
        h.Phases.SetLive(true);

        h.Phases.SetPhase(FlightPhase.Climb);
        Assert.Empty(h.Watch.Observe(fog, T0));
        h.Phases.SetPhase(FlightPhase.Cruise);

        Assert.Empty(h.Watch.Observe(Harness.Snapshot(Facts(vis: 600), status: WxProbeStatus.NoData), T0));

        h.Ofp.Clear();
        Assert.Empty(h.Watch.Observe(fog, T0));
        Assert.Empty(h.Arbiter.Requests);

        h.Ofp.Set(new OfpData { DestinationIcao = "EHAM" });
        Assert.Single(h.Watch.Observe(fog, T0));                 // first observation of a below-limit destination
    }

    [Fact]
    public void Shell_ANewDestinationOrFlightCycle_StartsAFreshBaseline()
    {
        var h = new Harness();
        h.Watch.Observe(Harness.Snapshot(Facts(vis: 600)), T0);
        Assert.Single(h.Arbiter.Requests);

        h.Signals.RaiseFlightCycleReset();
        h.Watch.Observe(Harness.Snapshot(Facts(vis: 600)), T0.AddMinutes(1));
        Assert.Equal(2, h.Arbiter.Requests.Count);                 // baseline again, rate limit cleared

        h.Ofp.Set(new OfpData { DestinationIcao = "EGLL" });
        h.Watch.Observe(Harness.Snapshot(Facts(vis: 600)) with
        {
            Second = WeatherCard.From(WeatherCardRole.Destination, "EGLL", "Heathrow", WxProbe.Found(Facts(vis: 600))),
        }, T0.AddMinutes(2));
        Assert.Equal(3, h.Arbiter.Requests.Count);
    }
}
