using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Web.Components;
using Xunit;

namespace ProsimCompanion.Core.Tests.Web;

/// <summary>Issue #154: the Fuel Log page's header figures and chart geometry.</summary>
public sealed class FuelLogPresentationTests
{
    private static readonly DateTimeOffset Takeoff = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private static OfpData Ofp() => new()
    {
        RequestId = "r1",
        OriginIcao = "AAAA",
        DestinationIcao = "DDDD",
        FuelPlanLandingKg = 5000,
        Navlog =
        [
            new OfpFix("AAAA", new GeoPoint(50, 0), 9000, TimeSpan.Zero, 0, false),
            new OfpFix("BBB", new GeoPoint(50, 1), 8000, TimeSpan.FromMinutes(10), 30000, false),
            new OfpFix("CCC", new GeoPoint(50, 2), 7000, TimeSpan.FromMinutes(20), 30000, false),
            new OfpFix("DDDD", new GeoPoint(50, 3), 6000, TimeSpan.FromMinutes(30), 0, false),
        ],
    };

    [Fact]
    public void Header_PlanAtThisPoint_IsInterpolated_AndTheLandingEstimateFollowsTheDelta()
    {
        var ofp = Ofp();
        var (log, _) = FuelLogCore.Advance(FuelLogSnapshot.Empty, ofp, new GeoPoint(50, 1.5), 7400, Takeoff, Takeoff.AddMinutes(15));

        var header = FuelLogPresentation.Header(log, ofp.Navlog, new GeoPoint(50, 1.5), 7400);

        Assert.Equal(7400, header.FuelOnBoardKg);
        Assert.Equal(7500, header.PlanHereKg);            // halfway BBB (8000) → CCC (7000)
        Assert.Equal(-100, header.DeltaKg);
        Assert.Equal(4900, header.EstimatedLandingKg);     // 5000 − 100
        Assert.Equal("BBB", header.BetweenFrom);
        Assert.Equal("CCC", header.BetweenTo);
    }

    [Fact]
    public void Header_WithoutAPosition_HasNoPlanFigure()
    {
        var header = FuelLogPresentation.Header(FuelLogCore.FromOfp(Ofp()), Ofp().Navlog, null, 9000);

        Assert.Null(header.PlanHereKg);
        Assert.Null(header.DeltaKg);
        Assert.Null(header.EstimatedLandingKg);
    }

    [Fact]
    public void Chart_LaysOutPlanAndActual_InAFixedViewBox()
    {
        var ofp = Ofp();
        var (log, _) = FuelLogCore.Advance(FuelLogSnapshot.Empty, ofp, new GeoPoint(50, 1.5), 7400, Takeoff, Takeoff.AddMinutes(15));

        var chart = FuelLogPresentation.Chart(log, 7400, 4900)!;

        Assert.Equal(660, chart.Width);
        Assert.Equal(4, chart.PlannedPoints.Split(' ').Length);
        Assert.Equal(2, chart.ActualDots.Count);                       // AAAA and BBB stamped
        Assert.Equal("AAAA", chart.ActualDots[0].Ident);
        Assert.NotNull(chart.PlannedLandingY);
        Assert.Equal("EST 4,900", chart.Estimate!.Value.Label);
        Assert.Equal(2, chart.ProjectionPoints.Split(' ').Length);       // from the last stamp to the end
        Assert.Contains(chart.FixLabels, l => l.Label == "DDDD");
        Assert.True(chart.GridLines.Count >= 3);
        // The highest plan figure sits above the lowest on screen (smaller y).
        var ys = chart.PlannedPoints.Split(' ').Select(p => double.Parse(p.Split(',')[1], System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.True(ys[0] < ys[^1]);
    }

    [Fact]
    public void Chart_NeedsTwoPlannedFigures()
        => Assert.Null(FuelLogPresentation.Chart(FuelLogSnapshot.Empty, null, null));

    [Theory]
    [InlineData(new double[] { }, "no fix passed yet")]
    [InlineData(new double[] { -90 }, "−90 at the last fix")]
    [InlineData(new double[] { -90, -160, -130 }, "−90 · −160 · −130 at the last three fixes")]
    [InlineData(new double[] { 40, 0 }, "+40 · 0 at the last two fixes")]
    public void RecentDeltasLine_ReadsLikeTheDraft(double[] deltas, string expected)
        => Assert.Equal(expected, FuelLogPresentation.RecentDeltasLine(deltas));
}
