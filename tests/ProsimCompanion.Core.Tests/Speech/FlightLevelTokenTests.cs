using ProsimCompanion.Speech.Commands;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>{flightLevel} rendering (issue #49): the token is the digits only — the words
/// "flight level" live in the template, like every other token.</summary>
public sealed class FlightLevelTokenTests
{
    [Theory]
    [InlineData(7_500.0, "seven five")]
    [InlineData(36_000.0, "three six zero")]
    [InlineData(4_960.0, "five zero")]
    public void Altitude_RendersAsFlightLevelDigits(double altitudeFt, string expected)
        => Assert.Equal(expected, SpokenValueFormatting.FlightLevel(altitudeFt));

    [Fact]
    public void MissingOrGroundValues_ReadUnavailable()
    {
        Assert.Equal("unavailable", SpokenValueFormatting.FlightLevel(null));
        Assert.Equal("unavailable", SpokenValueFormatting.FlightLevel(0));
        Assert.Equal("unavailable", SpokenValueFormatting.FlightLevel(-200));
    }

    [Fact]
    public void ApplyTokens_ExpandsFlightLevel()
    {
        var values = CommandTokenValues.Unavailable with { FlightLevel = "seven five" };

        Assert.Equal(
            "Standard, cross checked, passing flight level seven five",
            SpokenValueFormatting.ApplyTokens(
                "Standard, cross checked, passing flight level {flightLevel}", values));
    }
}
