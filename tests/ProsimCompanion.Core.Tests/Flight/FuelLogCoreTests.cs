using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Flight;
using Xunit;

namespace ProsimCompanion.Core.Tests.Flight;

/// <summary>Issue #154: the fuel log stamps each navlog fix as it is passed — never before
/// takeoff, the origin row at the takeoff time — restarts on a new OFP, and reads a trend.
/// Issue #167: a fix the aircraft is no longer at (passed during a restart, a gap, or a
/// several-fix jump) is marked not recorded instead of being stamped with the wrong moment.</summary>
public sealed class FuelLogCoreTests
{
    // A straight east-west route at 50 N: 0 E → 1 E → 2 E → 3 E, planned fuel 9000 → 6000,
    // ten planned minutes per leg.
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
        var (log, stamped, skipped) = FuelLogCore.Advance(FuelLogSnapshot.Empty, Ofp(), new GeoPoint(50, 0), 9600, takeoffUtc: null, Takeoff);

        Assert.True(log.HasPlan);
        Assert.Empty(stamped);
        Assert.Empty(skipped);
        Assert.Equal(0, log.PassedCount);
    }

    [Fact]
    public void AfterTakeoff_TheOriginRowCarriesTheTakeoffTime_AndFixesStampAsPassed()
    {
        var ofp = Ofp();
        var (log, first, _) = FuelLogCore.Advance(FuelLogSnapshot.Empty, ofp, new GeoPoint(50, 0.1), 9400, Takeoff, Takeoff.AddMinutes(1));

        var origin = Assert.Single(first);
        Assert.Equal("AAAA", origin.Ident);
        Assert.Equal(Takeoff, origin.ActualTimeUtc);          // the takeoff time, not "now"
        Assert.Equal(9400, origin.ActualFobKg);
        Assert.Equal(400, origin.DeltaKg);

        // Just past BBB (20% of the next leg = 2 planned minutes: still "at" the fix).
        var (later, second, _) = FuelLogCore.Advance(log, ofp, new GeoPoint(50, 1.2), 7700, Takeoff, Takeoff.AddMinutes(14));
        var bbb = Assert.Single(second);
        Assert.Equal("BBB", bbb.Ident);
        Assert.Equal(Takeoff.AddMinutes(14), bbb.ActualTimeUtc);
        Assert.Equal(-300, bbb.DeltaKg);
        Assert.Equal(4, bbb.DeltaMinutes(Takeoff));
        Assert.Equal(2, later.PassedCount);

        // Same leg again: nothing new.
        var (same, none, _) = FuelLogCore.Advance(later, ofp, new GeoPoint(50, 1.5), 7650, Takeoff, Takeoff.AddMinutes(15));
        Assert.Empty(none);
        Assert.Equal(2, same.PassedCount);
    }

    /// <summary>Issue #167 (2026-10-10 restart in the climb): five fixes passed during the
    /// outage were all stamped 20:58:10Z with the restart's fuel. Only the fix the aircraft
    /// is still at may take "now"; the ones behind are not recorded.</summary>
    [Fact]
    public void SeveralFixesPassedInOneTick_OnlyTheOneStillAtHandStamps_TheRestAreNotRecorded()
    {
        var ofp = Ofp();
        var (log, _, _) = FuelLogCore.Advance(FuelLogSnapshot.Empty, ofp, new GeoPoint(50, 0.1), 9400, Takeoff, Takeoff.AddMinutes(1));
        var (next, stamped, skipped) = FuelLogCore.Advance(log, ofp, new GeoPoint(50, 2.1), 6600, Takeoff, Takeoff.AddMinutes(26));

        Assert.Equal("CCC", Assert.Single(stamped).Ident);
        Assert.Equal("BBB", Assert.Single(skipped).Ident);
        Assert.True(next.Rows[1].NotRecorded);
        Assert.Null(next.Rows[1].ActualFobKg);
        Assert.Equal(6600, next.Rows[2].ActualFobKg);
        Assert.Equal(2, next.PassedCount);
        Assert.Equal(2, next.LastHandled!.Index);
    }

    [Fact]
    public void AFixFarBehind_IsNotStampedWithNow()
    {
        var ofp = Ofp();
        // Halfway along the BBB → CCC leg: five planned minutes past BBB, beyond the 3-minute lag.
        var (log, stamped, skipped) = FuelLogCore.Advance(FuelLogSnapshot.Empty, ofp, new GeoPoint(50, 1.5), 7400, Takeoff, Takeoff.AddMinutes(15));

        Assert.Equal("AAAA", Assert.Single(stamped).Ident);
        Assert.Equal("BBB", Assert.Single(skipped).Ident);
        Assert.Equal(1, log.PassedCount);
        // The next fix still stamps normally.
        var (next, later, _) = FuelLogCore.Advance(log, ofp, new GeoPoint(50, 2.05), 6700, Takeoff, Takeoff.AddMinutes(25));
        Assert.Equal("CCC", Assert.Single(later).Ident);
        Assert.Equal(2, next.PassedCount);
    }

    [Fact]
    public void ReachingTheDestination_StampsTheLastRow()
    {
        var ofp = Ofp();
        var (log, _, _) = FuelLogCore.Advance(FuelLogSnapshot.Empty, ofp, new GeoPoint(50, 2.1), 6600, Takeoff, Takeoff.AddMinutes(26));
        var (next, stamped, _) = FuelLogCore.Advance(log, ofp, new GeoPoint(50, 3.0), 6100, Takeoff, Takeoff.AddMinutes(31));

        Assert.Equal("DDDD", Assert.Single(stamped).Ident);
        Assert.Equal(6100, next.Rows[3].ActualFobKg);
        Assert.Equal(3, next.PassedCount); // AAAA, CCC, DDDD; BBB not recorded
    }

    /// <summary>Issue #167: started in flight with nothing restored — the leg runs without a
    /// takeoff time, the origin and the fixes already behind are not recorded, and the
    /// planned clock times stay empty rather than being re-based on the restart.</summary>
    [Fact]
    public void TakeoffUnknown_RunsTheLog_WithoutAnyPlannedTimes()
    {
        var ofp = Ofp();
        var (log, stamped, skipped) = FuelLogCore.Advance(FuelLogSnapshot.Empty, ofp, new GeoPoint(50, 1.5), 7400, takeoffUtc: null, Takeoff.AddMinutes(15), takeoffUnknown: true);

        Assert.True(log.TakeoffUnknown);
        Assert.Null(log.TakeoffUtc);
        Assert.Empty(stamped);
        Assert.Equal(["AAAA", "BBB"], skipped.Select(r => r.Ident));
        Assert.All(log.Rows.Take(2), r => Assert.True(r.NotRecorded));

        var (next, later, _) = FuelLogCore.Advance(log, ofp, new GeoPoint(50, 2.05), 6700, takeoffUtc: null, Takeoff.AddMinutes(25), takeoffUnknown: true);
        var ccc = Assert.Single(later);
        Assert.Equal("CCC", ccc.Ident);
        Assert.Equal(Takeoff.AddMinutes(25), ccc.ActualTimeUtc);
        Assert.Equal(-300, ccc.DeltaKg);
        Assert.Null(ccc.DeltaMinutes(next.TakeoffUtc));
        Assert.Null(ccc.PlannedTimeUtc(next.TakeoffUtc));
    }

    /// <summary>A log restored from the snapshot keeps its stamps; only what was passed in
    /// the meantime is handled, and never re-stamped.</summary>
    [Fact]
    public void RestoredLog_KeepsItsStamps_AndNeverRestampsThem()
    {
        var ofp = Ofp();
        var (log, _, _) = FuelLogCore.Advance(FuelLogSnapshot.Empty, ofp, new GeoPoint(50, 0.1), 9400, Takeoff, Takeoff.AddMinutes(1));
        (log, _, _) = FuelLogCore.Advance(log, ofp, new GeoPoint(50, 1.1), 7900, Takeoff, Takeoff.AddMinutes(11));

        // "Restart" 15 minutes later, halfway to DDDD: BBB's stamp is untouched, CCC was passed in the outage.
        var (after, stamped, skipped) = FuelLogCore.Advance(log, ofp, new GeoPoint(50, 2.5), 6500, Takeoff, Takeoff.AddMinutes(26));

        Assert.Empty(stamped);
        Assert.Equal("CCC", Assert.Single(skipped).Ident);
        Assert.Equal(Takeoff.AddMinutes(11), after.Rows[1].ActualTimeUtc);
        Assert.Equal(7900, after.Rows[1].ActualFobKg);
        Assert.Equal(2, after.PassedCount);
    }

    [Fact]
    public void ANewOfp_StartsAFreshLog()
    {
        var (log, _, _) = FuelLogCore.Advance(FuelLogSnapshot.Empty, Ofp("r1"), new GeoPoint(50, 1.2), 7700, Takeoff, Takeoff.AddMinutes(14));
        Assert.Equal(2, log.PassedCount);

        var (fresh, stamped, _) = FuelLogCore.Advance(log, Ofp("r2"), null, null, Takeoff, Takeoff.AddMinutes(15));

        Assert.Empty(stamped);
        Assert.Equal(0, fresh.PassedCount);
        Assert.NotEqual(log.PlanKey, fresh.PlanKey);
    }

    [Fact]
    public void TakeoffTimeCleared_ResetsTheStamps()
    {
        var ofp = Ofp();
        var (log, _, _) = FuelLogCore.Advance(FuelLogSnapshot.Empty, ofp, new GeoPoint(50, 1.2), 7700, Takeoff, Takeoff.AddMinutes(14));
        var (reset, _, _) = FuelLogCore.Advance(log, ofp, new GeoPoint(50, 0), 9600, takeoffUtc: null, Takeoff.AddHours(3));

        Assert.Equal(0, reset.PassedCount);
        Assert.Null(reset.LastHandled);
        Assert.Null(reset.TakeoffUtc);
    }

    [Fact]
    public void NoOfp_IsEmpty()
    {
        var (log, stamped, _) = FuelLogCore.Advance(FuelLogSnapshot.Empty, null, new GeoPoint(50, 1), 8000, Takeoff, Takeoff);

        Assert.False(log.HasPlan);
        Assert.Empty(stamped);
    }

    [Theory]
    [InlineData(new double[] { -100, -150, -200 }, "losing")]
    [InlineData(new double[] { -200, -150, -100 }, "gaining")]
    [InlineData(new double[] { -100, -110, -105 }, "holding")]
    [InlineData(new double[] { -100 }, null)]
    public void Trend_ReadsTheLastThreeDeltas(double[] deltas, string? expected)
    {
        var rows = deltas.Select((d, i) => new FuelLogRow(i, $"F{i}", null, 8000, Takeoff, 8000 + d)).ToList();
        var log = new FuelLogSnapshot("k", "A", "B", rows, 5000, Takeoff);

        Assert.Equal(expected, FuelLogCore.Trend(log));
    }
}
