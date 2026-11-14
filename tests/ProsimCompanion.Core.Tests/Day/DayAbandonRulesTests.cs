using ProsimCompanion.Core.Day;
using Xunit;

namespace ProsimCompanion.Core.Tests.Day;

/// <summary>Issue #239: an open day whose last save is older than twelve hours of REAL time is
/// abandoned at app start, a younger one resumes — the pure rule, and the engine's abandon.</summary>
public sealed class DayAbandonRulesTests
{
    // The sim PC, 2026-11-14 07:53:36 local (UTC+11): the start that resumed a week-old day.
    private static readonly DateTimeOffset Start = new(2026, 11, 13, 20, 53, 36, TimeSpan.Zero);

    private static DayState OnLeg()
    {
        var day = new DayState
        {
            DayId = "day-20261107-194915",
            StartedUtc = "2026-11-07T19:49:15.0000000+00:00",
            DutyStartUtc = "2026-11-07T19:49:15.0000000+00:00",
            State = DayPhase.OnLeg,
            CurrentLegIndex = 1,
        };
        day.Legs.Add(new DayLeg { Index = 1, From = "EGLL", To = "LOWW", ActualOffUtc = "2026-11-07T20:20:00.0000000+00:00" });
        return day;
    }

    [Fact]
    public void TheLimit_IsTwelveHours()
        => Assert.Equal(TimeSpan.FromHours(12), DayAbandonRules.StaleAfter);

    [Fact]
    public void AWeekOldOpenDay_IsStale_AndTheAgeIsTheTimeSinceTheLastSave()
    {
        var lastSave = new DateTimeOffset(2026, 11, 7, 20, 20, 0, TimeSpan.Zero);
        var age = DayAbandonRules.StaleAge(OnLeg(), lastSave, Start);
        Assert.Equal(Start - lastSave, age);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(3 * 60, false)]          // the three restarts of the 2026-11-14 flight
    [InlineData(12 * 60, false)]         // exactly the limit: still the day in progress
    [InlineData((12 * 60) + 1, true)]
    [InlineData(7 * 24 * 60, true)]
    public void OnlyASaveOlderThanTheLimit_IsStale(int minutesOld, bool stale)
        => Assert.Equal(stale, DayAbandonRules.StaleAge(OnLeg(), Start.AddMinutes(-minutesOld), Start) is not null);

    [Fact]
    public void ATurnaround_IsJudgedByTheSameClock()
    {
        var day = OnLeg();
        day.State = DayPhase.Turnaround;
        Assert.NotNull(DayAbandonRules.StaleAge(day, Start.AddHours(-13), Start));
        Assert.Null(DayAbandonRules.StaleAge(day, Start.AddHours(-2), Start));
    }

    [Fact]
    public void NoDay_AClosedDay_AnUnknownOrFutureSaveTime_AreNeverStale()
    {
        Assert.Null(DayAbandonRules.StaleAge(null, Start.AddDays(-7), Start));

        var ended = OnLeg();
        ended.State = DayPhase.Ended;
        Assert.Null(DayAbandonRules.StaleAge(ended, Start.AddDays(-7), Start));

        Assert.Null(DayAbandonRules.StaleAge(OnLeg(), null, Start));              // the day resumes, as before the rule
        Assert.Null(DayAbandonRules.StaleAge(OnLeg(), Start.AddHours(5), Start)); // a clock set back
    }

    [Fact]
    public void AbandonDay_DropsTheUnfinishedLeg_KeepsTheFlownOnes_AndLeavesNoDay()
    {
        var t0 = new DateTimeOffset(2026, 11, 7, 19, 49, 15, TimeSpan.Zero);
        var engine = new CompanyDayEngine();
        engine.StartDay(null, 15, t0);
        engine.SeedRouteFromOfp("EGLL", "EHAM", "BAW432");
        engine.StampOffBlocks(t0.AddMinutes(20));
        engine.CompleteLeg("session-1", t0.AddMinutes(100));
        engine.StartNextLeg();
        engine.SeedRouteFromOfp("EHAM", "EGLL", "BAW433");
        engine.StampOffBlocks(t0.AddMinutes(160)); // off blocks on leg 2, then the flight was given up

        var abandoned = engine.AbandonDay();

        Assert.NotNull(abandoned);
        Assert.Null(engine.Day);                                   // "no day": nothing to resume
        Assert.Equal(DaySnapshot.Empty, engine.BuildSnapshot(t0.AddDays(7)));
        Assert.Equal(DayPhase.Ended, abandoned.State);
        var leg = Assert.Single(abandoned.Legs);
        Assert.Equal(1, leg.Index);
        Assert.Equal("EHAM", leg.To);
        Assert.Equal(1, abandoned.LegsCompleted);

        // The logbook day built from it is the flown leg, with duty to its on-blocks plus the
        // allowance — never to the day a week later on which it was discarded.
        var record = new DaySummaryComposer().BuildRecord(abandoned, t0.AddDays(7));
        Assert.Equal(1, record.Legs);
        Assert.Equal(["EGLL", "EHAM"], record.Route);
        Assert.Equal(115, record.DutyMinutes);

        Assert.Null(engine.AbandonDay()); // nothing open any more
    }

    [Fact]
    public void AbandonDay_WithNothingFlown_LeavesNoLegs()
    {
        var engine = new CompanyDayEngine();
        engine.StartDay(null, 15, Start);
        engine.StampOffBlocks(Start.AddMinutes(20));

        var abandoned = engine.AbandonDay();

        Assert.NotNull(abandoned);
        Assert.Empty(abandoned.Legs);
        Assert.Equal(0, abandoned.LegsCompleted);
        Assert.Null(engine.Day);
    }
}
