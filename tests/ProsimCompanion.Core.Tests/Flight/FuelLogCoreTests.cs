using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Flight;
using Xunit;

namespace ProsimCompanion.Core.Tests.Flight;

/// <summary>Issue #154: the fuel log stamps each navlog fix as it is passed — never before
/// takeoff, the origin row at the takeoff time — restarts on a new OFP, and reads a trend.</summary>
public sealed class FuelLogCoreTests
{
    // A straight east-west route at 50 N: 0 E → 1 E → 2 E → 3 E, planned fuel 9000 → 6000.
    private static OfpData Ofp(string requestId = "r1") => new()
    {
        RequestId = requestId,
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

    private static readonly DateTimeOffset Takeoff = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FromOfp_OneRowPerFix_NothingPassed()
    {
        var log = FuelLogCore.FromOfp(Ofp());

        Assert.True(log.HasPlan);
        Assert.Equal(4, log.Rows.Count);
        Assert.Equal(0, log.PassedCount);
        Assert.Equal(5000, log.PlannedLandingKg);
        Assert.Equal("BBB", log.Rows[1].Ident);
        Assert.Equal(8000, log.Rows[1].PlannedFobKg);
    }

    [Fact]
    public void BeforeTakeoff_NothingIsStamped_EvenAtTheFirstFix()
    {
        var (log, stamped) = FuelLogCore.Advance(FuelLogSnapshot.Empty, Ofp(), new GeoPoint(50, 0), 9600, takeoffUtc: null, Takeoff);

        Assert.True(log.HasPlan);
        Assert.Empty(stamped);
        Assert.Equal(0, log.PassedCount);
    }

    [Fact]
    public void AfterTakeoff_TheOriginRowCarriesTheTakeoffTime_AndFixesStampAsPassed()
    {
        var ofp = Ofp();
        var (log, first) = FuelLogCore.Advance(FuelLogSnapshot.Empty, ofp, new GeoPoint(50, 0.1), 9400, Takeoff, Takeoff.AddMinutes(1));

        var origin = Assert.Single(first);
        Assert.Equal("AAAA", origin.Ident);
        Assert.Equal(Takeoff, origin.ActualTimeUtc);          // the takeoff time, not "now"
        Assert.Equal(9400, origin.ActualFobKg);
        Assert.Equal(400, origin.DeltaKg);

        // Past BBB, 40% of the way to CCC.
        var (later, second) = FuelLogCore.Advance(log, ofp, new GeoPoint(50, 1.4), 7700, Takeoff, Takeoff.AddMinutes(14));
        var bbb = Assert.Single(second);
        Assert.Equal("BBB", bbb.Ident);
        Assert.Equal(Takeoff.AddMinutes(14), bbb.ActualTimeUtc);
        Assert.Equal(-300, bbb.DeltaKg);
        Assert.Equal(4, bbb.DeltaMinutes(Takeoff));
        Assert.Equal(2, later.PassedCount);

        // Same position again: nothing new.
        var (same, none) = FuelLogCore.Advance(later, ofp, new GeoPoint(50, 1.5), 7650, Takeoff, Takeoff.AddMinutes(15));
        Assert.Empty(none);
        Assert.Equal(2, same.PassedCount);
    }

    [Fact]
    public void SeveralFixesPassedInOneTick_AllStampAlike()
    {
        var ofp = Ofp();
        var (log, _) = FuelLogCore.Advance(FuelLogSnapshot.Empty, ofp, new GeoPoint(50, 0.1), 9400, Takeoff, Takeoff.AddMinutes(1));
        var (next, stamped) = FuelLogCore.Advance(log, ofp, new GeoPoint(50, 2.5), 6600, Takeoff, Takeoff.AddMinutes(26));

        Assert.Equal(["BBB", "CCC"], stamped.Select(r => r.Ident));
        Assert.All(stamped, r => Assert.Equal(6600, r.ActualFobKg));
        Assert.Equal(3, next.PassedCount);
    }

    [Fact]
    public void ReachingTheDestination_StampsTheLastRow()
    {
        var ofp = Ofp();
        var (log, _) = FuelLogCore.Advance(FuelLogSnapshot.Empty, ofp, new GeoPoint(50, 2.5), 6600, Takeoff, Takeoff.AddMinutes(26));
        var (next, stamped) = FuelLogCore.Advance(log, ofp, new GeoPoint(50, 3.0), 6100, Takeoff, Takeoff.AddMinutes(31));

        Assert.Equal("DDDD", Assert.Single(stamped).Ident);
        Assert.Equal(4, next.PassedCount);
    }

    [Fact]
    public void ANewOfp_StartsAFreshLog()
    {
        var (log, _) = FuelLogCore.Advance(FuelLogSnapshot.Empty, Ofp("r1"), new GeoPoint(50, 1.4), 7700, Takeoff, Takeoff.AddMinutes(14));
        Assert.Equal(2, log.PassedCount);

        var (fresh, stamped) = FuelLogCore.Advance(log, Ofp("r2"), null, null, Takeoff, Takeoff.AddMinutes(15));

        Assert.Empty(stamped);
        Assert.Equal(0, fresh.PassedCount);
        Assert.NotEqual(log.PlanKey, fresh.PlanKey);
    }

    [Fact]
    public void TakeoffTimeCleared_ResetsTheStamps()
    {
        var ofp = Ofp();
        var (log, _) = FuelLogCore.Advance(FuelLogSnapshot.Empty, ofp, new GeoPoint(50, 1.4), 7700, Takeoff, Takeoff.AddMinutes(14));
        var (reset, _) = FuelLogCore.Advance(log, ofp, new GeoPoint(50, 0), 9600, takeoffUtc: null, Takeoff.AddHours(3));

        Assert.Equal(0, reset.PassedCount);
        Assert.Null(reset.TakeoffUtc);
    }

    [Fact]
    public void NoOfp_IsEmpty()
    {
        var (log, stamped) = FuelLogCore.Advance(FuelLogSnapshot.Empty, null, new GeoPoint(50, 1), 8000, Takeoff, Takeoff);

        Assert.False(log.HasPlan);
        Assert.Empty(stamped);
    }

    [Theory]
    [InlineData(new double[] { -90, -160, -230 }, "losing")]
    [InlineData(new double[] { -230, -160, -90 }, "gaining")]
    [InlineData(new double[] { -90, -100, -95 }, "holding")]
    [InlineData(new double[] { -90 }, null)]
    public void Trend_ReadsTheLastThreeDeltas(double[] deltas, string? expected)
    {
        var rows = deltas.Select((d, i) => new FuelLogRow(i, $"F{i}", null, 8000, Takeoff, 8000 + d)).ToList();
        var log = new FuelLogSnapshot("k", "A", "B", rows, 5000, Takeoff);

        Assert.Equal(expected, FuelLogCore.Trend(log));
    }
}
