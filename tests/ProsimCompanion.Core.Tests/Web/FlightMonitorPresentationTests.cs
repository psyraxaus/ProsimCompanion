using System.Globalization;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Airports;
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

    // ---- flight progress figures and the route strip (issue #145) ----

    private static readonly AirportLocation StripOrigin = new("AAAA", new GeoPoint(0, 10), 0, "test");
    private static readonly AirportLocation StripDestination = new("BBBB", new GeoPoint(0, 20), 0, "test");

    private static FlightProgressSnapshot Located(double along, double cross, double? track = 90, double? descent = null)
        => new()
        {
            Position = new GeoPoint(0, 10),
            Origin = StripOrigin,
            Destination = StripDestination,
            RouteDistanceNm = 600,
            AlongTrackNm = along,
            CrossTrackNm = cross,
            TrackTrueDeg = track,
            DescentDistanceNm = descent,
            Fraction = along / 600,
            FractionBasis = ProgressBasis.Position,
        };

    [Theory]
    [InlineData(311.6, "312 NM")]
    [InlineData(2991.2, "2,991 NM")]
    [InlineData(0.2, "0 NM")]
    [InlineData(null, "—")]
    public void Distance_IsWholeMiles(double? nm, string expected)
        => Assert.Equal(expected, FlightMonitorPresentation.Distance(nm));

    [Theory]
    [InlineData(14.4, "14 MIN")]
    [InlineData(0.0, "NOW")]
    [InlineData(null, "—")]
    public void MinutesToTod_Formats(double? minutes, string expected)
        => Assert.Equal(expected, FlightMonitorPresentation.MinutesToTod(minutes));

    [Fact]
    public void ProgressCaption_SaysWhereTheFiguresCameFrom()
    {
        Assert.Equal("Great-circle direct · from position", FlightMonitorPresentation.ProgressCaption(Located(100, 0)));
        Assert.Equal("Time-based · no aircraft position",
            FlightMonitorPresentation.ProgressCaption(new FlightProgressSnapshot { Fraction = 0.4, FractionBasis = ProgressBasis.Time }));
        Assert.Equal("Time-based · airport position unknown",
            FlightMonitorPresentation.ProgressCaption(new FlightProgressSnapshot
            {
                Position = new GeoPoint(1, 1), Fraction = 0.4, FractionBasis = ProgressBasis.Time,
            }));
        Assert.Equal("No progress data", FlightMonitorPresentation.ProgressCaption(FlightProgressSnapshot.Empty));
        Assert.Equal("GS", FlightMonitorPresentation.BasisTag(ProgressBasis.Position));
        Assert.Equal("PLAN", FlightMonitorPresentation.BasisTag(ProgressBasis.Time));
    }

    [Fact]
    public void RouteStrip_PlacesTheAircraftAlongTheLine_OnOneScale()
    {
        // 600 nm across 1,120 units: half way is the middle of the strip, on the line.
        var strip = FlightMonitorPresentation.RouteStrip(Located(along: 300, cross: 0));

        Assert.Equal(600, strip.MarkerX!.Value, 1);
        Assert.Equal(RouteStripView.LineY, strip.MarkerY!.Value, 1);
        Assert.Equal(600, strip.FlownX!.Value, 1);
        Assert.Equal(0, strip.MarkerRotationDeg, 1);    // tracking 090 along a 090 route
    }

    // Owner decision 2026-10-05: the plane rides the line — an airway route a few miles off
    // the great circle used to put the marker a few px below it, which read as a drawing fault.
    [Theory]
    [InlineData(10.0)]
    [InlineData(-500.0)]
    [InlineData(0.0)]
    public void RouteStrip_OffRoute_KeepsTheMarkerOnTheLine(double cross)
    {
        var strip = FlightMonitorPresentation.RouteStrip(Located(along: 300, cross: cross));

        Assert.Equal(RouteStripView.LineY, strip.MarkerY);
        Assert.Equal(600, strip.MarkerX!.Value, 1);
    }

    // Owner decision 2026-10-04: the plane points along the strip towards the destination for
    // the whole journey — the live track (even the reciprocal, or none at all) never turns it.
    [Theory]
    [InlineData(120.0)]
    [InlineData(60.0)]
    [InlineData(270.0)]
    [InlineData(null)]
    public void RouteStrip_AlwaysPointsTheMarkerAtTheDestination_WhateverTheTrack(double? track)
    {
        var strip = FlightMonitorPresentation.RouteStrip(Located(along: 300, cross: 0, track: track));

        Assert.Equal(0, strip.MarkerRotationDeg);
    }

    [Fact]
    public void RouteStrip_BeforeTheOriginOrPastTheDestination_KeepsTheFlownPartOnTheLine()
    {
        var behind = FlightMonitorPresentation.RouteStrip(Located(along: -30, cross: 0));
        var beyond = FlightMonitorPresentation.RouteStrip(Located(along: 660, cross: 0));

        Assert.Equal(RouteStripView.OriginX, behind.FlownX!.Value, 1);
        Assert.Equal(RouteStripView.DestinationX, beyond.FlownX!.Value, 1);
        Assert.InRange(behind.MarkerX!.Value, RouteStripView.Edge, RouteStripView.OriginX);
        Assert.InRange(beyond.MarkerX!.Value, RouteStripView.DestinationX, RouteStripView.Width - RouteStripView.Edge);
    }

    [Fact]
    public void RouteStrip_MarksTheTopOfDescentEstimate()
    {
        // 105 nm of descent on a 600 nm leg: the tick sits 495 nm along.
        var strip = FlightMonitorPresentation.RouteStrip(Located(along: 300, cross: 0, descent: 105));
        var none = FlightMonitorPresentation.RouteStrip(Located(along: 300, cross: 0));
        var longerThanTheLeg = FlightMonitorPresentation.RouteStrip(Located(along: 300, cross: 0, descent: 700));

        Assert.Equal(RouteStripView.OriginX + (495.0 / 600 * 1120), strip.TodX!.Value, 1);
        Assert.Null(none.TodX);
        Assert.Null(longerThanTheLeg.TodX);
    }

    [Fact]
    public void RouteStrip_WithoutAPosition_RidesTheLineAtTheTimeFraction()
    {
        var strip = FlightMonitorPresentation.RouteStrip(
            new FlightProgressSnapshot { Fraction = 0.25, FractionBasis = ProgressBasis.Time });
        var nothing = FlightMonitorPresentation.RouteStrip(FlightProgressSnapshot.Empty);

        Assert.Equal(RouteStripView.OriginX + (0.25 * 1120), strip.MarkerX!.Value, 1);
        Assert.Equal(RouteStripView.LineY, strip.MarkerY);
        Assert.Null(strip.TodX);
        Assert.Null(nothing.MarkerX);
        Assert.Null(nothing.FlownX);
    }

    [Fact]
    public void RouteStrip_Px_IsInvariant()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal("123.5", RouteStripView.Px(123.46));
            Assert.Equal("-30", RouteStripView.Px(-30));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void WeatherCardFormat_Figures()
    {
        var facts = MetarParser.ToFacts("EGLL 221020Z 25014G24KT 4000 -RA BKN030 17/09 Q1012", "J");

        Assert.Equal("250°/14 KT", WeatherCardFormat.Wind(facts));
        Assert.Equal("4 KM", WeatherCardFormat.Visibility(facts));
        Assert.Equal("CEILING 3,000 FT", WeatherCardFormat.Ceiling(facts));
        Assert.Equal("+17°C", WeatherCardFormat.Temperature(facts));
        Assert.Equal("Q1012", WeatherCardFormat.Qnh(facts));
        Assert.Equal("cloud-rain", WeatherCardFormat.SkyIcon(SkyCondition.Rain));
        Assert.Equal("CALM", WeatherCardFormat.Wind(MetarParser.ToFacts("EGLL 221020Z 00000KT 9999 FEW020 17/09 Q1012")));
        Assert.Equal("VRB/05 KT", WeatherCardFormat.Wind(MetarParser.ToFacts("EGLL 221020Z VRB05KT 9999 FEW020 17/09 Q1012")));
    }
}
