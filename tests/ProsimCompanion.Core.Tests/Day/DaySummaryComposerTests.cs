using ProsimCompanion.Core.Day;
using Xunit;

namespace ProsimCompanion.Core.Tests.Day;

public sealed class DaySummaryComposerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 8, 6, 0, 0, TimeSpan.Zero);

    private static DayState TwoLegDay()
    {
        var day = new DayState
        {
            DayId = "2026-08-08-A",
            StartedUtc = T0.ToString("O"),
            DutyStartUtc = T0.ToString("O"),
            State = DayPhase.Ended,
            Mode = DayMode.Planned,
            PostFlightAllowanceMin = 15,
            CurrentLegIndex = 2,
        };
        day.Legs.Add(new DayLeg
        {
            Index = 1,
            From = "YSSY",
            To = "YMML",
            BlockMinutes = 80,
            ActualOnUtc = T0.AddMinutes(120).ToString("O"),
            Stabilized = true,
            DefectsRaised = 1,
        });
        day.Legs.Add(new DayLeg
        {
            Index = 2,
            From = "YMML",
            To = "YSSY",
            BlockMinutes = 85,
            ScheduledOnUtc = T0.AddMinutes(340).ToString("O"),
            ActualOnUtc = T0.AddMinutes(345).ToString("O"),
            Stabilized = false,
            Abnormals = 1,
            MemoryDrills = 1,
            DefectsRectified = 1,
        });
        return day;
    }

    [Fact]
    public void Compose_BuildsTheFullDeterministicSummary()
    {
        var text = new DaySummaryComposer().Compose(TwoLegDay(), T0.AddMinutes(400));

        // Duty = last on-blocks (345) + allowance (15) = 6 hours; block = 165 = 2 h 45 m.
        Assert.Equal(
            "That's the duty day complete. 2 sectors flown. Total block 2 hours 45 minutes, duty 6 hours."
            + " We finished 5 minutes behind schedule. 1 of 2 approaches stabilized."
            + " 1 abnormal handled. 1 memory drill called. 1 defect raised, 1 cleared.",
            text);
    }

    [Fact]
    public void Compose_OmitsSectionsWithNothingToSay()
    {
        var day = new DayState
        {
            DayId = "day-1",
            StartedUtc = T0.ToString("O"),
            DutyStartUtc = T0.ToString("O"),
            State = DayPhase.Ended,
            PostFlightAllowanceMin = 15,
        };
        day.Legs.Add(new DayLeg { Index = 1, BlockMinutes = 61, ActualOnUtc = T0.AddMinutes(70).ToString("O") });

        var text = new DaySummaryComposer().Compose(day, T0.AddMinutes(100));
        Assert.Equal(
            "That's the duty day complete. 1 sector flown. Total block 1 hour 1 minute, duty 1 hour 25 minutes.",
            text);
    }

    [Fact]
    public void BuildRecord_CarriesEveryAggregate()
    {
        var record = new DaySummaryComposer().BuildRecord(TwoLegDay(), T0.AddMinutes(400));

        Assert.Equal("2026-08-08-A", record.DayId);
        Assert.Equal("2026-08-08", record.Date);
        Assert.Equal(2, record.Legs);
        Assert.Equal(["YSSY", "YMML", "YSSY"], record.Route);
        Assert.Equal(165, record.BlockMinutes);
        Assert.Equal(360, record.DutyMinutes);
        Assert.Equal(5, record.DelayMinutes);
        Assert.Equal(1, record.StabilizedApproaches);
        Assert.Equal(2, record.JudgedApproaches);
        Assert.Equal(1, record.Abnormals);
        Assert.Equal(1, record.MemoryDrills);
        Assert.Equal(1, record.DefectsRaised);
        Assert.Equal(1, record.DefectsRectified);
    }

    [Theory]
    [InlineData(1, 55, null, 0, "That's leg 1 — 55 minutes block.")]
    [InlineData(2, 1, 0, 0, "That's leg 2 — 1 minute block, on schedule.")]
    [InlineData(2, 62, 12, 0, "That's leg 2 — 62 minutes block, 12 minutes behind schedule.")]
    [InlineData(3, 48, -6, 0, "That's leg 3 — 48 minutes block, 6 minutes ahead of schedule.")]
    [InlineData(1, 70, null, 2, "That's leg 1 — 70 minutes block. 2 items still in the tech log for the next sector.")]
    [InlineData(1, 70, 1, 1, "That's leg 1 — 70 minutes block, 1 minute behind schedule. 1 item still in the tech log for the next sector.")]
    public void TurnaroundLine_CoversScheduleAndTechLogVariants(
        int leg, int block, int? delay, int open, string expected)
        => Assert.Equal(expected, DaySummaryComposer.TurnaroundLine(leg, block, delay, open));
}
