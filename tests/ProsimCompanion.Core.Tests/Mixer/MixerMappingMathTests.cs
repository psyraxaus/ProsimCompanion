using ProsimCompanion.Audio.Mixer;
using Xunit;

namespace ProsimCompanion.Core.Tests.Mixer;

public sealed class MixerMappingMathTests
{
    [Theory]
    [InlineData(0, -60)]       // knob closed → floor
    [InlineData(1024, 12)]     // full knob → +12 dB (past unity, like the local backend)
    [InlineData(512, -24)]     // midpoint of the 72 dB span
    [InlineData(2048, 12)]     // clamp above the knob range
    [InlineData(-10, -60)]     // clamp below
    public void GainDb_ScalesTheKnobOntoTheDefaultSpan(double raw, double expected)
    {
        Assert.Equal(expected, MixerMappingMath.GainDb(raw, -60, 12), precision: 3);
    }

    [Fact]
    public void GainDb_HonoursACustomSpan_AndRoundsToTenthOfADb()
    {
        // 0..1024 onto −20..0 dB: 338 → −13.4 (not −13.3984375).
        Assert.Equal(-13.4, MixerMappingMath.GainDb(338, -20, 0), precision: 6);
        Assert.Equal(0, MixerMappingMath.GainDb(1024, -20, 0));
    }

    [Fact]
    public void Level_DegenerateInputRange_ReturnsTheOutputMinimum()
    {
        Assert.Equal(-60, MixerMappingMath.Level(500, 1024, 1024, -60, 12));
        Assert.Equal(-60, MixerMappingMath.Level(double.NaN, 0, 1024, -60, 12));
    }

    [Theory]
    [InlineData(1, 0)] // latch 1 = channel open → not muted
    [InlineData(0, 1)] // latch 0 = REC pushed in → muted
    public void MuteFromLatch_InvertsTheLatch(int latch, double expected)
    {
        Assert.Equal(expected, MixerMappingMath.MuteFromLatch(latch));
    }

    [Theory]
    [InlineData(0, 1)]    // fully down → muted
    [InlineData(10, 1)]   // inside the 1 % rest band → still muted
    [InlineData(11, 0)]   // just above the band → open
    [InlineData(1024, 0)]
    public void MuteFromDial_MutesAtTheBottomOfTheTravel(double raw, double expected)
    {
        Assert.Equal(expected, MixerMappingMath.MuteFromDial(raw));
    }
}
