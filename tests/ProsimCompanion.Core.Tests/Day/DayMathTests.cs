using ProsimCompanion.Core.Day;
using Xunit;

namespace ProsimCompanion.Core.Tests.Day;

public sealed class DayMathTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 8, 6, 0, 0, TimeSpan.Zero);

    private static DayState Day(DayPhase state = DayPhase.OnLeg) => new()
    {
        DayId = "day-x",
        StartedUtc = T0.ToString("O"),
        DutyStartUtc = T0.ToString("O"),
        State = state,
        PostFlightAllowanceMin = 15,
    };

    [Fact]
    public void DutyMinutes_OpenDay_RunsToNow()
    {
        var day = Day();
        Assert.Equal(125, DayMath.DutyMinutes(day, T0.AddMinutes(125)));
    }

    [Fact]
    public void DutyMinutes_EndedDay_IsLastOnBlocksPlusAllowance_RegardlessOfNow()
    {
        var day = Day(DayPhase.Ended);
        day.Legs.Add(new DayLeg { Index = 1, ActualOnUtc = T0.AddMinutes(60).ToString("O") });
        day.Legs.Add(new DayLeg { Index = 2, ActualOnUtc = T0.AddMinutes(200).ToString("O") });

        // ONE formula: end = last actual on-blocks + allowance; "now" is irrelevant once ended.
        Assert.Equal(215, DayMath.DutyMinutes(day, T0.AddDays(3)));
    }

    [Fact]
    public void DutyMinutes_EndedDayWithNoCompletedLegs_IsJustTheAllowance()
    {
        Assert.Equal(15, DayMath.DutyMinutes(Day(DayPhase.Ended), T0.AddHours(9)));
    }

    [Fact]
    public void DutyMinutes_MissingDutyStart_IsZero()
    {
        var day = Day();
        day.DutyStartUtc = null;
        Assert.Equal(0, DayMath.DutyMinutes(day, T0.AddHours(2)));
    }

    [Fact]
    public void BlockMinutes_SumsFilledLegs()
    {
        var day = Day();
        day.Legs.Add(new DayLeg { Index = 1, BlockMinutes = 55 });
        day.Legs.Add(new DayLeg { Index = 2 });
        day.Legs.Add(new DayLeg { Index = 3, BlockMinutes = 62 });
        Assert.Equal(117, DayMath.BlockMinutes(day));
    }

    [Theory]
    [InlineData(10, 10)]   // behind schedule
    [InlineData(-8, -8)]   // ahead of schedule
    [InlineData(0, 0)]     // on the dot
    public void DelayMinutes_ComparesLastScheduledLeg(int actualOffsetMin, int expected)
    {
        var day = Day();
        var scheduled = T0.AddMinutes(90);
        day.Legs.Add(new DayLeg
        {
            Index = 1,
            ScheduledOnUtc = scheduled.ToString("O"),
            ActualOnUtc = scheduled.AddMinutes(actualOffsetMin).ToString("O"),
        });
        Assert.Equal(expected, DayMath.DelayMinutes(day));
    }

    [Fact]
    public void DelayMinutes_NoScheduledLeg_IsNull()
    {
        var day = Day();
        day.Legs.Add(new DayLeg { Index = 1, ActualOnUtc = T0.AddMinutes(50).ToString("O") });
        Assert.Null(DayMath.DelayMinutes(day));
    }

    [Fact]
    public void BuildView_UsesTheSameFormulas()
    {
        var day = Day();
        day.CurrentLegIndex = 1;
        day.Legs.Add(new DayLeg { Index = 1, From = "YSSY", To = "YMML", BlockMinutes = 80, ActualOnUtc = T0.AddMinutes(95).ToString("O") });

        var view = DayMath.BuildView(day, T0.AddMinutes(100));
        Assert.True(view.Active);
        Assert.Equal("OnLeg", view.State);
        Assert.Equal(1, view.LegIndex);
        Assert.Equal(1, view.LegsCompleted);
        Assert.Equal("YSSY", view.From);
        Assert.Equal(80, view.BlockMinutes);
        Assert.Equal(100, view.DutyMinutes);
        Assert.Null(view.DelayMinutes);
    }

    [Theory]
    [InlineData(0, "0 minutes")]
    [InlineData(1, "1 minute")]
    [InlineData(45, "45 minutes")]
    [InlineData(60, "1 hour")]
    [InlineData(120, "2 hours")]
    [InlineData(61, "1 hour 1 minute")]
    [InlineData(460, "7 hours 40 minutes")]
    public void FormatHoursMinutes_SpeaksNaturally(int minutes, string expected)
        => Assert.Equal(expected, DayMath.FormatHoursMinutes(minutes));
}
