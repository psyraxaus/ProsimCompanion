using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Airports;
using ProsimCompanion.Core.Flight;
using Xunit;

namespace ProsimCompanion.Core.Tests.Flight;

public sealed class FlightProgressCoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    // A 600 nm leg due east along the equator: one degree of longitude is 60.04 nm, so the
    // arithmetic in every test can be checked by eye.
    private static readonly AirportLocation Origin = new("AAAA", new GeoPoint(0, 10), 0, "test");
    private static readonly AirportLocation Destination = new("BBBB", new GeoPoint(0, 20), 1000, "test");
    private const double DegreeNm = 60.0405;

    private static readonly OfpData Ofp = new()
    {
        OriginIcao = "AAAA",
        DestinationIcao = "BBBB",
        EstimatedEnroute = TimeSpan.FromMinutes(120),
        CruiseFlightLevel = 360,
    };

    private static readonly FlightTimesSnapshot Airborne = new(T0, T0.AddMinutes(15), null, null);

    private static FlightDataSnapshot Data(double? lon, double groundSpeed = 450, double altitude = 36000, double fmsCruise = 36000)
        => new()
        {
            IsValid = true,
            IsReady = true,
            GroundSpeedKt = groundSpeed,
            AltitudeFt = altitude,
            FmsCruiseAltFt = fmsCruise,
            Position = lon is { } l ? new GeoPoint(0, l) : null,
            TrackTrueDeg = 90,
        };

    private static FlightProgressInputs Inputs(
        FlightPhase phase,
        FlightDataSnapshot? data,
        bool live = true,
        FlightTimesSnapshot? times = null,
        AirportLocation? origin = null,
        AirportLocation? destination = null,
        bool locateAirports = true)
        => new(phase, live, data, times ?? Airborne, Ofp, T0,
            locateAirports ? origin ?? Origin : null,
            locateAirports ? destination ?? Destination : null,
            null);

    // ---- valid ----

    [Fact]
    public void ValidPosition_GivesPositionBasedDistancesFractionAndEta()
    {
        var core = new FlightProgressCore();

        var (snapshot, _) = core.Evaluate(Inputs(FlightPhase.Cruise, Data(lon: 13)), T0.AddMinutes(60), 10);

        Assert.Equal(10 * DegreeNm, snapshot.RouteDistanceNm!.Value, 1);
        Assert.Equal(3 * DegreeNm, snapshot.DistanceFlownNm!.Value, 1);
        Assert.Equal(7 * DegreeNm, snapshot.DistanceToGoNm!.Value, 1);
        Assert.Equal(0.3, snapshot.Fraction!.Value, 3);
        Assert.Equal(ProgressBasis.Position, snapshot.FractionBasis);
        Assert.Equal(ProgressBasis.Position, snapshot.EtaBasis);
        // 420.3 nm at 450 kt is 56 min; the ETA is cut to the whole minute.
        Assert.Equal(T0.AddMinutes(60 + 56), snapshot.EtaUtc);
        Assert.Equal(3 * DegreeNm, snapshot.AlongTrackNm!.Value, 1);
        Assert.Equal(0, snapshot.CrossTrackNm!.Value, 1);
    }

    [Fact]
    public void Fraction_StaysInsideZeroToOne_WhenTheAircraftIsOffTheDirectLine()
    {
        var core = new FlightProgressCore();
        // Behind the origin (a SID that leaves the wrong way) and beyond the destination.
        var behind = core.Evaluate(Inputs(FlightPhase.Climb, Data(lon: 9)), T0, 10).Snapshot;
        var beyond = core.Evaluate(Inputs(FlightPhase.Approach, Data(lon: 21)), T0, 10).Snapshot;

        Assert.InRange(behind.Fraction!.Value, 0, 0.1);
        Assert.InRange(beyond.Fraction!.Value, 0.9, 1);
    }

    [Fact]
    public void OnTheGround_EtaIsTheTimeBasedOne_EvenWithAPosition()
    {
        var core = new FlightProgressCore();
        var taxi = Data(lon: 10, groundSpeed: 12);

        var (snapshot, _) = core.Evaluate(
            Inputs(FlightPhase.TaxiOut, taxi, times: new FlightTimesSnapshot(T0, null, null, null)), T0.AddMinutes(5), 10);

        Assert.Equal(ProgressBasis.Position, snapshot.FractionBasis);
        Assert.Equal(ProgressBasis.Time, snapshot.EtaBasis);
        Assert.Equal(T0.AddMinutes(120), snapshot.EtaUtc);
        Assert.Null(snapshot.MinutesToTod);
    }

    [Fact]
    public void OnBlocksStamp_IsTheEta_OnceTheLegIsOver()
    {
        var core = new FlightProgressCore();
        var times = new FlightTimesSnapshot(T0, T0.AddMinutes(15), T0.AddMinutes(110), T0.AddMinutes(118));

        var (snapshot, _) = core.Evaluate(Inputs(FlightPhase.Shutdown, Data(lon: 20, groundSpeed: 0), times: times), T0.AddMinutes(130), 10);

        Assert.Equal(T0.AddMinutes(118), snapshot.EtaUtc);
        Assert.Equal(ProgressBasis.Time, snapshot.EtaBasis);
        Assert.Equal(1.0, snapshot.Fraction!.Value, 3);
    }

    // ---- invalid ----

    [Fact]
    public void NoPosition_FallsBackToTheTimeBasedValues()
    {
        var core = new FlightProgressCore();

        var (snapshot, tod) = core.Evaluate(Inputs(FlightPhase.Cruise, Data(lon: null)), T0.AddMinutes(60), 10);

        Assert.Null(snapshot.Position);
        Assert.Null(snapshot.DistanceToGoNm);
        Assert.Null(snapshot.DistanceFlownNm);
        Assert.Equal(0.5, snapshot.Fraction!.Value, 3);                 // 60 of 120 minutes
        Assert.Equal(ProgressBasis.Time, snapshot.FractionBasis);
        Assert.Equal(T0.AddMinutes(15 + 120), snapshot.EtaUtc);         // takeoff + enroute
        Assert.Equal(ProgressBasis.Time, snapshot.EtaBasis);
        Assert.Null(snapshot.MinutesToTod);
        Assert.Null(tod);
        // The route length needs only the airports, so it survives.
        Assert.Equal(10 * DegreeNm, snapshot.RouteDistanceNm!.Value, 1);
    }

    [Fact]
    public void NotLive_IgnoresThePosition()
    {
        // ProSim pushes plausible data with no sim session; a position from it is not evidence.
        var core = new FlightProgressCore();

        var (snapshot, _) = core.Evaluate(Inputs(FlightPhase.Cruise, Data(lon: 13), live: false), T0.AddMinutes(60), 10);

        Assert.Null(snapshot.Position);
        Assert.Equal(ProgressBasis.Time, snapshot.FractionBasis);
    }

    [Fact]
    public void InvalidSample_IgnoresThePosition()
    {
        var core = new FlightProgressCore();
        var stale = Data(lon: 13) with { IsValid = false };

        var (snapshot, _) = core.Evaluate(Inputs(FlightPhase.Cruise, stale), T0.AddMinutes(60), 10);

        Assert.Null(snapshot.DistanceToGoNm);
        Assert.Equal(ProgressBasis.Time, snapshot.FractionBasis);
    }

    [Fact]
    public void NoAirportCoordinates_FallsBackToTime_ButKeepsThePosition()
    {
        var core = new FlightProgressCore();

        var (snapshot, _) = core.Evaluate(Inputs(FlightPhase.Cruise, Data(lon: 13), locateAirports: false), T0.AddMinutes(60), 10);

        Assert.NotNull(snapshot.Position);
        Assert.Null(snapshot.RouteDistanceNm);
        Assert.Equal(ProgressBasis.Time, snapshot.FractionBasis);
        Assert.Equal(ProgressBasis.Time, snapshot.EtaBasis);
    }

    [Fact]
    public void NothingAtAll_IsNoBasis()
    {
        var core = new FlightProgressCore();
        var inputs = new FlightProgressInputs(
            FlightPhase.Preflight, false, null, FlightTimesSnapshot.Empty, null, null, null, null, null);

        var (snapshot, tod) = core.Evaluate(inputs, T0, 10);

        Assert.Null(snapshot.Fraction);
        Assert.Equal(ProgressBasis.None, snapshot.FractionBasis);
        Assert.Null(snapshot.EtaUtc);
        Assert.Equal(ProgressBasis.None, snapshot.EtaBasis);
        Assert.Null(tod);
    }

    [Fact]
    public void SameAirportBothEnds_UsesTheTimeFraction()
    {
        // A circuit: distance from the field says nothing about how far along the leg is.
        var core = new FlightProgressCore();

        var (snapshot, _) = core.Evaluate(
            Inputs(FlightPhase.Cruise, Data(lon: 11), destination: Origin with { Icao = "AAAA" }), T0.AddMinutes(60), 10);

        Assert.Equal(ProgressBasis.Time, snapshot.FractionBasis);
        Assert.NotNull(snapshot.DistanceToGoNm);
    }

    // ---- lost and regained ----

    [Fact]
    public void PositionLostMidFlight_ThenRegained_SwitchesBasisBothWays()
    {
        var core = new FlightProgressCore();

        var before = core.Evaluate(Inputs(FlightPhase.Cruise, Data(lon: 13)), T0.AddMinutes(40), 10).Snapshot;
        var lost = core.Evaluate(Inputs(FlightPhase.Cruise, Data(lon: null)), T0.AddMinutes(41), 10).Snapshot;
        var regained = core.Evaluate(Inputs(FlightPhase.Cruise, Data(lon: 13.2)), T0.AddMinutes(42), 10).Snapshot;

        Assert.Equal(ProgressBasis.Position, before.FractionBasis);
        Assert.Equal(ProgressBasis.Time, lost.FractionBasis);
        Assert.Null(lost.DistanceToGoNm);
        Assert.Equal(ProgressBasis.Position, regained.FractionBasis);
        Assert.Equal(6.8 * DegreeNm, regained.DistanceToGoNm!.Value, 1);
    }

    [Fact]
    public void PositionRegainedInsideTheTodWindow_StillFiresOnce()
    {
        var core = new FlightProgressCore();
        // 36,000 ft to a 1,000 ft field = 105 nm of descent. At 450 kt, 10 min is 75 nm, so
        // the lead is crossed at 180 nm to go (lon 17.002).
        Assert.Null(core.Evaluate(Inputs(FlightPhase.Cruise, Data(lon: 15)), T0, 10).Tod);
        Assert.Null(core.Evaluate(Inputs(FlightPhase.Cruise, Data(lon: null)), T0.AddMinutes(1), 10).Tod);

        var fired = core.Evaluate(Inputs(FlightPhase.Cruise, Data(lon: 17.5)), T0.AddMinutes(20), 10).Tod;
        var again = core.Evaluate(Inputs(FlightPhase.Cruise, Data(lon: 17.6)), T0.AddMinutes(21), 10).Tod;

        Assert.NotNull(fired);
        Assert.Null(again);
    }

    // ---- top of descent ----

    [Theory]
    [InlineData(36000, 0, 108)]
    [InlineData(36000, 1000, 105)]
    [InlineData(10000, 5000, 15)]
    [InlineData(3000, 5000, 0)]   // never negative
    public void DescentDistance_IsThreeMilesPerThousandFeet(double cruise, double elevation, double expected)
        => Assert.Equal(expected, FlightProgressCore.DescentDistanceNm(cruise, elevation), 6);

    [Fact]
    public void Tod_InTheCruise_UsesTheActualAltitudeAndTheDestinationElevation()
    {
        var core = new FlightProgressCore();
        // Cruising at FL380 although the FMS still says 36,000: the aircraft's own level wins.
        var (snapshot, _) = core.Evaluate(Inputs(FlightPhase.Cruise, Data(lon: 13, altitude: 38000)), T0, 10);

        Assert.Equal(111, snapshot.DescentDistanceNm!.Value, 1);                 // (38000 − 1000) / 1000 × 3
        Assert.Equal((7 * DegreeNm) - 111, snapshot.DistanceToTodNm!.Value, 1);
        Assert.Equal(((7 * DegreeNm) - 111) / 450 * 60, snapshot.MinutesToTod!.Value, 1);
    }

    [Fact]
    public void Tod_InTheClimb_UsesThePlannedLevel_FmsFirstThenOfp()
    {
        var core = new FlightProgressCore();

        var fms = core.Evaluate(Inputs(FlightPhase.Climb, Data(lon: 11, altitude: 12000, fmsCruise: 34000)), T0, 10).Snapshot;
        var ofp = core.Evaluate(Inputs(FlightPhase.Climb, Data(lon: 11, altitude: 12000, fmsCruise: 0)), T0, 10).Snapshot;

        Assert.Equal(99, fms.DescentDistanceNm!.Value, 1);    // (34000 − 1000) × 3 / 1000
        Assert.Equal(105, ofp.DescentDistanceNm!.Value, 1);   // OFP FL360
    }

    [Fact]
    public void Tod_UnknownElevation_AssumesSeaLevel()
    {
        var core = new FlightProgressCore();

        var (snapshot, _) = core.Evaluate(
            Inputs(FlightPhase.Cruise, Data(lon: 13), destination: Destination with { ElevationFt = null }), T0, 10);

        Assert.Equal(108, snapshot.DescentDistanceNm!.Value, 1);
    }

    [Fact]
    public void Tod_PastThePoint_ReadsZero_AndIsGoneInTheDescent()
    {
        var core = new FlightProgressCore();

        var past = core.Evaluate(Inputs(FlightPhase.Cruise, Data(lon: 19)), T0, 10).Snapshot;
        var descent = core.Evaluate(Inputs(FlightPhase.Descent, Data(lon: 19, altitude: 20000)), T0, 10).Snapshot;

        Assert.Equal(0, past.DistanceToTodNm);
        Assert.Equal(0, past.MinutesToTod);
        Assert.Null(descent.MinutesToTod);
        Assert.Null(descent.DescentDistanceNm);
    }

    [Fact]
    public void Tod_NoGroundSpeed_NoFigure()
    {
        var core = new FlightProgressCore();

        var (snapshot, _) = core.Evaluate(Inputs(FlightPhase.Cruise, Data(lon: 13, groundSpeed: 0)), T0, 10);

        Assert.Null(snapshot.MinutesToTod);
        Assert.Equal(ProgressBasis.Time, snapshot.EtaBasis);
    }

    [Fact]
    public void TodApproaching_FiresOnceOnTheDownwardCrossing()
    {
        var core = new FlightProgressCore();

        Assert.Null(core.Evaluate(Inputs(FlightPhase.Cruise, Data(lon: 14)), T0, 10).Tod);        // 34 min
        Assert.Null(core.Evaluate(Inputs(FlightPhase.Cruise, Data(lon: 16.9)), T0, 10).Tod);      // 10.8 min

        var fired = core.Evaluate(Inputs(FlightPhase.Cruise, Data(lon: 17.1)), T0, 10).Tod;       // 9.2 min

        Assert.NotNull(fired);
        Assert.Equal(9.2, fired.MinutesToTod, 1);
        Assert.Equal(105, fired.DescentDistanceNm, 1);
        Assert.Equal(1000, fired.DestinationElevationFt);
        Assert.Equal(10, fired.LeadMinutes);
        Assert.Null(core.Evaluate(Inputs(FlightPhase.Cruise, Data(lon: 17.2)), T0, 10).Tod);
        // Ground-speed jitter that takes the figure back over the lead does not re-arm it.
        Assert.Null(core.Evaluate(Inputs(FlightPhase.Cruise, Data(lon: 17.2, groundSpeed: 300)), T0, 10).Tod);
        Assert.Null(core.Evaluate(Inputs(FlightPhase.Cruise, Data(lon: 17.3)), T0, 10).Tod);
    }

    [Fact]
    public void TodApproaching_LeadOfZero_NeverFires_ButTheFigureIsStillComputed()
    {
        var core = new FlightProgressCore();

        core.Evaluate(Inputs(FlightPhase.Cruise, Data(lon: 14)), T0, 0);
        var (snapshot, tod) = core.Evaluate(Inputs(FlightPhase.Cruise, Data(lon: 17.5)), T0, 0);

        Assert.Null(tod);
        Assert.NotNull(snapshot.MinutesToTod);
    }

    [Fact]
    public void TodApproaching_StartedInsideTheWindow_DoesNotFire()
    {
        // The app (re)started in the cruise with T/D already closer than the lead: no
        // crossing was seen, and a second notification for the same descent is worse than none.
        var core = new FlightProgressCore();

        Assert.Null(core.Evaluate(Inputs(FlightPhase.Cruise, Data(lon: 17.5)), T0, 10).Tod);
        Assert.Null(core.Evaluate(Inputs(FlightPhase.Cruise, Data(lon: 17.8)), T0, 10).Tod);
    }

    [Fact]
    public void TodApproaching_ShortHop_FiresOnTheFirstAirborneFigure_WhenTheDepartureWasSeen()
    {
        var core = new FlightProgressCore();
        var near = Destination with { Position = new GeoPoint(0, 12) };   // a 120 nm leg

        // 108 nm to go, 69 nm of descent from FL240: 39 nm = 7.8 min at 300 kt.
        core.Evaluate(Inputs(FlightPhase.TakeoffRoll, Data(lon: 10, groundSpeed: 120), destination: near), T0, 10);
        var fired = core.Evaluate(
            Inputs(FlightPhase.Climb, Data(lon: 10.2, groundSpeed: 300, altitude: 8000, fmsCruise: 24000), destination: near), T0, 10).Tod;

        Assert.NotNull(fired);
    }

    [Fact]
    public void TodApproaching_ReArmsForTheNextLeg()
    {
        var core = new FlightProgressCore();
        core.Evaluate(Inputs(FlightPhase.Cruise, Data(lon: 14)), T0, 10);
        Assert.NotNull(core.Evaluate(Inputs(FlightPhase.Cruise, Data(lon: 17.5)), T0, 10).Tod);

        // Land, turn around, fly the same leg again.
        core.Evaluate(Inputs(FlightPhase.Shutdown, Data(lon: 20, groundSpeed: 0)), T0, 10);
        core.Evaluate(Inputs(FlightPhase.Preflight, Data(lon: 10, groundSpeed: 0)), T0, 10);
        core.Evaluate(Inputs(FlightPhase.Cruise, Data(lon: 14)), T0, 10);

        Assert.NotNull(core.Evaluate(Inputs(FlightPhase.Cruise, Data(lon: 17.5)), T0, 10).Tod);
    }

    [Fact]
    public void Reset_ReArmsTheLatch()
    {
        var core = new FlightProgressCore();
        core.Evaluate(Inputs(FlightPhase.Cruise, Data(lon: 14)), T0, 10);
        Assert.NotNull(core.Evaluate(Inputs(FlightPhase.Cruise, Data(lon: 17.5)), T0, 10).Tod);

        core.Reset();
        core.Evaluate(Inputs(FlightPhase.Cruise, Data(lon: 14)), T0, 10);

        Assert.NotNull(core.Evaluate(Inputs(FlightPhase.Cruise, Data(lon: 17.5)), T0, 10).Tod);
    }

    // ---- the time-based rules themselves (moved from the Web presentation) ----

    [Fact]
    public void TimeFraction_NullUntilOffBlocks_AndPinsAtOne()
    {
        var eet = TimeSpan.FromMinutes(120);

        Assert.Null(FlightProgressCore.TimeFraction(FlightTimesSnapshot.Empty, eet, T0));
        Assert.Null(FlightProgressCore.TimeFraction(Airborne, null, T0));
        Assert.Equal(0.25, FlightProgressCore.TimeFraction(Airborne, eet, T0.AddMinutes(30))!.Value, 3);
        Assert.Equal(1.0, FlightProgressCore.TimeFraction(Airborne, eet, T0.AddMinutes(300))!.Value, 3);
    }

    [Fact]
    public void Snapshot_IsStableForAParkedAircraft()
    {
        // The store notifies on inequality: a parked aircraft with sub-metre position noise
        // must not re-render every page once a second.
        var core = new FlightProgressCore();
        var times = FlightTimesSnapshot.Empty;
        var a = core.Evaluate(Inputs(FlightPhase.Preflight, Data(lon: 10.000001, groundSpeed: 0), times: times), T0, 10).Snapshot;
        var b = core.Evaluate(Inputs(FlightPhase.Preflight, Data(lon: 10.000002, groundSpeed: 0), times: times), T0.AddSeconds(1), 10).Snapshot;

        Assert.Equal(a, b);
    }
}
