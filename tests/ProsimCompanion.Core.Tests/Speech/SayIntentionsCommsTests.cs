using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Radios;
using ProsimCompanion.Speech.SayIntentions;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Radios tile (owner request 2026-10-25): the getWX comms list and the airport whose
/// stations the tile shows.</summary>
public sealed class SayIntentionsCommsTests
{
    [Fact]
    public void Parse_RootComms_TypeFreqCallsign()
    {
        const string body = """
            {"icao":"EFHK","comms":[
              {"type":"ATIS","freq":"135.075","callsign":"Helsinki ATIS"},
              {"type":"CLEARANCE","freq":"121.8"},
              {"type":"GROUND","freq":"118.125","callsign":"Helsinki Ground"},
              {"type":"TOWER","freq":119.7}]}
            """;

        var stations = SayIntentionsComms.Parse(body);

        Assert.Equal(4, stations.Count);
        Assert.Equal(new AtcStation("ATIS", "Helsinki ATIS", 135_075), stations[0]);
        Assert.Equal(new AtcStation("CLEARANCE", "", 121_800), stations[1]);
        Assert.Equal(new AtcStation("GROUND", "Helsinki Ground", 118_125), stations[2]);
        Assert.Equal(new AtcStation("TOWER", "", 119_700), stations[3]);
    }

    [Fact]
    public void Parse_CommsNestedOneLevel_OrInsideAirportsArray()
    {
        var nested = SayIntentionsComms.Parse("""{"airport":{"icao":"EGLL","comms":[{"type":"TOWER","freq":"118.5"}]}}""");
        var inAirports = SayIntentionsComms.Parse("""{"airports":[{"airport":"EGLL","comms":[{"type":"GROUND","freq":"121.9"}]}]}""");

        Assert.Equal([new AtcStation("TOWER", "", 118_500)], nested);
        Assert.Equal([new AtcStation("GROUND", "", 121_900)], inAirports);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"comms":"none"}""")]
    [InlineData("""{"comms":[{"type":"TOWER","freq":"abc"},{"freq":"118.5"},{"type":"CTR","freq":"99.1"}]}""")]
    public void Parse_MalformedOrEmpty_YieldsNoStations(string body)
        => Assert.Empty(SayIntentionsComms.Parse(body));

    [Theory]
    [InlineData(FlightPhase.ColdAndDark, "EFHK")]
    [InlineData(FlightPhase.Preflight, "EFHK")]
    [InlineData(FlightPhase.TaxiOut, "EFHK")]
    [InlineData(FlightPhase.Cruise, "EFHK")]
    [InlineData(FlightPhase.Descent, "EGLL")]
    [InlineData(FlightPhase.Approach, "EGLL")]
    [InlineData(FlightPhase.TaxiIn, "EGLL")]
    [InlineData(FlightPhase.Shutdown, "EGLL")]
    public void LocalIcao_OriginUntilDescent_ThenDestination(FlightPhase phase, string expected)
        => Assert.Equal(expected, SayIntentionsFrequencyMonitor.LocalIcao(phase, "efhk", "EGLL"));

    [Fact]
    public void LocalIcao_FallsBackToTheOtherAirport_AndEmptyWithoutAnOfp()
    {
        Assert.Equal("EGLL", SayIntentionsFrequencyMonitor.LocalIcao(FlightPhase.Preflight, "", "EGLL"));
        Assert.Equal("EFHK", SayIntentionsFrequencyMonitor.LocalIcao(FlightPhase.Descent, "EFHK", null));
        Assert.Equal("", SayIntentionsFrequencyMonitor.LocalIcao(FlightPhase.Preflight, null, null));
    }

    [Theory]
    [InlineData("118.125", 118_125)]
    [InlineData("119.7", 119_700)]
    [InlineData("118.12", 118_125)]
    [InlineData("118.17", 118_175)]
    [InlineData("136.990", 136_990)]
    public void RadioFrequency_ToKhz_ReadsCommonForms(string text, int expected)
        => Assert.Equal(expected, RadioFrequency.ToKhz(text));

    [Theory]
    [InlineData("117.9")]
    [InlineData("137.0")]
    [InlineData("EFIN")]
    [InlineData("")]
    [InlineData(null)]
    public void RadioFrequency_ToKhz_RejectsOutOfBandAndText(string? text)
        => Assert.Null(RadioFrequency.ToKhz(text));

    [Fact]
    public void RadioFrequency_Format_ThreeDecimals_DashesForUnknown()
    {
        Assert.Equal("118.125", RadioFrequency.Format(118_125));
        Assert.Equal("119.700", RadioFrequency.Format(119_700));
        Assert.Equal("---.---", RadioFrequency.Format(0));
    }
}
