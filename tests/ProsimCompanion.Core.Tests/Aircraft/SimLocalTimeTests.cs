using Moq;
using ProsimCompanion.Core.Aircraft;
using Xunit;

namespace ProsimCompanion.Core.Tests.Aircraft;

/// <summary>Owner report 2026-10-25: the FO said "good morning" at sim 16:35 because the
/// briefing had no clock. The local time comes from MSFS's LOCAL TIME SimVar; absent or
/// stale means no greeting word rather than a guessed one.</summary>
public sealed class SimLocalTimeTests
{
    [Theory]
    [InlineData(4, "morning")] // the owner's 04:50 EGLL dawn departure (2026-10-29)
    [InlineData(5, "morning")]
    [InlineData(11, "morning")]
    [InlineData(12, "afternoon")]
    [InlineData(16, "afternoon")]
    [InlineData(17, "afternoon")]
    [InlineData(18, "evening")]
    [InlineData(23, "evening")]
    [InlineData(0, "evening")]
    [InlineData(3, "evening")]
    public void Word_FollowsTheCrewConvention(int hour, string expected)
        => Assert.Equal(expected, TimeOfDayGreeting.Word(TimeSpan.FromHours(hour) + TimeSpan.FromMinutes(35)));

    [Fact]
    public void Greeting_NullWithoutAClock()
    {
        Assert.Null(TimeOfDayGreeting.Greeting(null));
        Assert.Equal("Good afternoon", TimeOfDayGreeting.Greeting(new TimeSpan(16, 35, 0)));
    }

    /// <summary>Issue #229: the morning band's edges, minute-exact — 03:59 is still the
    /// overnight crew's evening, 04:00 the dawn departure's morning, noon the afternoon.</summary>
    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(3, 59, false)]
    [InlineData(4, 0, true)]
    [InlineData(11, 59, true)]
    [InlineData(12, 0, false)]
    [InlineData(17, 59, false)]
    [InlineData(18, 0, false)]
    [InlineData(23, 59, false)]
    public void IsMorning_FollowsTheMorningBandExactly(int hour, int minute, bool expected)
    {
        var clock = new TimeSpan(hour, minute, 0);
        Assert.Equal(expected, TimeOfDayGreeting.IsMorning(clock));
        Assert.Equal(expected, TimeOfDayGreeting.Word(clock) == TimeOfDayGreeting.Morning);
    }

    [Fact]
    public void Phrase_IsTheOneSpokenForm()
    {
        Assert.Equal("Good evening", TimeOfDayGreeting.Phrase(TimeOfDayGreeting.Evening));
        Assert.Equal(TimeOfDayGreeting.Phrase(TimeOfDayGreeting.Word(new TimeSpan(2, 30, 0))), TimeOfDayGreeting.Greeting(new TimeSpan(2, 30, 0)));
        Assert.Throws<ArgumentException>(() => TimeOfDayGreeting.Phrase(" "));
    }

    [Fact]
    public void NoSimVars_NoClock()
    {
        using var clock = new SimLocalTime(null);

        Assert.Null(clock.LocalTimeOfDay);
    }

    [Fact]
    public void LocalTimePush_BecomesTheTimeOfDay_NothingPushedIsNull()
    {
        var sub = new FakeSimVar("LOCAL TIME");
        var year = new FakeSimVar("LOCAL YEAR");
        var day = new FakeSimVar("LOCAL DAY OF YEAR");
        var simVars = new Mock<ISimVars>();
        simVars.Setup(s => s.SubscribeDynamic("LOCAL TIME", "seconds", It.IsAny<DataRefTier>())).Returns(sub);
        simVars.Setup(s => s.SubscribeDynamic("LOCAL YEAR", "number", It.IsAny<DataRefTier>())).Returns(year);
        simVars.Setup(s => s.SubscribeDynamic("LOCAL DAY OF YEAR", "number", It.IsAny<DataRefTier>())).Returns(day);
        var zulu = new FakeSimVar("ZULU TIME");
        simVars.Setup(s => s.SubscribeDynamic("ZULU TIME", "seconds", It.IsAny<DataRefTier>())).Returns(zulu);
        using var clock = new SimLocalTime(simVars.Object);
        Assert.Null(clock.ZuluTimeOfDay);

        Assert.Null(clock.LocalTimeOfDay);
        Assert.Null(clock.LocalDate);

        sub.RaisePush(16 * 3600 + 35 * 60 + 12.0);
        Assert.Equal(new TimeSpan(16, 35, 12), clock.LocalTimeOfDay);
        Assert.Null(clock.LocalDate); // the date SimVars have not pushed yet

        // Issue #194: the sim's calendar day from LOCAL YEAR + LOCAL DAY OF YEAR.
        year.RaisePush(2026.0);
        day.RaisePush(305.0);
        Assert.Equal(new DateOnly(2026, 11, 1), clock.LocalDate);
        zulu.RaisePush(15 * 3600 + 35 * 60 + 12.0);
        Assert.Equal(new TimeSpan(15, 35, 12), clock.ZuluTimeOfDay); // issue #195

        sub.RaisePush(90_000.0);
        Assert.Null(clock.LocalTimeOfDay);
        Assert.Null(clock.LocalDate); // no live clock, no date
    }

    private sealed class FakeSimVar(string name) : IDataRefSubscription
    {
        public string Name => name;
        public object? RawValue { get; private set; }
        public bool IsStale => false;
        public DateTimeOffset? LastUpdatedUtc { get; private set; }

        public event EventHandler? ValueChanged;

        public T GetValue<T>(T fallback) => DataRefCoercion.Coerce(RawValue, fallback);

        public void RaisePush(object value)
        {
            RawValue = value;
            LastUpdatedUtc = DateTimeOffset.UtcNow;
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
        }
    }
}
