using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Boarding;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Weather;
using ProsimCompanion.Web.Components;
using Xunit;

namespace ProsimCompanion.Core.Tests.Web;

public sealed class FlightMonitorPresentationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 10, 45, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(FlightPhase.Unknown, MonitorMode.Gate)]
    [InlineData(FlightPhase.ColdAndDark, MonitorMode.Gate)]
    [InlineData(FlightPhase.Preflight, MonitorMode.Gate)]
    [InlineData(FlightPhase.Departure, MonitorMode.Gate)]
    [InlineData(FlightPhase.PushbackAndStart, MonitorMode.Flight)]
    [InlineData(FlightPhase.TaxiOut, MonitorMode.Flight)]
    [InlineData(FlightPhase.Cruise, MonitorMode.Flight)]
    [InlineData(FlightPhase.LandingRollout, MonitorMode.Flight)]
    [InlineData(FlightPhase.TaxiIn, MonitorMode.Arrival)]
    [InlineData(FlightPhase.Shutdown, MonitorMode.Arrival)]
    public void Mode_FollowsThePhase(FlightPhase phase, MonitorMode expected)
        => Assert.Equal(expected, FlightMonitorPresentation.Mode(phase));

    [Fact]
    public void StateWord_GateModeReadsTheGateMonitor()
    {
        Assert.Equal("BOARDING", FlightMonitorPresentation.StateWord(MonitorMode.Gate, GateState.Boarding, FlightPhase.Departure, false, false));
        Assert.Equal("FINAL CALL", FlightMonitorPresentation.StateWord(MonitorMode.Gate, GateState.FinalCall, FlightPhase.Departure, false, false));
        Assert.Equal("GATE CLOSED", FlightMonitorPresentation.StateWord(MonitorMode.Gate, GateState.ClosedAfterBoarding, FlightPhase.Departure, false, false));
    }

    [Fact]
    public void StateWord_FlightModeReadsThePhase()
        => Assert.Equal("CRUISE", FlightMonitorPresentation.StateWord(MonitorMode.Flight, GateState.ClosedAfterBoarding, FlightPhase.Cruise, false, false));

    [Fact]
    public void StateWord_ArrivalModeReadsTheDeboarding()
    {
        Assert.Equal("TAXI IN", FlightMonitorPresentation.StateWord(MonitorMode.Arrival, GateState.ClosedAfterBoarding, FlightPhase.TaxiIn, false, false));
        Assert.Equal("ON BLOCKS", FlightMonitorPresentation.StateWord(MonitorMode.Arrival, GateState.ClosedAfterBoarding, FlightPhase.Shutdown, false, false));
        Assert.Equal("DEBOARDING", FlightMonitorPresentation.StateWord(MonitorMode.Arrival, GateState.ClosedAfterBoarding, FlightPhase.Shutdown, true, false));
        Assert.Equal("ARRIVED", FlightMonitorPresentation.StateWord(MonitorMode.Arrival, GateState.ClosedAfterBoarding, FlightPhase.Shutdown, true, true));
    }

    [Fact]
    public void FlightProgress_IsTimeBased_AndPins()
    {
        var times = new FlightTimesSnapshot(T0, T0.AddMinutes(15), null, null);
        var eet = TimeSpan.FromMinutes(120);

        Assert.Equal(0.5, FlightMonitorPresentation.FlightProgress(times, eet, T0.AddMinutes(60))!.Value, 3);
        Assert.Equal(1.0, FlightMonitorPresentation.FlightProgress(times, eet, T0.AddMinutes(200))!.Value, 3);
        Assert.Null(FlightMonitorPresentation.FlightProgress(FlightTimesSnapshot.Empty, eet, T0));
        Assert.Null(FlightMonitorPresentation.FlightProgress(times, null, T0));
    }

    [Fact]
    public void Eta_UsesTheBestAnchor()
    {
        var ofp = new OfpData { EstimatedEnroute = TimeSpan.FromMinutes(120) };
        var std = T0;

        Assert.Equal(T0.AddMinutes(120), FlightMonitorPresentation.Eta(FlightTimesSnapshot.Empty, ofp, std));
        Assert.Equal(T0.AddMinutes(130), FlightMonitorPresentation.Eta(new FlightTimesSnapshot(T0.AddMinutes(10), null, null, null), ofp, std));
        Assert.Equal(T0.AddMinutes(145), FlightMonitorPresentation.Eta(new FlightTimesSnapshot(T0.AddMinutes(10), T0.AddMinutes(25), null, null), ofp, std));
        Assert.Equal(T0.AddMinutes(150), FlightMonitorPresentation.Eta(new FlightTimesSnapshot(T0, T0.AddMinutes(15), T0.AddMinutes(140), T0.AddMinutes(150)), ofp, std));
        Assert.Null(FlightMonitorPresentation.Eta(FlightTimesSnapshot.Empty, null, std));
    }

    [Theory]
    [InlineData(135, "2h 15m")]
    [InlineData(48, "48m")]
    [InlineData(60, "1h 00m")]
    public void Duration_Formats(int minutes, string expected)
        => Assert.Equal(expected, FlightMonitorPresentation.Duration(TimeSpan.FromMinutes(minutes)));

    [Fact]
    public void Duration_AndClock_HandleUnknown()
    {
        Assert.Equal("—", FlightMonitorPresentation.Duration(null));
        Assert.Equal("—", FlightMonitorPresentation.Clock(null));
        Assert.Equal("13:00Z", FlightMonitorPresentation.Clock(new DateTimeOffset(2026, 9, 23, 13, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void WeatherCardFormat_Figures()
    {
        var facts = MetarParser.ToFacts("EGLL 221020Z 25014G24KT 4000 -RA BKN030 17/09 Q1012", "J");

        Assert.Equal("250°/14 KT", WeatherCardFormat.Wind(facts));
        Assert.Equal("4 KM", WeatherCardFormat.Visibility(facts));
        Assert.Equal("CIG 3,000 FT", WeatherCardFormat.Ceiling(facts));
        Assert.Equal("+17°C", WeatherCardFormat.Temperature(facts));
        Assert.Equal("Q1012", WeatherCardFormat.Qnh(facts));
        Assert.Equal("cloud-rain", WeatherCardFormat.SkyIcon(SkyCondition.Rain));
        Assert.Equal("CALM", WeatherCardFormat.Wind(MetarParser.ToFacts("EGLL 221020Z 00000KT 9999 FEW020 17/09 Q1012")));
        Assert.Equal("VRB/05 KT", WeatherCardFormat.Wind(MetarParser.ToFacts("EGLL 221020Z VRB05KT 9999 FEW020 17/09 Q1012")));
    }
}
