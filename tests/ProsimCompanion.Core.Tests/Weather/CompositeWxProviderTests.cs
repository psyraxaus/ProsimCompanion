using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Weather;
using Xunit;

namespace ProsimCompanion.Core.Tests.Weather;

public sealed class CompositeWxProviderTests
{
    private static readonly WxFacts Observation = MetarParser.ToFacts("YSSY 070800Z 27012KT CAVOK 22/10 Q1013");

    private static Mock<IWxProvider> Provider(WxProbe result)
    {
        var mock = new Mock<IWxProvider>();
        mock.Setup(p => p.ProbeAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        return mock;
    }

    private static CompositeWxProvider Composite(WeatherStore? store = null, params IWxProvider[] providers)
        => new(providers, store ?? new WeatherStore(), NullLogger<CompositeWxProvider>.Instance);

    [Fact]
    public async Task FirstTierWithMetar_Wins_LowerTiersNotCalled()
    {
        var first = Provider(WxProbe.Found(Observation));
        var second = Provider(WxProbe.Found(MetarParser.ToFacts("YSSY 070800Z 00000KT CAVOK 25/10 Q1020")));

        var probe = await Composite(null, first.Object, second.Object).ProbeAsync("YSSY");

        Assert.Equal(WxProbeStatus.Found, probe.Status);
        Assert.Equal(Observation.RawMetar, probe.Facts.RawMetar);
        second.Verify(p => p.ProbeAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EmptyTier_FallsThroughToNext()
    {
        var first = Provider(WxProbe.Unavailable("ActiveSky not connected"));
        var second = Provider(WxProbe.Found(Observation));

        var probe = await Composite(null, first.Object, second.Object).ProbeAsync("YSSY");

        Assert.Equal(Observation.RawMetar, probe.Facts.RawMetar);
        first.Verify(p => p.ProbeAsync("YSSY", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ThrowingTier_IsSkipped_ChainSurvives()
    {
        var first = new Mock<IWxProvider>();
        first.Setup(p => p.ProbeAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var probe = await Composite(null, first.Object, Provider(WxProbe.Found(Observation)).Object)
            .ProbeAsync("YSSY");

        Assert.Equal(Observation.RawMetar, probe.Facts.RawMetar);
    }

    [Fact]
    public async Task GetAsyncExtension_ReturnsWinnersFacts()
    {
        var facts = await Composite(null, Provider(WxProbe.Found(Observation)).Object).GetAsync("YSSY");
        Assert.Equal(Observation.RawMetar, facts.RawMetar);
    }

    // ── Reason aggregation (issue #62) ──────────────────────────────────────────────────

    [Fact]
    public async Task AllTiersUnavailable_ReportsUnavailable_WithJoinedReasons()
    {
        var probe = await Composite(
            null,
            Provider(WxProbe.Unavailable("ActiveSky not connected")).Object,
            Provider(WxProbe.Unavailable("gateway HTTP 500")).Object).ProbeAsync("YSSY");

        Assert.Equal(WxProbeStatus.Unavailable, probe.Status);
        Assert.Null(probe.Facts.RawMetar);
        Assert.Equal("ActiveSky not connected; gateway HTTP 500", probe.Detail);
    }

    [Fact]
    public async Task AnyTierNoData_ReportsNoData_NotUnavailable()
    {
        var probe = await Composite(
            null,
            Provider(WxProbe.Unavailable("ActiveSky not connected")).Object,
            Provider(WxProbe.NoData("gateway has no METAR for YSSY")).Object).ProbeAsync("YSSY");

        Assert.Equal(WxProbeStatus.NoData, probe.Status);
        Assert.Contains("gateway has no METAR for YSSY", probe.Detail);
    }

    [Fact]
    public async Task ThrowingTier_ContributesItsMessageToTheDetail()
    {
        var first = new Mock<IWxProvider>();
        first.Setup(p => p.ProbeAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var probe = await Composite(null, first.Object).ProbeAsync("YSSY");

        Assert.Equal(WxProbeStatus.Unavailable, probe.Status);
        Assert.Contains("boom", probe.Detail);
    }

    // ── ATIS/runway backfill (unchanged behaviour) ──────────────────────────────────────

    [Fact]
    public async Task Winner_BackfillsAtisAndRunway_FromSayIntentionsCache()
    {
        var store = new WeatherStore();
        store.Update(s => s with
        {
            Departure = new AirportWeather(
                "YSSY", "Sydney information Juliett.", "", "", "16R", null, null),
        });

        var probe = await Composite(store, Provider(WxProbe.Found(Observation)).Object).ProbeAsync("YSSY");

        Assert.Equal(Observation.RawMetar, probe.Facts.RawMetar); // winner's METAR kept
        Assert.Equal("J", probe.Facts.AtisLetter);                // backfilled — no network call
        Assert.Equal("16R", probe.Facts.ActiveRunway);
    }

    [Fact]
    public async Task Backfill_NeverOverridesWinnerFields()
    {
        var store = new WeatherStore();
        store.Update(s => s with
        {
            Arrival = new AirportWeather("YMML", "information Bravo", "", "", "27", null, null),
        });
        var winner = Observation with { AtisLetter = "K", ActiveRunway = "34" };

        var probe = await Composite(store, Provider(WxProbe.Found(winner)).Object).ProbeAsync("YMML");

        Assert.Equal("K", probe.Facts.AtisLetter);
        Assert.Equal("34", probe.Facts.ActiveRunway);
    }

    [Fact]
    public async Task Backfill_IgnoresCacheForOtherAirports()
    {
        var store = new WeatherStore();
        store.Update(s => s with
        {
            Departure = new AirportWeather("YMML", "information Bravo", "", "", "27", null, null),
        });

        var probe = await Composite(store, Provider(WxProbe.Found(Observation)).Object).ProbeAsync("YSSY");

        Assert.Null(probe.Facts.AtisLetter);
        Assert.Null(probe.Facts.ActiveRunway);
    }
}
