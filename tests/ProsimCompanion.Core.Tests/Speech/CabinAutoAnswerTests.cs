using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Speech.Cabin;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Cabin-call auto-answer (issue #11): the phase windows, the seat-relative panel and
/// the wait machine are pure so the write-safety rules hold without a sim.</summary>
public sealed class CabinAutoAnswerTests
{
    private static CabinOptions Options(bool ground = false, bool air = false)
        => new() { AutoAnswerGround = ground, AutoAnswerAir = air };

    [Fact]
    public void OffByDefault_NoPhaseIsAnswered()
    {
        var options = new CabinOptions();
        foreach (var phase in Enum.GetValues<FlightPhase>())
        {
            Assert.Equal(CabinAutoAnswerGate.None, CabinAutoAnswer.GateFor(phase, options));
            Assert.Null(CabinAutoAnswer.Plan(phase, options, humanIsRightSeat: false));
        }
    }

    [Theory]
    [InlineData(FlightPhase.PushbackAndStart)]
    [InlineData(FlightPhase.TaxiOut)]
    public void GroundWindow_IsPushbackAndTaxiOut(FlightPhase phase)
    {
        Assert.Equal(CabinAutoAnswerGate.Ground, CabinAutoAnswer.GateFor(phase, Options(ground: true)));
        Assert.Equal(CabinAutoAnswerGate.None, CabinAutoAnswer.GateFor(phase, Options(air: true)));
    }

    [Theory]
    [InlineData(FlightPhase.Descent)]
    [InlineData(FlightPhase.Approach)]
    public void AirWindow_IsDescentAndApproach(FlightPhase phase)
    {
        Assert.Equal(CabinAutoAnswerGate.Air, CabinAutoAnswer.GateFor(phase, Options(air: true)));
        Assert.Equal(CabinAutoAnswerGate.None, CabinAutoAnswer.GateFor(phase, Options(ground: true)));
    }

    [Theory]
    [InlineData(FlightPhase.Unknown)]
    [InlineData(FlightPhase.ColdAndDark)]
    [InlineData(FlightPhase.Preflight)]
    [InlineData(FlightPhase.Departure)]
    [InlineData(FlightPhase.TakeoffRoll)]
    [InlineData(FlightPhase.InitialClimb)]
    [InlineData(FlightPhase.Climb)]
    [InlineData(FlightPhase.Cruise)]
    [InlineData(FlightPhase.LandingRollout)]
    [InlineData(FlightPhase.TaxiIn)]
    [InlineData(FlightPhase.Shutdown)]
    public void OtherPhases_NeverAnswered_EvenWithBothOptionsOn(FlightPhase phase)
        => Assert.Equal(CabinAutoAnswerGate.None, CabinAutoAnswer.GateFor(phase, Options(ground: true, air: true)));

    [Fact]
    public void Plan_CarriesTheWindowsDelay_AndClampsNegative()
    {
        var options = Options(ground: true, air: true);
        options.AutoAnswerGroundDelayMs = 4000;
        options.AutoAnswerAirDelayMs = -5;

        var ground = CabinAutoAnswer.Plan(FlightPhase.TaxiOut, options, humanIsRightSeat: false)!;
        var air = CabinAutoAnswer.Plan(FlightPhase.Approach, options, humanIsRightSeat: false)!;

        Assert.Equal(4000, ground.DelayMs);
        Assert.Equal(0, air.DelayMs);
    }

    [Fact]
    public void Panel_IsSeatRelative_FoPanelByDefault_CaptainPanelWhenHumanSitsRight()
    {
        Assert.Equal("system.switches.S_ASP2_CAB_REC_LATCH", CabinAutoAnswer.LatchFor(humanIsRightSeat: false));
        Assert.Equal("system.switches.S_ASP_CAB_REC_LATCH", CabinAutoAnswer.LatchFor(humanIsRightSeat: true));
    }

    [Fact]
    public void Step_CabSelected_AlwaysProceeds_EvenBeforeTheAnswerIsDue()
        => Assert.Equal(CabinCallWaitStep.Proceed, CabinAutoAnswer.Step(cabSelected: true, now: 0, waitDeadline: 25_000, answerDueAt: 4_000, answered: false));

    [Fact]
    public void Step_AnswerFiresOnceWhenDue_ThenKeepsWaitingForTheEcho()
    {
        Assert.Equal(CabinCallWaitStep.Keep, CabinAutoAnswer.Step(false, now: 3_999, waitDeadline: 25_000, answerDueAt: 4_000, answered: false));
        Assert.Equal(CabinCallWaitStep.Answer, CabinAutoAnswer.Step(false, now: 4_000, waitDeadline: 25_000, answerDueAt: 4_000, answered: false));
        Assert.Equal(CabinCallWaitStep.Keep, CabinAutoAnswer.Step(false, now: 4_250, waitDeadline: 25_000, answerDueAt: 4_000, answered: true));
    }

    [Fact]
    public void Step_NoPlan_ProceedsAtTheGraceDeadline()
    {
        Assert.Equal(CabinCallWaitStep.Keep, CabinAutoAnswer.Step(false, now: 24_999, waitDeadline: 25_000, answerDueAt: null, answered: false));
        Assert.Equal(CabinCallWaitStep.Proceed, CabinAutoAnswer.Step(false, now: 25_000, waitDeadline: 25_000, answerDueAt: null, answered: false));
    }

    [Fact]
    public void Step_AnswerDueAfterTheDeadline_StillFiresFirst()
        // The shell extends the deadline past the answer + echo window, so this only guards
        // the ordering rule: a due answer beats the deadline check.
        => Assert.Equal(CabinCallWaitStep.Answer, CabinAutoAnswer.Step(false, now: 30_000, waitDeadline: 25_000, answerDueAt: 30_000, answered: false));
}
