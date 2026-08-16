using ProsimCompanion.Core.Weather;
using Xunit;

namespace ProsimCompanion.Core.Tests.Weather;

/// <summary>The UI failure-message mapping (issue #62): "no data for this ICAO" and "fetch
/// failed" must read differently, and a found observation must produce no message at all.</summary>
public sealed class WxProbeTests
{
    [Fact]
    public void Found_HasNoFailureMessage()
    {
        var probe = WxProbe.Found(MetarParser.ToFacts("YSSY 070800Z 27012KT CAVOK 22/10 Q1013"));
        Assert.Null(probe.FailureMessage("YSSY"));
    }

    [Fact]
    public void NoData_SaysNoMetarForIcao_WithDetail()
    {
        var probe = WxProbe.NoData("ActiveSky has no METAR for YSSY; gateway has no METAR for YSSY");
        Assert.Equal(
            "No METAR for YSSY (ActiveSky has no METAR for YSSY; gateway has no METAR for YSSY)",
            probe.FailureMessage("YSSY"));
    }

    [Fact]
    public void Unavailable_SaysFetchFailed_WithDetail()
    {
        var probe = WxProbe.Unavailable("gateway HTTP 500; ActiveSky not connected");
        Assert.Equal(
            "Weather fetch failed (gateway HTTP 500; ActiveSky not connected)",
            probe.FailureMessage("YSSY"));
    }

    [Fact]
    public void MissingDetail_OmitsTheParenthetical()
    {
        Assert.Equal("No METAR for YSSY", WxProbe.NoData(null).FailureMessage("YSSY"));
        Assert.Equal("Weather fetch failed", WxProbe.Unavailable(null).FailureMessage("YSSY"));
        Assert.Equal("Weather fetch failed", WxProbe.Unavailable("  ").FailureMessage("YSSY"));
    }
}
