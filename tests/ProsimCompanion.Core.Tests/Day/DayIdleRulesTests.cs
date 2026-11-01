using ProsimCompanion.Core.Day;
using Xunit;

namespace ProsimCompanion.Core.Tests.Day;

/// <summary>ADR-0019 §8 (issue #217): the idle backstop counts from deboarding complete (else
/// on-blocks) and the FO's offer waits for cold and dark — pure rules, EHAM 2026-11-01 driven
/// through them.</summary>
public sealed class DayIdleRulesTests
{
    private static readonly DateTimeOffset Shutdown = new(2026, 11, 1, 1, 37, 25, TimeSpan.Zero);
    private static readonly DateTimeOffset Deboarded = new(2026, 11, 1, 1, 50, 3, TimeSpan.Zero);
    private static readonly DateTimeOffset NewPlan = new(2026, 11, 1, 3, 5, 6, TimeSpan.Zero);

    private static DayState Turnaround(string? idleSince, string? offered = null)
    {
        var day = new DayState
        {
            DayId = "day-20261101",
            StartedUtc = Shutdown.AddHours(-2).ToString("O"),
            State = DayPhase.Turnaround,
            CurrentLegIndex = 1,
            IdleSinceUtc = idleSince,
            EndOfDutyOfferedUtc = offered,
        };
        day.Legs.Add(new DayLeg { Index = 1, From = "EGLL", To = "EHAM", ActualOnUtc = Shutdown.ToString("O") });
        return day;
    }

    [Fact]
    public void IdleSince_IsTheDeboardingStamp_ElseOnBlocks_AndNothingOnALeg()
    {
        Assert.Equal(Deboarded, DayIdleRules.IdleSince(Turnaround(Deboarded.ToString("O"))));
        Assert.Equal(Shutdown, DayIdleRules.IdleSince(Turnaround(null))); // an older daystate: on-blocks

        var onLeg = Turnaround(Deboarded.ToString("O"));
        onLeg.State = DayPhase.OnLeg;
        Assert.Null(DayIdleRules.IdleSince(onLeg));
    }

    [Fact]
    public void Backstop_Eham88MinuteTurnaround_SurvivesTheOldNinetyAndTheNewDefault()
    {
        var day = Turnaround(Deboarded.ToString("O"));

        // The real turnaround: shutdown 01:37, deboarded 01:50, the new plan 03:05 (75 min after deboarding).
        Assert.False(DayIdleRules.ShouldAutoClose(day, 90, NewPlan));
        Assert.False(DayIdleRules.ShouldAutoClose(day, 300, NewPlan));
        Assert.True(DayIdleRules.ShouldAutoClose(day, 300, Deboarded.AddMinutes(300)));
        Assert.False(DayIdleRules.ShouldAutoClose(day, 0, Deboarded.AddHours(20))); // 0 = never
    }

    [Fact]
    public void Offer_WaitsForTheLaterOfIdleStartAndColdAndDark_OncePerDay()
    {
        var day = Turnaround(Deboarded.ToString("O"));
        var dark = Deboarded.AddMinutes(30);

        Assert.False(DayIdleRules.ShouldOffer(day, 10, null, dark.AddHours(2)));          // powered: never
        Assert.False(DayIdleRules.ShouldOffer(day, 10, dark, dark.AddMinutes(9)));        // not yet
        Assert.True(DayIdleRules.ShouldOffer(day, 10, dark, dark.AddMinutes(10)));
        Assert.False(DayIdleRules.ShouldOffer(day, 0, dark, dark.AddMinutes(60)));        // 0 = never ask

        // Dark before deboarding finished (a restart in the dark restores an earlier edge): the
        // deboarding stamp is the later one and counts.
        Assert.False(DayIdleRules.ShouldOffer(day, 10, Shutdown, Deboarded.AddMinutes(9)));
        Assert.True(DayIdleRules.ShouldOffer(day, 10, Shutdown, Deboarded.AddMinutes(10)));

        // Already offered today: never again.
        Assert.False(DayIdleRules.ShouldOffer(Turnaround(Deboarded.ToString("O"), dark.ToString("O")), 10, dark, dark.AddHours(3)));
    }
}
