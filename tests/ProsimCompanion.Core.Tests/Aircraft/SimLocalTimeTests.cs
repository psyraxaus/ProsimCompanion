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
        var simVars = new Mock<ISimVars>();
        simVars.Setup(s => s.SubscribeDynamic("LOCAL TIME", "seconds", It.IsAny<DataRefTier>())).Returns(sub);
        using var clock = new SimLocalTime(simVars.Object);

        Assert.Null(clock.LocalTimeOfDay);

        sub.RaisePush(16 * 3600 + 35 * 60 + 12.0);
        Assert.Equal(new TimeSpan(16, 35, 12), clock.LocalTimeOfDay);

        sub.RaisePush(90_000.0);
        Assert.Null(clock.LocalTimeOfDay);
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
