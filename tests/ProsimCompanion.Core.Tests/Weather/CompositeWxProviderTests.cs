using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Weather;
using Xunit;

namespace ProsimCompanion.Core.Tests.Weather;

public sealed class CompositeWxProviderTests
{
    private static readonly WxFacts Observation = MetarParser.ToFacts("YSSY 070800Z 27012KT CAVOK 22/10 Q1013");

    private static Mock<IWxProvider> Provider(WxFacts result)
    {
        var mock = new Mock<IWxProvider>();
        mock.Setup(p => p.GetAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        return mock;
    }

    private static CompositeWxProvider Composite(WeatherStore? store = null, params IWxProvider[] providers)
        => new(providers, store ?? new WeatherStore(), NullLogger<CompositeWxProvider>.Instance);

    [Fact]
    public async Task FirstTierWithMetar_Wins_LowerTiersNotCalled()
    {
        var first = Provider(Observation);
        var second = Provider(MetarParser.ToFacts("YSSY 070800Z 00000KT CAVOK 25/10 Q1020"));

        var facts = await Composite(null, first.Object, second.Object).GetAsync("YSSY");

        Assert.Equal(Observation.RawMetar, facts.RawMetar);
        second.Verify(p => p.GetAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EmptyTier_FallsThroughToNext()
    {
        var first = Provider(WxFacts.None);
        var second = Provider(Observation);

        var facts = await Composite(null, first.Object, second.Object).GetAsync("YSSY");

        Assert.Equal(Observation.RawMetar, facts.RawMetar);
        first.Verify(p => p.GetAsync("YSSY", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ThrowingTier_IsSkipped_ChainSurvives()
    {
        var first = new Mock<IWxProvider>();
        first.Setup(p => p.GetAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var facts = await Composite(null, first.Object, Provider(Observation).Object).GetAsync("YSSY");

        Assert.Equal(Observation.RawMetar, facts.RawMetar);
    }

    [Fact]
    public async Task AllTiersEmpty_ReturnsNone()
        => Assert.Equal(WxFacts.None,
            await Composite(null, Provider(WxFacts.None).Object).GetAsync("YSSY"));

    [Fact]
    public async Task Winner_BackfillsAtisAndRunway_FromSayIntentionsCache()
    {
        var store = new WeatherStore();
        store.Update(s => s with
        {
            Departure = new AirportWeather(
                "YSSY", "Sydney information Juliett.", "", "", "16R", null, null),
        });

        var facts = await Composite(store, Provider(Observation).Object).GetAsync("YSSY");

        Assert.Equal(Observation.RawMetar, facts.RawMetar); // winner's METAR kept
        Assert.Equal("J", facts.AtisLetter);                // backfilled — no network call
        Assert.Equal("16R", facts.ActiveRunway);
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

        var facts = await Composite(store, Provider(winner).Object).GetAsync("YMML");

        Assert.Equal("K", facts.AtisLetter);
        Assert.Equal("34", facts.ActiveRunway);
    }

    [Fact]
    public async Task Backfill_IgnoresCacheForOtherAirports()
    {
        var store = new WeatherStore();
        store.Update(s => s with
        {
            Departure = new AirportWeather("YMML", "information Bravo", "", "", "27", null, null),
        });

        var facts = await Composite(store, Provider(Observation).Object).GetAsync("YSSY");

        Assert.Null(facts.AtisLetter);
        Assert.Null(facts.ActiveRunway);
    }
}
