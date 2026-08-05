using ProsimCompanion.Core.Aircraft.WeightAndBalance;
using Xunit;

namespace ProsimCompanion.Core.Tests.Aircraft.WeightAndBalance;

public sealed class A320WeightAndBalanceTests
{
    // ── Loaded index (bit-exact anchors from ProSim-emitted FINAL JSON) ──────────────────

    [Theory]
    [InlineData(26.9, 706.35)]
    [InlineData(25.4, 704.10)]
    [InlineData(29.6, 710.40)] // the reference MAC maps to the base index exactly
    public void LoadedIndex_MatchesProsimAnchors_AfterTwoDecimalRounding(double mac, double expected)
        => Assert.Equal(expected, Math.Round(A320WeightAndBalance.CalculateLoadedIndex(mac), 2));

    // ── Fuel-CG adjustment table ─────────────────────────────────────────────────────────

    [Fact]
    public void CgAdjustment_ZeroFuel_IsZero()
        => Assert.Equal(0.0, A320WeightAndBalance.GetCgAdjustmentForFuel(0));

    [Fact]
    public void CgAdjustment_FirstNonZeroBucket_MatchesTable()
        => Assert.Equal(-0.0611692667008015, A320WeightAndBalance.GetCgAdjustmentForFuel(2));

    [Fact]
    public void CgAdjustment_ClampsBothEnds()
    {
        Assert.Equal(A320WeightAndBalance.GetCgAdjustmentForFuel(0), A320WeightAndBalance.GetCgAdjustmentForFuel(-5));
        // 194 entries → index 193 is the last real bucket; anything beyond holds it.
        Assert.Equal(-6.636081635952, A320WeightAndBalance.GetCgAdjustmentForFuel(193));
        Assert.Equal(-6.636081635952, A320WeightAndBalance.GetCgAdjustmentForFuel(10_000));
    }

    [Fact]
    public void ZfwCg_IsGrossCgMinusAdjustment_SoItLandsAftOfGross()
    {
        // Entries are negative → subtracting them moves the ZFW CG aft (numerically larger).
        var zfwCg = A320WeightAndBalance.CalculateZfwCg(50, 27.0);
        Assert.True(zfwCg > 27.0);
        Assert.Equal(27.0 - A320WeightAndBalance.GetCgAdjustmentForFuel(50), zfwCg);
    }

    // ── CG plausibility gate ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0.0)]      // unpopulated dataref sentinel
    [InlineData(4.9)]
    [InlineData(60.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void EnsurePlausibleCg_RejectsImplausibleReads(double cg)
        => Assert.Throws<InvalidOperationException>(() => A320WeightAndBalance.EnsurePlausibleCg(cg, "test"));

    [Theory]
    [InlineData(5.0)]
    [InlineData(28.5)]
    [InlineData(60.0)]
    public void EnsurePlausibleCg_PassesPlausibleReads(double cg)
        => Assert.Equal(cg, A320WeightAndBalance.EnsurePlausibleCg(cg, "test"));

    // ── Preliminary calculation ──────────────────────────────────────────────────────────

    private static WeightAndBalanceLiveState LiveState() => new()
    {
        ZoneCapacities = [24, 30, 36, 42],
        ZoneAmounts = [18, 22, 27, 31],
        CargoForwardCapacityKg = 3000,
        CargoAftCapacityKg = 4000,
        CargoBulkCapacityKg = 1000,
        CargoForwardKg = 1200,
        CargoAftKg = 1800,
        FuelCenterKg = 2000,
        FuelLeftKg = 2500,
        FuelRightKg = 2500,
        ZfwKg = 60000,
        GrossWeightKg = 67000,
        GrossCgMac = 27.0,
        ZfwCgMac = 26.5,
    };

    [Fact]
    public void Preliminary_ZoneSplit_TruncatesAndAbsorbsRemainderInZone4()
    {
        var plan = new FlightPlanInputs { PassengerCount = 99, PlannedFuelKg = 7000 };
        var data = A320WeightAndBalance.CalculatePreliminary(plan, LiveState());

        // loadFactor 99/132 = 0.75; truncating casts: 18/22/27, zone4 = 99−67 = 32.
        Assert.Equal([18, 22, 27, 32], data.PassengersByZone);
        Assert.Equal(99, data.TotalPassengers);
    }

    [Fact]
    public void Preliminary_PrefersOfpEstimates_AndDerivesLawFromPlannedTrip()
    {
        var plan = new FlightPlanInputs
        {
            PassengerCount = 100,
            PlannedFuelKg = 7000,
            PlannedLandingFuelKg = 2600,
            EstimatedZeroFuelWeightKg = 59500,
            EstimatedTakeoffWeightKg = 66500,
        };
        var data = A320WeightAndBalance.CalculatePreliminary(plan, LiveState());

        Assert.Equal(59500, data.ZeroFuelWeight);
        Assert.Equal(66500, data.TakeoffWeight);
        Assert.Equal(66500 - (7000 - 2600), data.LandingWeight); // TOW − planned trip
        Assert.Equal(27.0, data.TakeoffWeightMac);               // live aircraft.cg
        Assert.Equal(26.5, data.ZeroFuelWeightMac);              // live aircraft.zfwcg
    }

    [Fact]
    public void Preliminary_FallsBackToLiveState_WhenEstimatesMissing()
    {
        var plan = new FlightPlanInputs { PassengerCount = 100, PlannedFuelKg = 7000 };
        var data = A320WeightAndBalance.CalculatePreliminary(plan, LiveState());

        Assert.Equal(60000, data.ZeroFuelWeight);          // live ZFW
        Assert.Equal(60000 + 7000, data.TakeoffWeight);    // ZFW + planned fuel
    }

    [Fact]
    public void Preliminary_ClampsCargoToCombinedCapacity_BulkFoldsIntoAft()
    {
        var plan = new FlightPlanInputs { PassengerCount = 50, PlannedFuelKg = 5000, CargoTotalKg = 99999 };
        var data = A320WeightAndBalance.CalculatePreliminary(plan, LiveState());

        Assert.Equal(8000, data.ForwardCargoWeight + data.AftCargoWeight, 3); // fwd+aft+bulk capacity
        Assert.Equal(3000, data.ForwardCargoWeight, 3);                        // capacity-proportional
    }

    [Fact]
    public void Preliminary_ImplausibleCg_Throws()
    {
        var live = LiveState();
        live.ZfwCgMac = 0.0; // unpopulated
        Assert.Throws<InvalidOperationException>(
            () => A320WeightAndBalance.CalculatePreliminary(new FlightPlanInputs { PassengerCount = 1 }, live));
    }

    // ── Final calculation ────────────────────────────────────────────────────────────────

    [Fact]
    public void Final_DerivesMacZfwFromFuelTable_AndDefaultsLawToTow()
    {
        var live = LiveState();
        var data = A320WeightAndBalance.CalculateFinal(live);

        var totalFuel = 7000;
        var expectedMacZfw = 27.0 - A320WeightAndBalance.GetCgAdjustmentForFuel(totalFuel / 100);
        Assert.Equal(expectedMacZfw, data.ZeroFuelWeightMac);
        Assert.Equal(27.0, data.TakeoffWeightMac);
        Assert.Equal(live.GrossWeightKg, data.TakeoffWeight);
        Assert.Equal(live.GrossWeightKg, data.LandingWeight); // orchestrator overrides with trip fuel
        Assert.Equal(98, data.TotalPassengers);
        Assert.Equal(totalFuel, data.FuelWeight);
    }
}
