using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Speech.Callouts;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Owner's A322 limits (2026-09-20): VLO extension 250 kt, VLO retraction 220 kt,
/// VLE 280 kt; flap placards 230 / 215 / 200 / 185 / 177 per handle step.</summary>
public sealed class GearCallDeciderTests
{
    private static readonly SopOptions Defaults = new();

    [Fact]
    public void DefaultLimits_AreTheA322Figures()
    {
        Assert.Equal(280, Defaults.GearMaxKt);
        Assert.Equal(250, Defaults.GearExtendMaxKt);
        Assert.Equal(220, Defaults.GearRetractMaxKt);
    }

    [Fact]
    public void DefaultFlapPlacards_FollowTheSixStepHandle()
    {
        // 0 = UP, 1 = CONF 1, 2 = CONF 1+F, 3 = CONF 2, 4 = CONF 3, 5 = FULL.
        var placards = SopOptions.DefaultFlapPlacards.ToDictionary(p => p.FlapHandle, p => p.MaxKt);

        Assert.Equal(230, placards[1]);
        Assert.Equal(215, placards[2]);
        Assert.Equal(200, placards[3]);
        Assert.Equal(185, placards[4]);
        Assert.Equal(177, placards[5]);
        Assert.Equal("1+F", SopOptions.FlapHandleLabel(2));
        Assert.Equal("FULL", SopOptions.FlapHandleLabel(5));
    }

    [Fact]
    public void GearDown_AboveExtensionLimit_IsRefusedWithBothSpeeds()
        => Assert.Equal(
            "Negative — speed 262, gear extension limit is 250.",
            GearCallDecider.SpeedRefusal(up: false, indicatedAirspeedKt: 261.6, Defaults));

    [Fact]
    public void GearUp_AboveRetractionLimit_IsRefusedWithBothSpeeds()
        => Assert.Equal(
            "Negative — speed 230, gear retraction limit is 220.",
            GearCallDecider.SpeedRefusal(up: true, indicatedAirspeedKt: 230, Defaults));

    [Theory]
    [InlineData(false, 250)]
    [InlineData(false, 180)]
    [InlineData(true, 220)]
    [InlineData(true, 160)]
    public void AtOrBelowTheLimit_IsAccepted(bool up, double ias)
        => Assert.Null(GearCallDecider.SpeedRefusal(up, ias, Defaults));

    [Fact]
    public void ZeroLimit_DisablesTheCheck()
        => Assert.Null(GearCallDecider.SpeedRefusal(
            up: false, indicatedAirspeedKt: 330, new SopOptions { GearExtendMaxKt = 0 }));
}
