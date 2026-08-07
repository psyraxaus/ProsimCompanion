using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Weather;
using ProsimCompanion.Speech.SayIntentions;
using Xunit;

namespace ProsimCompanion.Core.Tests.Weather;

public sealed class SayIntentionsWeatherParseTests
{
    // ---- getWX airports[] ----

    [Fact]
    public void ParseAirports_FullShape_MapsEveryField()
    {
        const string body = """
            {"airports":[
              {"airport":"YSSY","atis":"Sydney information Juliett.","metar":"YSSY 070800Z 27012KT CAVOK 22/10 Q1013",
               "taf":"TAF YSSY 070500Z 0706/0812 27012KT CAVOK","active_runway":"16R",
               "wind_direction":270,"wind_speed":12},
              {"airport":"YMML","atis":"","metar":"YMML 070800Z 35010KT 9999 SCT030 18/09 Q1017",
               "taf":"","active_runway":"34","wind_direction":"350","wind_speed":"10"}
            ]}
            """;

        var result = SayIntentionsWeatherService.ParseAirports(body);

        Assert.Equal(2, result.Count);
        var yssy = result[0];
        Assert.Equal("YSSY", yssy.Airport);
        Assert.Equal("Sydney information Juliett.", yssy.Atis);
        Assert.StartsWith("YSSY 070800Z", yssy.Metar, StringComparison.Ordinal);
        Assert.StartsWith("TAF YSSY", yssy.Taf, StringComparison.Ordinal);
        Assert.Equal("16R", yssy.ActiveRunway);
        Assert.Equal(270, yssy.WindDirection);
        Assert.Equal(12, yssy.WindSpeed);

        // Numeric strings (seen in live SI responses) parse too.
        Assert.Equal(350, result[1].WindDirection);
        Assert.Equal(10, result[1].WindSpeed);
    }

    [Fact]
    public void ParseAirports_MissingFields_BecomeEmptyOrNull()
    {
        var result = SayIntentionsWeatherService.ParseAirports("""{"airports":[{"airport":"YSSY"}]}""");

        var wx = Assert.Single(result);
        Assert.Equal("YSSY", wx.Airport);
        Assert.Equal("", wx.Atis);
        Assert.Equal("", wx.Metar);
        Assert.Equal("", wx.Taf);
        Assert.Equal("", wx.ActiveRunway);
        Assert.Null(wx.WindDirection);
        Assert.Null(wx.WindSpeed);
    }

    [Theory]
    [InlineData("""{"error":"no airports"}""")]
    [InlineData("""{"airports":"nope"}""")]
    [InlineData("not json at all")]
    [InlineData("")]
    public void ParseAirports_MalformedBody_ReturnsEmpty(string body)
        => Assert.Empty(SayIntentionsWeatherService.ParseAirports(body));

    [Fact]
    public void ParseAirports_FeedsCompositeBackfill()
    {
        // End-to-end shape check: a parsed SI entry produces facts the composite can serve.
        var result = SayIntentionsWeatherService.ParseAirports("""
            {"airports":[{"airport":"YSSY","atis":"information Juliett","metar":"YSSY 070800Z 27012KT CAVOK 22/10 Q1013","active_runway":"16R"}]}
            """);

        var facts = SayIntentionsStoreWxProvider.ToFacts(result[0]);
        Assert.Equal("J", facts.AtisLetter);
        Assert.Equal("16R", facts.ActiveRunway);
        Assert.Equal(1013, facts.QnhHpa);
    }

    // ---- getCurrentFrequencies CPDLC scan ----

    [Fact]
    public void ParseCpdlcStation_FindsEntryCaseInsensitively()
    {
        const string body = """
            {"frequencies":[
              {"station":"Tower","freq":"120.500"},
              {"station":"cpdlc","freq":"EFIN"},
              {"station":"Ground","freq":"121.700"}
            ]}
            """;

        Assert.Equal("EFIN", SayIntentionsWeatherService.ParseCpdlcStation(body));
    }

    [Theory]
    [InlineData("""{"frequencies":[{"station":"Tower","freq":"120.500"}]}""")] // no CPDLC entry
    [InlineData("""{"frequencies":[]}""")]
    [InlineData("""{"other":true}""")]
    [InlineData("not json")]
    [InlineData("")]
    public void ParseCpdlcStation_AbsentOrMalformed_IsEmptyString(string body)
        => Assert.Equal("", SayIntentionsWeatherService.ParseCpdlcStation(body));

    [Fact]
    public void ParseCpdlcStation_MissingFreqOnCpdlcEntry_IsEmptyString()
        => Assert.Equal("", SayIntentionsWeatherService.ParseCpdlcStation(
            """{"frequencies":[{"station":"CPDLC"}]}"""));
}
