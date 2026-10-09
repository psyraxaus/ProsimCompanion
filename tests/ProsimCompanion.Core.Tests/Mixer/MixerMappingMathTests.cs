using ProsimCompanion.Audio.Mixer;
using ProsimCompanion.Core.Configuration;
using Xunit;

namespace ProsimCompanion.Core.Tests.Mixer;

public sealed class MixerMappingMathTests
{
    [Theory]
    [InlineData(0, -60)]       // knob closed → floor
    [InlineData(1024, 12)]     // full knob → +12 dB (past unity, like the local backend)
    [InlineData(512, -24)]     // midpoint of the 72 dB span
    [InlineData(2048, 12)]     // clamp above the input range
    [InlineData(-10, -60)]     // clamp below
    public void Level_ScalesTheDefaultKnobRangeOntoTheDbSpan(double input, double expected)
    {
        Assert.Equal(expected, MixerMappingMath.Level(input, 0, 1024, -60, 12, invert: false), precision: 3);
    }

    [Fact]
    public void Level_Invert_RunsMaxToMin()
    {
        Assert.Equal(12, MixerMappingMath.Level(0, 0, 1024, -60, 12, invert: true), precision: 3);
        Assert.Equal(-60, MixerMappingMath.Level(1024, 0, 1024, -60, 12, invert: true), precision: 3);
    }

    [Fact]
    public void Level_CustomRanges_AndRoundingToTenthOfADb()
    {
        // 0..100 % onto −20..0 dB; 33 % → −13.4 (not −13.4000001).
        Assert.Equal(-13.4, MixerMappingMath.Level(33, 0, 100, -20, 0, invert: false), precision: 6);
    }

    [Fact]
    public void Level_DegenerateInputRange_ReturnsTheOutputMinimum()
    {
        Assert.Equal(-60, MixerMappingMath.Level(500, 1024, 1024, -60, 12, invert: false));
        Assert.Equal(-60, MixerMappingMath.Level(double.NaN, 0, 1024, -60, 12, invert: false));
    }

    [Theory]
    [InlineData(1, 0.5, false, 1)]   // latch 1 at/above threshold → 1
    [InlineData(0, 0.5, false, 0)]
    [InlineData(1, 0.5, true, 0)]    // REC latch 1 = unmuted → Mute 0
    [InlineData(0, 0.5, true, 1)]    // REC latch 0 = muted → Mute 1
    [InlineData(0.5, 0.5, false, 1)] // threshold is inclusive
    [InlineData(double.NaN, 0.5, false, 0)]
    public void Toggle_ThresholdAndInvert(double input, double threshold, bool invert, double expected)
    {
        Assert.Equal(expected, MixerMappingMath.Toggle(input, threshold, invert));
    }

    [Fact]
    public void Apply_PicksTheKindFromTheMapping()
    {
        var level = new MixerMapping { Kind = MixerMappingKind.Level, InputMin = 0, InputMax = 1024, OutputMinDb = -60, OutputMaxDb = 12 };
        var toggle = new MixerMapping { Kind = MixerMappingKind.Toggle, Threshold = 0.5, Invert = true };

        Assert.Equal(-24, MixerMappingMath.Apply(level, 512), precision: 3);
        Assert.Equal(0, MixerMappingMath.Apply(toggle, 1));
        Assert.Equal(1, MixerMappingMath.Apply(toggle, 0));
    }
}
