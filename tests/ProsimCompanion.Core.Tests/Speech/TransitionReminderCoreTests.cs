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
        double? ta = 6000, double? tl = 70, double? qnh = 1013, DateTimeOffset? now = null, bool live = true)
        => new(phase, live, altitude, std, ta, tl, qnh, now ?? T0);

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

    [Theory]
    [InlineData(1013, "Transition level. QNH one zero one three, please.")]
    [InlineData(998.6, "Transition level. QNH niner niner niner, please.")]
    [InlineData(0, "Transition level. QNH, please.")]
    public void QnhText_ReadsDigits(double qnh, string expected)
    {
        Assert.Equal(expected, TransitionReminderCore.QnhText(qnh));
    }
}
