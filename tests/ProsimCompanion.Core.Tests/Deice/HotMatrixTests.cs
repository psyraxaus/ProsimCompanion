using ProsimCompanion.Core.Deice;
using Xunit;

namespace ProsimCompanion.Core.Tests.Deice;

public sealed class HotMatrixTests
{
    [Fact]
    public void NoPrecipitation_NoHoldover()
        => Assert.Null(HotMatrix.Lookup(4, 100, -5, HotPrecip.None));

    [Fact]
    public void ActiveFrost_IsBandIndependent()
    {
        Assert.Equal(new HotMatrix.HotWindow(45, 45), HotMatrix.Lookup(1, 100, -30, HotPrecip.ActiveFrost));
        Assert.Equal(new HotMatrix.HotWindow(480, 480), HotMatrix.Lookup(4, 100, -30, HotPrecip.ActiveFrost));
    }

    [Fact]
    public void TypeI_Snow_WarmBand_MatchesTable()
        => Assert.Equal(new HotMatrix.HotWindow(6, 11), HotMatrix.Lookup(1, 100, 0, HotPrecip.Snow));

    [Fact]
    public void TypeIV_Full_Snow_WarmBand_IsTheReferenceWindow()
        => Assert.Equal(new HotMatrix.HotWindow(35, 75), HotMatrix.Lookup(4, 100, -2, HotPrecip.Snow));

    [Fact]
    public void TypeII_At75Percent_ScalesTheReference()
    {
        // family 0.7 × concentration 0.6 = 0.42 → 35*0.42 = 14.7 → 15; 75*0.42 = 31.5 → 32.
        Assert.Equal(new HotMatrix.HotWindow(15, 32), HotMatrix.Lookup(2, 75, -2, HotPrecip.Snow));
    }

    [Fact]
    public void BelowLout_NoHoldover()
    {
        Assert.Null(HotMatrix.Lookup(1, 100, -26, HotPrecip.Snow));
        Assert.Null(HotMatrix.Lookup(4, 100, -26, HotPrecip.Snow));
    }

    [Fact]
    public void ConditionsNotProtectedInColdBands_ReturnNull()
    {
        // Rain on a cold-soaked wing only carries a window in the warmest band.
        Assert.NotNull(HotMatrix.Lookup(4, 100, 0, HotPrecip.RainOnColdSoakedWing));
        Assert.Null(HotMatrix.Lookup(4, 100, -5, HotPrecip.RainOnColdSoakedWing));
    }

    [Fact]
    public void OatBandEdges_AreInclusiveOnTheWarmSide()
    {
        // −3 is still band 0; just below drops to band 1.
        Assert.Equal(new HotMatrix.HotWindow(35, 75), HotMatrix.Lookup(4, 100, -3, HotPrecip.Snow));
        Assert.Equal(new HotMatrix.HotWindow(20, 45), HotMatrix.Lookup(4, 100, -3.1, HotPrecip.Snow));
    }
}
