using ProsimCompanion.Audio;
using Xunit;

namespace ProsimCompanion.Core.Tests.Audio;

public sealed class VolumeMathTests
{
    [Theory]
    [InlineData(0, 0f)]
    [InlineData(512, 0.5f)]
    [InlineData(1024, 1f)]
    [InlineData(2048, 1f)]   // clamp above the knob range
    [InlineData(-50, 0f)]    // clamp below
    public void Normalize_MapsKnobRangeLinearly(double raw, float expected)
    {
        Assert.Equal(expected, VolumeMath.Normalize(raw), precision: 4);
    }

    [Theory]
    [InlineData(0f, -60f)]   // knob closed ⇒ floor
    [InlineData(1f, 12f)]    // full knob deliberately past unity (predecessor-documented)
    [InlineData(0.5f, -24f)] // midpoint of the 72 dB span
    public void ToVoiceMeeterGainDb_MapsTheDocumentedSpan(float normalized, float expected)
    {
        Assert.Equal(expected, VolumeMath.ToVoiceMeeterGainDb(normalized), precision: 3);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.25f)]
    [InlineData(0.5f)]
    [InlineData(0.83333f)] // ~0 dB
    [InlineData(1f)]
    public void GainDbConversion_RoundTrips(float normalized)
    {
        var roundTripped = VolumeMath.FromVoiceMeeterGainDb(VolumeMath.ToVoiceMeeterGainDb(normalized));

        Assert.Equal(normalized, roundTripped, precision: 4);
    }

    [Fact]
    public void FromVoiceMeeterGainDb_ClampsOutOfRangeGains()
    {
        Assert.Equal(0f, VolumeMath.FromVoiceMeeterGainDb(-80f));
        Assert.Equal(1f, VolumeMath.FromVoiceMeeterGainDb(20f));
    }
}
