using ProsimCompanion.Core.Flight;
using ProsimCompanion.Speech.Callouts;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

public sealed class TransitionReminderCoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(30);

    private static TransitionReminderInput Input(
        FlightPhase phase, double altitude, bool? std,
        double? ta = 6000, double? tl = 70, double? qnh = 1013, DateTimeOffset? now = null, bool live = true,
        double? destinationTa = null)
        => new(phase, live, altitude, std, ta, tl, qnh, now ?? T0, destinationTa);

    [Fact]
    public void Climb_PastTransitionAltitude_OnQnh_RemindsStandard_AfterDelay_Once()
    {
        var core = new TransitionReminderCore();

        Assert.Null(core.Process(Input(FlightPhase.Climb, 6200, std: false), Delay));
        Assert.Null(core.Process(Input(FlightPhase.Climb, 6600, std: false, now: T0.AddSeconds(20)), Delay));

        var reminder = core.Process(Input(FlightPhase.Climb, 7100, std: false, now: T0.AddSeconds(30)), Delay);
        Assert.NotNull(reminder);
        Assert.Equal(TransitionReminderKind.Standard, reminder.Kind);
        Assert.Equal(TransitionReminderCore.StandardText, reminder.Text);
        Assert.Equal(6000, reminder.ThresholdFt);

        // Still on QNH a minute later: said once, not nagged.
        Assert.Null(core.Process(Input(FlightPhase.Climb, 9000, std: false, now: T0.AddSeconds(90)), Delay));
    }

    [Fact]
    public void Climb_SettingStandardInsideTheDelay_Disarms()
    {
        var core = new TransitionReminderCore();

        Assert.Null(core.Process(Input(FlightPhase.Climb, 6200, std: false), Delay));
        Assert.Null(core.Process(Input(FlightPhase.Climb, 6600, std: true, now: T0.AddSeconds(10)), Delay));
        Assert.Null(core.Process(Input(FlightPhase.Climb, 7000, std: true, now: T0.AddSeconds(60)), Delay));
    }

    [Fact]
    public void Climb_BelowTransitionAltitude_Silent()
    {
        var core = new TransitionReminderCore();
        Assert.Null(core.Process(Input(FlightPhase.Climb, 5900, std: false), Delay));
        Assert.Null(core.Process(Input(FlightPhase.Climb, 5990, std: false, now: T0.AddMinutes(5)), Delay));
    }

    [Fact]
    public void Descent_BelowTransitionLevel_OnStd_RemindsQnh_WithDigits()
    {
        var core = new TransitionReminderCore();

        Assert.Null(core.Process(Input(FlightPhase.Descent, 6900, std: true, qnh: 1013), Delay));
        var reminder = core.Process(Input(FlightPhase.Descent, 6400, std: true, qnh: 1013, now: T0.AddSeconds(30)), Delay);

        Assert.NotNull(reminder);
        Assert.Equal(TransitionReminderKind.Qnh, reminder.Kind);
        Assert.Equal("Transition level. QNH one zero one three, please.", reminder.Text);
        Assert.Equal(7000, reminder.ThresholdFt);
        Assert.Equal(1013, reminder.QnhHpa);
    }

    [Fact]
    public void Descent_WithoutDestinationQnh_AsksForQnhPlainly()
    {
        var core = new TransitionReminderCore();
        core.Process(Input(FlightPhase.Approach, 6900, std: true, qnh: null), Delay);
        var reminder = core.Process(Input(FlightPhase.Approach, 6400, std: true, qnh: null, now: T0.AddSeconds(30)), Delay);

        Assert.NotNull(reminder);
        Assert.Equal(TransitionReminderCore.QnhUnknownText, reminder.Text);
        Assert.Null(reminder.QnhHpa);
    }

    [Fact]
    public void Descent_AlreadyOnQnh_Silent()
    {
        var core = new TransitionReminderCore();
        Assert.Null(core.Process(Input(FlightPhase.Descent, 6900, std: false), Delay));
        Assert.Null(core.Process(Input(FlightPhase.Descent, 3000, std: false, now: T0.AddMinutes(5)), Delay));
    }

    [Fact]
    public void NoNavDataFigure_NoReminder_NeverAGuess()
    {
        var core = new TransitionReminderCore();
        Assert.Null(core.Process(Input(FlightPhase.Climb, 19000, std: false, ta: null), Delay));
        Assert.Null(core.Process(Input(FlightPhase.Climb, 19500, std: false, ta: null, now: T0.AddMinutes(2)), Delay));
        Assert.Null(core.Process(Input(FlightPhase.Descent, 3000, std: true, tl: null), Delay));
        Assert.Null(core.Process(Input(FlightPhase.Descent, 2500, std: true, tl: null, now: T0.AddMinutes(4)), Delay));
        Assert.Null(core.Process(Input(FlightPhase.Descent, 2000, std: true, tl: 0, now: T0.AddMinutes(6)), Delay));
    }

    [Fact]
    public void NotLive_UnknownStdGate_OrDisabled_HoldsAndDropsTheArmTimer()
    {
        var core = new TransitionReminderCore();

        Assert.Null(core.Process(Input(FlightPhase.Climb, 6500, std: false), Delay));
        // The gate drops out for a while: the arm timer restarts afterwards.
        Assert.Null(core.Process(Input(FlightPhase.Climb, 7000, std: null, now: T0.AddSeconds(20)), Delay));
        Assert.Null(core.Process(Input(FlightPhase.Climb, 7500, std: false, now: T0.AddSeconds(40)), Delay));
        Assert.NotNull(core.Process(Input(FlightPhase.Climb, 8000, std: false, now: T0.AddSeconds(70)), Delay));

        var other = new TransitionReminderCore();
        Assert.Null(other.Process(Input(FlightPhase.Climb, 6500, std: false, live: false), Delay));
        Assert.Null(other.Process(Input(FlightPhase.Climb, 6500, std: false, live: false, now: T0.AddMinutes(2)), Delay));
    }

    [Fact]
    public void GoAround_ClimbingAgain_ReArmsTheQnhReminder_AndDescentReArmsStandard()
    {
        var core = new TransitionReminderCore();

        core.Process(Input(FlightPhase.Climb, 6500, std: false), Delay);
        Assert.NotNull(core.Process(Input(FlightPhase.Climb, 8000, std: false, now: T0.AddSeconds(30)), Delay));

        core.Process(Input(FlightPhase.Descent, 6900, std: true, now: T0.AddMinutes(30)), Delay);
        Assert.NotNull(core.Process(Input(FlightPhase.Descent, 6400, std: true, now: T0.AddMinutes(30).AddSeconds(30)), Delay));

        // Go-around: back up through the transition altitude on QNH — reminded again.
        core.Process(Input(FlightPhase.Climb, 6500, std: false, now: T0.AddMinutes(40)), Delay);
        Assert.NotNull(core.Process(Input(FlightPhase.Climb, 8000, std: false, now: T0.AddMinutes(40).AddSeconds(30)), Delay));

        // And the second descent gets its QNH reminder again.
        core.Process(Input(FlightPhase.Descent, 6900, std: true, now: T0.AddMinutes(50)), Delay);
        Assert.NotNull(core.Process(Input(FlightPhase.Descent, 6400, std: true, now: T0.AddMinutes(50).AddSeconds(30)), Delay));
    }

    [Fact]
    public void Reset_ForgetsEveryLatch()
    {
        var core = new TransitionReminderCore();
        core.Process(Input(FlightPhase.Climb, 6500, std: false), Delay);
        Assert.NotNull(core.Process(Input(FlightPhase.Climb, 8000, std: false, now: T0.AddSeconds(30)), Delay));

        core.Reset();

        core.Process(Input(FlightPhase.Climb, 6500, std: false, now: T0.AddMinutes(5)), Delay);
        Assert.NotNull(core.Process(Input(FlightPhase.Climb, 8000, std: false, now: T0.AddMinutes(5).AddSeconds(30)), Delay));
    }

    // Issue #198, owner's flight EGLL-LOWW 2026-11-14: "LOWW TL null" — no descent reminder
    // although the destination has a transition altitude. The level is derived from it.
    [Theory]
    [InlineData(10000, 1022, 10000)] // the flight: LOWW, QNH 1022 — FL100 lies at 10,238 ft on QNH
    [InlineData(10000, 1000, 11000)] // low pressure: FL100 would be below the transition altitude
    [InlineData(5000, 1022, 5000)]
    [InlineData(5000, 1013, 6000)]   // FL50 on 1013 hPa is 7 ft under the altitude
    [InlineData(5000, 1014, 5000)]
    [InlineData(5000, 980, 6000)]
    [InlineData(5000, 975, 7000)]
    [InlineData(6000, 1055, 5000)]   // very high pressure: FL50 is already above 6,000 ft on QNH
    public void DescentThreshold_WithoutADatabaseLevel_IsTheLowestFlightLevelAtOrAboveTheTransitionAltitude(
        double ta, double qnh, double expectedFt)
    {
        var threshold = TransitionReminderCore.ResolveDescentThreshold(null, ta, qnh);

        Assert.NotNull(threshold);
        Assert.Equal(expectedFt, threshold.Value.ThresholdFt);
        Assert.Equal(TransitionThresholdSource.DerivedFromQnh, threshold.Value.Source);
        // The level is at or above the altitude, and the one below it is not.
        Assert.True(expectedFt >= TransitionReminderCore.PressureAltitudeFt(ta, qnh) - 0.5);
        Assert.True(expectedFt - 1000 < TransitionReminderCore.PressureAltitudeFt(ta, qnh) - 0.5);
    }

    [Fact]
    public void DescentThreshold_Sources_DatabaseFirst_ThenQnh_ThenTheMargin_ThenNothing()
    {
        var database = TransitionReminderCore.ResolveDescentThreshold(70, 5000, 1022);
        Assert.Equal(7000, database!.Value.ThresholdFt);
        Assert.Equal("database TL", database.Value.Describe());

        var derived = TransitionReminderCore.ResolveDescentThreshold(null, 5000, 1022);
        Assert.Equal("derived from TA 5000 ft and QNH 1022", derived!.Value.Describe());

        var margin = TransitionReminderCore.ResolveDescentThreshold(0, 5000, null);
        Assert.Equal(6000, margin!.Value.ThresholdFt);
        Assert.Equal(TransitionThresholdSource.DerivedFromMargin, margin.Value.Source);
        Assert.Equal("derived from TA 5000 ft plus 1000 ft (no QNH)", margin.Value.Describe());

        // A QNH that cannot be one is no QNH.
        Assert.Equal(TransitionThresholdSource.DerivedFromMargin, TransitionReminderCore.ResolveDescentThreshold(null, 5000, 0)!.Value.Source);

        Assert.Null(TransitionReminderCore.ResolveDescentThreshold(null, null, 1022));
        Assert.Null(TransitionReminderCore.ResolveDescentThreshold(null, 0, null));
    }

    [Fact]
    public void Descent_NoDatabaseLevel_RemindsBelowTheDerivedLevel_AndNamesItsSource()
    {
        var core = new TransitionReminderCore();

        // LOWW on the flight: TA 10,000 ft, QNH 1022, still on STD.
        Assert.Null(core.Process(Input(FlightPhase.Descent, 10400, std: true, tl: null, qnh: 1022, destinationTa: 10000), Delay));
        Assert.Null(core.Process(Input(FlightPhase.Descent, 10050, std: true, tl: null, qnh: 1022, destinationTa: 10000, now: T0.AddMinutes(2)), Delay));
        Assert.Null(core.Process(Input(FlightPhase.Descent, 9900, std: true, tl: null, qnh: 1022, destinationTa: 10000, now: T0.AddMinutes(3)), Delay));
        var reminder = core.Process(Input(FlightPhase.Descent, 9300, std: true, tl: null, qnh: 1022, destinationTa: 10000, now: T0.AddMinutes(3).AddSeconds(30)), Delay);

        Assert.NotNull(reminder);
        Assert.Equal(TransitionReminderKind.Qnh, reminder.Kind);
        Assert.Equal("Transition level. QNH one zero two two, please.", reminder.Text);
        Assert.Equal(10000, reminder.ThresholdFt);
        Assert.Equal(TransitionThresholdSource.DerivedFromQnh, reminder.Source);
        Assert.Equal("derived from TA 10000 ft and QNH 1022", reminder.ThresholdBasis);

        // Once per descent.
        Assert.Null(core.Process(Input(FlightPhase.Approach, 6000, std: true, tl: null, qnh: 1022, destinationTa: 10000, now: T0.AddMinutes(10)), Delay));
    }

    [Fact]
    public void Descent_NoDatabaseLevel_NoQnh_RemindsBelowTheAltitudePlusTheMargin()
    {
        var core = new TransitionReminderCore();

        Assert.Null(core.Process(Input(FlightPhase.Descent, 6200, std: true, tl: null, qnh: null, destinationTa: 5000), Delay));
        Assert.Null(core.Process(Input(FlightPhase.Descent, 5900, std: true, tl: null, qnh: null, destinationTa: 5000, now: T0.AddMinutes(1)), Delay));
        var reminder = core.Process(Input(FlightPhase.Descent, 5400, std: true, tl: null, qnh: null, destinationTa: 5000, now: T0.AddMinutes(1).AddSeconds(30)), Delay);

        Assert.NotNull(reminder);
        Assert.Equal(TransitionReminderCore.QnhUnknownText, reminder.Text);
        Assert.Equal(6000, reminder.ThresholdFt);
        Assert.Equal(TransitionThresholdSource.DerivedFromMargin, reminder.Source);
    }

    [Fact]
    public void Descent_DatabaseLevel_Wins_AndTheReminderSaysSo()
    {
        var core = new TransitionReminderCore();

        core.Process(Input(FlightPhase.Descent, 6900, std: true, tl: 70, qnh: 1022, destinationTa: 5000), Delay);
        var reminder = core.Process(Input(FlightPhase.Descent, 6400, std: true, tl: 70, qnh: 1022, destinationTa: 5000, now: T0.AddSeconds(30)), Delay);

        Assert.NotNull(reminder);
        Assert.Equal(7000, reminder.ThresholdFt);
        Assert.Equal(TransitionThresholdSource.DatabaseTransitionLevel, reminder.Source);
        Assert.Equal("database TL", reminder.ThresholdBasis);
    }

    [Fact]
    public void Descent_NeitherLevelNorAltitude_StaysSilent_AndAlreadyOnQnhStaysSilentOnADerivedLevel()
    {
        var core = new TransitionReminderCore();
        Assert.Null(core.Process(Input(FlightPhase.Descent, 3000, std: true, tl: null, destinationTa: null), Delay));
        Assert.Null(core.Process(Input(FlightPhase.Descent, 2500, std: true, tl: null, destinationTa: null, now: T0.AddMinutes(4)), Delay));

        // The flight itself: the pilot's side went to QNH above the level — nothing to say.
        Assert.Null(core.Process(Input(FlightPhase.Descent, 9500, std: false, tl: null, qnh: 1022, destinationTa: 10000, now: T0.AddMinutes(5)), Delay));
        Assert.Null(core.Process(Input(FlightPhase.Descent, 4000, std: false, tl: null, qnh: 1022, destinationTa: 10000, now: T0.AddMinutes(9)), Delay));
    }

    [Fact]
    public void Climb_TheStandardReminder_NamesTheDatabaseAltitude()
    {
        var core = new TransitionReminderCore();
        core.Process(Input(FlightPhase.Climb, 6500, std: false), Delay);
        var reminder = core.Process(Input(FlightPhase.Climb, 8153, std: false, now: T0.AddSeconds(30)), Delay);

        Assert.NotNull(reminder);
        Assert.Equal(TransitionThresholdSource.DatabaseTransitionAltitude, reminder.Source);
        Assert.Equal("database TA", reminder.ThresholdBasis);
    }

    [Theory]
    [InlineData(1013, "Transition level. QNH one zero one three, please.")]
    [InlineData(998.6, "Transition level. QNH niner niner niner, please.")]
    [InlineData(0, "Transition level. QNH, please.")]
    public void QnhText_ReadsDigits(double qnh, string expected)
    {
        Assert.Equal(expected, TransitionReminderCore.QnhText(qnh));
    }
}
