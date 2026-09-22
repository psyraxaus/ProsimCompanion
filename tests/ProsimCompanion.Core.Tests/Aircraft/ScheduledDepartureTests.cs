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
}
