using ProsimCompanion.Core.Aircraft.WeightAndBalance;
using Xunit;

namespace ProsimCompanion.Core.Tests.Aircraft.WeightAndBalance;

public sealed class A320TrimEnvelopeTests
{
    // ---- table corner points reproduce exactly ----

    [Theory]
    [InlineData(53_400, 21.44, 34.62)] // fwd kink (narrowest forward point of the T/O envelope)
    [InlineData(37_400, 23.78, 30.96)] // chart floor
    [InlineData(71_800, 20.83, 37.18)] // operational fwd kink
    public void GrossLimits_HitTableCorners(double weightKg, double expectedMin, double expectedMax)
    {
        var (min, max) = A320TrimEnvelope.GrossWeightLimits(weightKg);
        Assert.Equal(expectedMin, min, 2);
        Assert.Equal(expectedMax, max, 1);
    }

    [Fact]
    public void GrossLimits_InterpolateLinearlyBetweenCorners()
    {
        // Midpoint of the (37.4, 23.78) → (40.0, 23.40) forward segment.
        var (min, _) = A320TrimEnvelope.GrossWeightLimits(38_700);
        Assert.Equal((23.78 + 23.40) / 2.0, min, 2);
    }

    // ---- clamping outside the chart ----

    [Fact]
    public void GrossLimits_BelowChartFloor_ClampToFloorValues()
    {
        var atFloor = A320TrimEnvelope.GrossWeightLimits(37_400);
        Assert.Equal(atFloor, A320TrimEnvelope.GrossWeightLimits(20_000));
        Assert.Equal(atFloor, A320TrimEnvelope.GrossWeightLimits(0));
        Assert.Equal(atFloor, A320TrimEnvelope.GrossWeightLimits(double.NaN));
    }

    [Fact]
    public void GrossLimits_AboveChartCeiling_ClampToCeilingValues()
    {
        var atCeiling = A320TrimEnvelope.GrossWeightLimits(77_000);
        Assert.Equal(atCeiling, A320TrimEnvelope.GrossWeightLimits(90_000));
    }

    // ---- envelope sanity across the whole weight range ----

    [Fact]
    public void GrossLimits_MinIsAlwaysBelowMax_AcrossTheChart()
    {
        for (var kg = 35_000.0; kg <= 78_000.0; kg += 100)
        {
            var (min, max) = A320TrimEnvelope.GrossWeightLimits(kg);
            Assert.True(max - min > 1.0, $"degenerate envelope at {kg} kg: {min}–{max}");
        }
    }

    [Fact]
    public void ZfwLimits_MinIsAlwaysBelowMax_AndInsideGrossEnvelopeFloor()
    {
        for (var kg = 37_400.0; kg <= 61_000.0; kg += 100)
        {
            var (zfwMin, zfwMax) = A320TrimEnvelope.ZeroFuelLimits(kg);
            var (gwMin, gwMax) = A320TrimEnvelope.GrossWeightLimits(kg);
            Assert.True(zfwMax > zfwMin, $"degenerate ZFW envelope at {kg} kg");
            // The ZFW pair is the INNER pair below MZFW — always inside the take-off pair.
            Assert.True(zfwMin >= gwMin, $"ZFW fwd limit outside T/O envelope at {kg} kg");
            Assert.True(zfwMax <= gwMax, $"ZFW aft limit outside T/O envelope at {kg} kg");
        }
    }

    // ---- the MZFW aft-side step (chart artifact, deliberately encoded) ----

    [Fact]
    public void GrossAftLimit_StepsDownCrossingMzfwUpward()
    {
        var (_, below) = A320TrimEnvelope.GrossWeightLimits(60_900);
        var (_, above) = A320TrimEnvelope.GrossWeightLimits(61_100);
        Assert.True(below > 36.0, $"take-off aft limit just below MZFW should exceed 36 (was {below:F2})");
        Assert.True(above < 36.0, $"operational aft limit just above MZFW should be under 36 (was {above:F2})");
    }

    // ---- membership helpers ----

    [Theory]
    [InlineData(65_000, 25.0, true)] // mid-envelope
    [InlineData(65_000, 20.0, false)] // forward of the fwd limit
    [InlineData(65_000, 38.0, false)] // aft of the aft limit
    [InlineData(76_500, 25.0, false)] // fine at 65 t, outside the narrowed top of the chart
    public void IsGrossCgWithinLimits_Checks(double weightKg, double mac, bool expected)
        => Assert.Equal(expected, A320TrimEnvelope.IsGrossCgWithinLimits(weightKg, mac));

    [Theory]
    [InlineData(55_000, 28.0, true)]
    [InlineData(55_000, 22.0, false)]
    [InlineData(55_000, 34.0, false)] // valid MACTOW there, but outside the ZFW envelope
    public void IsZfwCgWithinLimits_Checks(double weightKg, double mac, bool expected)
        => Assert.Equal(expected, A320TrimEnvelope.IsZfwCgWithinLimits(weightKg, mac));

    // ---- the fixed 21–38 window is genuinely replaced ----

    [Fact]
    public void Limits_AreTighterThanTheOldFixedWindow_AtTypicalWeights()
    {
        // At a typical 65 t TOW the old window would accept 21.5 and 37.9; the envelope must not.
        Assert.False(A320TrimEnvelope.IsGrossCgWithinLimits(65_000, 21.3));
        Assert.False(A320TrimEnvelope.IsGrossCgWithinLimits(65_000, 37.5));
    }
}
