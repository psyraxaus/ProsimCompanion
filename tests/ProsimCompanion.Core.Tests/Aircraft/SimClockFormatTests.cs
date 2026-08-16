using ProsimCompanion.Core.Aircraft;
using Xunit;

namespace ProsimCompanion.Core.Tests.Aircraft;

/// <summary>Header sim-clock formatting (issue #71). simulator.zuluTime is declared TimeSpan
/// and simulator.time DateTime in the A322 catalog, but the transport may deliver strings or
/// numbers — every plausible shape must format, and garbage must yield null (UTC fallback).</summary>
public sealed class SimClockFormatTests
{
    [Fact]
    public void TimeSpan_FormatsAsHHmmZ()
        => Assert.Equal("13:05Z", SimClockFormat.TryFormatZuluTime(new TimeSpan(13, 5, 42)));

    [Fact]
    public void TimeSpan_OverADay_WrapsInto24h()
        => Assert.Equal("01:30Z", SimClockFormat.TryFormatZuluTime(TimeSpan.FromHours(25.5)));

    [Fact]
    public void TimeSpan_Negative_WrapsInto24h()
        => Assert.Equal("23:00Z", SimClockFormat.TryFormatZuluTime(TimeSpan.FromHours(-1)));

    [Fact]
    public void DateTime_UsesItsTimeOfDay()
        => Assert.Equal("08:09Z", SimClockFormat.TryFormatZuluTime(new DateTime(2026, 8, 16, 8, 9, 30)));

    [Fact]
    public void String_TimeSpanShape_Parses()
        => Assert.Equal("09:41Z", SimClockFormat.TryFormatZuluTime("09:41:12"));

    [Fact]
    public void Double_IsSecondsSinceMidnight()
        => Assert.Equal("01:01Z", SimClockFormat.TryFormatZuluTime(3661.0));

    [Fact]
    public void Garbage_YieldsNull()
    {
        Assert.Null(SimClockFormat.TryFormatZuluTime(null));
        Assert.Null(SimClockFormat.TryFormatZuluTime("not a time"));
        Assert.Null(SimClockFormat.TryFormatZuluTime(double.NaN));
    }

    [Fact]
    public void Date_FromDateTime_IsDdMonUpper()
        => Assert.Equal("16AUG", SimClockFormat.TryFormatDate(new DateTime(2026, 8, 16, 8, 9, 30)));

    [Fact]
    public void Date_FromUnpopulatedDefault_IsNull()
        => Assert.Null(SimClockFormat.TryFormatDate(default(DateTime)));

    [Fact]
    public void Date_FromGarbage_IsNull()
    {
        Assert.Null(SimClockFormat.TryFormatDate(null));
        Assert.Null(SimClockFormat.TryFormatDate(12345));
    }
}
