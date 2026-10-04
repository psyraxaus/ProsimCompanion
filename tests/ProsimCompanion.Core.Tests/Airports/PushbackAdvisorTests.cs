using ProsimCompanion.Core.Airports;
using ProsimCompanion.Core.Airports.Parking;
using ProsimCompanion.Core.Flight;
using Xunit;

namespace ProsimCompanion.Core.Tests.Airports;

/// <summary>The pushback advisor against the real EFHK Gate W40 profile data of 2026-10-04:
/// stand heading 137°, LEFT route ends 227° (SW, Taxi AV), RIGHT route ends 47° (NE, Taxi AT).
/// Runway 22L's threshold lies north-east of the stand, so tail right faces it.</summary>
public sealed class PushbackAdvisorTests
{
    private static readonly GeoPoint W40 = new(60.3155884627228, 24.9581529816402);
    private static readonly GeoPoint Rwy22LThreshold = new(60.3335, 24.9781); // NE of the stand
    private static readonly GeoPoint Rwy04RThreshold = new(60.2990, 24.9420); // SW of the stand

    private static AirportParking W40Stand(bool withRoutes = true) => AirportParking.Bare(new ParkingIdentity(ParkingName.GateW, 40, ""), ParkingDataSources.GsxIni) with
    {
        GsxGateName = "Gate 40",
        Pose = new GeoPose(W40, 137.46),
        Pushback = new ParkingPushback(PushbackDirections.Both,
        [
            new PushbackSlot("Facing SW on Taxi AV", PushbackSlotKind.Left, withRoutes ? GeoPose.FromRaw(60.31665712, 24.95765133, -132.5354919) : null),
            new PushbackSlot("Facing NE on Taxi AT", PushbackSlotKind.Right, withRoutes ? GeoPose.FromRaw(60.31569183, 24.95612174, 47.46450806) : null),
        ]),
    };

    private static RunwayGeometry Runway(string ident, GeoPoint threshold) => new("EFHK", ident, threshold, null, null, "test");

    [Fact]
    public void Options_FromProfile_CarryFinalHeadings()
    {
        var options = PushbackAdvisor.Options(W40Stand(), menuEntries: null);

        Assert.Equal(2, options.Count);
        Assert.Equal(PushbackOptionKind.Left, options[0].Kind);
        Assert.Equal(227.46, options[0].FinalHeadingDeg!.Value, 1);
        Assert.Equal("profile", options[0].HeadingSource);
        Assert.Equal(PushbackOptionKind.Right, options[1].Kind);
        Assert.Equal(47.46, options[1].FinalHeadingDeg!.Value, 1);
    }

    [Fact]
    public void Options_WithoutRoutes_ReadTheCompassWordInTheLabel()
    {
        var options = PushbackAdvisor.Options(W40Stand(withRoutes: false), menuEntries: null);

        Assert.Equal(225, options[0].FinalHeadingDeg);
        Assert.Equal("label", options[0].HeadingSource);
        Assert.Equal(45, options[1].FinalHeadingDeg);
    }

    [Fact]
    public void Options_DefaultLabels_UseGeometryFromTheStandHeading()
    {
        var stand = W40Stand(withRoutes: false) with { Pushback = null };
        var options = PushbackAdvisor.Options(stand, [PushbackAdvisor.DefaultLeftLabel, PushbackAdvisor.DefaultRightLabel]);

        Assert.Equal(227.46, options[0].FinalHeadingDeg!.Value, 1); // nose right of 137 → 227
        Assert.Equal("geometry", options[0].HeadingSource);
        Assert.Equal(47.46, options[1].FinalHeadingDeg!.Value, 1);
    }

    [Fact]
    public void Suggest_W40_Runway22L_IsTailRight_High()
    {
        var options = PushbackAdvisor.Options(W40Stand(), null);
        var suggestion = PushbackAdvisor.Suggest(options, W40, Runway("22L", Rwy22LThreshold))!;

        Assert.Equal(PushbackOptionKind.Right, suggestion.Option.Kind);
        Assert.Equal(PushbackConfidence.High, suggestion.Confidence);
        Assert.Contains("north-east", suggestion.Reason, StringComparison.Ordinal);
        Assert.Equal(PushbackWish.TailRight, suggestion.AsChoice("suggestion").Wish);
    }

    [Fact]
    public void Suggest_W40_Runway04R_IsTailLeft()
    {
        var options = PushbackAdvisor.Options(W40Stand(), null);
        var suggestion = PushbackAdvisor.Suggest(options, W40, Runway("04R", Rwy04RThreshold))!;

        Assert.Equal(PushbackOptionKind.Left, suggestion.Option.Kind);
    }

    [Fact]
    public void Suggest_RunwayAbeam_IsLowConfidence()
    {
        // A threshold due south-east (137°) sits exactly between the two routes (227° and 47°).
        var options = PushbackAdvisor.Options(W40Stand(), null);
        var southEast = new GeoPoint(60.3005, 24.9865);
        var suggestion = PushbackAdvisor.Suggest(options, W40, Runway("15", southEast))!;

        Assert.Equal(PushbackConfidence.Low, suggestion.Confidence);
        Assert.Contains("is close", suggestion.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Suggest_SingleDirectionStand_IsHighWithoutRunway()
    {
        var stand = W40Stand() with
        {
            Pushback = new ParkingPushback(PushbackDirections.Left, [new PushbackSlot(PushbackAdvisor.DefaultLeftLabel, PushbackSlotKind.Left, null)]),
        };
        var suggestion = PushbackAdvisor.Suggest(PushbackAdvisor.Options(stand, null), null, null)!;

        Assert.Equal(PushbackOptionKind.Left, suggestion.Option.Kind);
        Assert.Equal(PushbackConfidence.High, suggestion.Confidence);
    }

    [Fact]
    public void Suggest_NoRunway_IsNull()
        => Assert.Null(PushbackAdvisor.Suggest(PushbackAdvisor.Options(W40Stand(), null), W40, null));

    [Theory]
    [InlineData(45, PushbackOptionKind.Right)]   // facing north-east
    [InlineData(0, PushbackOptionKind.Right)]    // north: 47° off, within 45? no → 47 > 45 → null handled below
    [InlineData(225, PushbackOptionKind.Left)]
    [InlineData(270, PushbackOptionKind.Left)]
    public void Match_Heading_PicksTheRouteWithin45Degrees(double asked, PushbackOptionKind expected)
    {
        var options = PushbackAdvisor.Options(W40Stand(), null);
        var match = PushbackAdvisor.Match(options, PushbackChoice.Facing(asked, "voice", "test"));
        if (asked == 0)
        {
            Assert.Null(match); // 47.5° off — too far to guess
            return;
        }

        Assert.Equal(expected, match!.Kind);
    }

    [Fact]
    public void Match_LeftRightStraightSlot()
    {
        var options = PushbackAdvisor.Options(null, ["Nose Right/Tail Left (LEFT)", "Nose Left/Tail Right (RIGHT)", "Straight pushback", "Facing South (V2)", "QuickEdit Pushback"]);

        Assert.Equal(PushbackOptionKind.Left, PushbackAdvisor.Match(options, PushbackChoice.TailLeft("web", ""))!.Kind);
        Assert.Equal(PushbackOptionKind.Right, PushbackAdvisor.Match(options, PushbackChoice.TailRight("web", ""))!.Kind);
        Assert.Equal(PushbackOptionKind.Straight, PushbackAdvisor.Match(options, PushbackChoice.Straight("web", ""))!.Kind);
        Assert.Equal("Facing South (V2)", PushbackAdvisor.Match(options, PushbackChoice.NamedSlot("facing south", "voice", ""))!.Label);
        Assert.Equal(4, options.Count); // QuickEdit is not a direction
    }

    [Theory]
    [InlineData("Facing SW on Taxi AV", 225.0)]
    [InlineData("On Taxiway A, facing S", 180.0)]
    [InlineData("push back facing north east", 45.0)]
    [InlineData("facing north-west", 315.0)]
    [InlineData("Nose Right/Tail Left (LEFT)", null)]
    [InlineData("Standard Pushback - Release Point 9", null)]
    [InlineData("Facing West (V2)", 270.0)]
    public void Compass_Parse(string text, double? expected) => Assert.Equal(expected, Compass.Parse(text));

    [Theory]
    [InlineData(0, "north")]
    [InlineData(47, "north-east")]
    [InlineData(227, "south-west")]
    [InlineData(359, "north")]
    public void Compass_Name(double deg, string expected) => Assert.Equal(expected, Compass.Name(deg));

    [Fact]
    public void RunwayLocator_NormalizesIdents()
    {
        Assert.Equal("22L", RunwayLocator.NormalizeIdent("rw22l"));
        Assert.Equal("09R", RunwayLocator.NormalizeIdent("9R"));
        Assert.Equal("04", RunwayLocator.NormalizeIdent("4"));
        Assert.Null(RunwayLocator.NormalizeIdent(" "));
    }

    [Fact]
    public void FindStand_ByGsxName_ThenByPosition()
    {
        var catalogue = new AirportParkings("EFHK", [W40Stand() with { GsxUiName = "Apron 1W (Gates W34-W48) | Gate 40" }],
            ParkingDataSources.GsxIni | ParkingDataSources.GsxPy, [], DateTimeOffset.UnixEpoch);

        Assert.NotNull(PushbackSuggestionService.FindStand(catalogue, "Apron 1W (Gates W34-W48) | Gate 40", null));
        Assert.NotNull(PushbackSuggestionService.FindStand(catalogue, null, new GeoPoint(60.3156, 24.9582)));
        Assert.Null(PushbackSuggestionService.FindStand(catalogue, null, new GeoPoint(60.3200, 24.9582))); // ~490 m away
    }
}
