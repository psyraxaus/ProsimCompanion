using ProsimCompanion.Core.Speech;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Numbers as words (owner report 2026-11-14: the FO read "8700 kilograms" as
/// "eighty seven, zero" because the app left the figure to the TTS engine).</summary>
public sealed class NumberWordsTests
{
    [Theory]
    [InlineData(0, "zero")]
    [InlineData(7, "seven")]
    [InlineData(13, "thirteen")]
    [InlineData(40, "forty")]
    [InlineData(99, "ninety nine")]
    [InlineData(100, "one hundred")]
    [InlineData(148, "one hundred forty eight")]
    [InlineData(800, "eight hundred")]
    [InlineData(1500, "one thousand five hundred")]
    [InlineData(8700, "eight thousand seven hundred")]
    [InlineData(10000, "ten thousand")]
    [InlineData(18820, "eighteen thousand eight hundred twenty")]
    [InlineData(62300, "sixty two thousand three hundred")]
    [InlineData(1000000, "one million")]
    [InlineData(-180, "minus one hundred eighty")]
    public void Cardinal(long value, string expected)
        => Assert.Equal(expected, NumberWords.Cardinal(value));

    [Theory]
    [InlineData("6.8", "six point eight")]
    [InlineData("62.30", "sixty two point three zero")]
    [InlineData("2,500", "two thousand five hundred")]
    [InlineData("-2", "minus two")]
    [InlineData("090", "zero niner zero")]       // a leading zero is an identifier
    [InlineData("12345678", "one two three four five six seven eight")]
    [InlineData("0.5", "zero point five")]
    [InlineData("abc", "abc")]
    public void Number(string written, string expected)
        => Assert.Equal(expected, NumberWords.Number(written));

    [Fact]
    public void Decimal_RoundsToTheAskedPlaces()
        => Assert.Equal("six point eight", NumberWords.Decimal(6.84, 1));

    [Theory]
    [InlineData(9, 0, "zero niner zero zero")]
    [InlineData(18, 40, "one eight four zero")]
    public void Clock(int hour, int minute, string expected)
        => Assert.Equal(expected, NumberWords.Clock(hour, minute));

    // ICAO Annex 10 Vol II, FAA AIM 4-2-9 and CAP 413 agree on altitudes.
    [Theory]
    [InlineData(800, "eight hundred")]
    [InlineData(2500, "two thousand five hundred")]
    [InlineData(6000, "six thousand")]
    [InlineData(10000, "one zero thousand")]
    [InlineData(13500, "one three thousand five hundred")]
    [InlineData(9900, "niner thousand niner hundred")]
    [InlineData(2550, "two five five zero")]
    public void RadioAltitude(int feet, string expected)
        => Assert.Equal(expected, NumberWords.RadioAltitude(feet));

    // FAA JO 7110.65 2-4-20 examples.
    [Theory]
    [InlineData("52", "fifty two")]
    [InlineData("100", "one hundred")]
    [InlineData("110", "one ten")]
    [InlineData("3015", "thirty fifteen")]
    [InlineData("101", "one zero one")]
    [InlineData("010", "zero one zero")]
    [InlineData("1004", "ten zero four")]
    [InlineData("850", "eight fifty")]
    [InlineData("2000", "two thousand")]
    public void GroupForm(string digits, string expected)
        => Assert.Equal(expected, NumberWords.GroupForm(digits));
}
