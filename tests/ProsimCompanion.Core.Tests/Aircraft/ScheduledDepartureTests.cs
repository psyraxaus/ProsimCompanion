using ProsimCompanion.Core.Aircraft;
using Xunit;

namespace ProsimCompanion.Core.Tests.Aircraft;

public sealed class ScheduledDepartureTests
{
    private static readonly DateTimeOffset Ofp = new(2026, 9, 22, 10, 45, 0, TimeSpan.Zero);

    [Fact]
    public void NoOverride_UsesTheOfp()
        => Assert.Equal(Ofp, ScheduledDeparture.Effective(null, Ofp, Ofp.AddHours(-1)));

    [Fact]
    public void Nothing_IsNull()
        => Assert.Null(ScheduledDeparture.Effective(null, null, Ofp));

    [Fact]
    public void Override_AnchorsToTheSimDay()
    {
        var now = new DateTimeOffset(2026, 9, 22, 9, 0, 0, TimeSpan.Zero);

        var std = ScheduledDeparture.Effective(new TimeOnly(11, 30), Ofp, now);

        Assert.Equal(new DateTimeOffset(2026, 9, 22, 11, 30, 0, TimeSpan.Zero), std);
    }

    [Fact]
    public void Override_LongPast_IsTomorrow()
    {
        // 22:00 sim time, pilot types 01:15 for the after-midnight departure.
        var now = new DateTimeOffset(2026, 9, 22, 22, 0, 0, TimeSpan.Zero);

        var std = ScheduledDeparture.Effective(new TimeOnly(1, 15), Ofp, now);

        Assert.Equal(new DateTimeOffset(2026, 9, 23, 1, 15, 0, TimeSpan.Zero), std);
    }

    [Fact]
    public void Ofp_OnAnotherDay_IsAnchoredToTheSimDay()
    {
        // Issue #139 (EKCH 2026-09-27): sim clock 07:45Z on the 27th, SimBrief sched_out 08:30Z
        // on the 26th (the real-world date) — "STD in −1412 min". Same time-of-day, sim's day.
        var ofp = new DateTimeOffset(2026, 9, 26, 8, 30, 0, TimeSpan.Zero);
        var simNow = new DateTimeOffset(2026, 9, 27, 7, 45, 0, TimeSpan.Zero);

        Assert.Equal(new DateTimeOffset(2026, 9, 27, 8, 30, 0, TimeSpan.Zero), ScheduledDeparture.Effective(null, ofp, simNow));

        // A sim day three days AHEAD of the OFP, and one three days behind.
        Assert.Equal(new DateTimeOffset(2026, 9, 29, 8, 30, 0, TimeSpan.Zero), ScheduledDeparture.Effective(null, ofp, simNow.AddDays(2)));
        Assert.Equal(new DateTimeOffset(2026, 9, 23, 8, 30, 0, TimeSpan.Zero), ScheduledDeparture.Effective(null, ofp, simNow.AddDays(-4)));
    }

    [Fact]
    public void Ofp_WithinTwelveHours_IsUsedAsWritten()
    {
        // 11 h early and 11 h late both keep the OFP's own date.
        Assert.Equal(Ofp, ScheduledDeparture.Effective(null, Ofp, Ofp.AddHours(-11)));
        Assert.Equal(Ofp, ScheduledDeparture.Effective(null, Ofp, Ofp.AddHours(11)));
        // An after-midnight departure flown the evening before (sim 22:00, STD 01:15 next day).
        var std = new DateTimeOffset(2026, 9, 23, 1, 15, 0, TimeSpan.Zero);
        Assert.Equal(std, ScheduledDeparture.Effective(null, std, new DateTimeOffset(2026, 9, 22, 22, 0, 0, TimeSpan.Zero)));
    }
}
