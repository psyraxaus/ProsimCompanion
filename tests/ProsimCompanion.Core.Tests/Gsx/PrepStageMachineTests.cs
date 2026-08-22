using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Sync;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>
/// Table tests for the ground-prep decision function (campaign #78). Hold-gate order is the
/// contract: session → resync (#30, the 150 ms reposition race) → voice (ADR-0006, checked
/// after resync so SeedComplete still fast-forwards) → phase window → readiness/gate.
/// </summary>
public sealed class PrepStageMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    private static PrepStageMachine.PrepInputs Open(GsxPrepStage stage = GsxPrepStage.Reposition)
        => new(
            AutomationEnabled: true,
            SessionPhase: SimSessionPhase.InSession,
            ResyncAssessed: true,
            VoiceActivationMode: false,
            CycleStarted: false,
            FlightPhase: FlightPhase.Preflight,
            GsxReady: true,
            GateKey: "gate|A1",
            SessionGateKey: null,
            Stage: stage,
            SettleUntil: Now - TimeSpan.FromSeconds(1));

    [Fact]
    public void EverythingOpen_RunsTheCurrentStage()
    {
        var decision = PrepStageMachine.Next(Open(), Now);

        Assert.Equal(PrepCommand.RunStage, decision.Command);
        Assert.True(decision.ReleasesHold);
    }

    [Fact]
    public void Disabled_IsSilent()
    {
        var decision = PrepStageMachine.Next(Open() with { AutomationEnabled = false }, Now);

        Assert.Equal(PrepCommand.None, decision.Command);
        Assert.False(decision.ReleasesHold);
    }

    [Theory]
    [InlineData(SimSessionPhase.NotInSession)]
    [InlineData(SimSessionPhase.Unknown)]
    [InlineData(SimSessionPhase.Walkaround)]
    public void OutsideTheSession_Holds_WalkaroundIncluded(SimSessionPhase phase)
    {
        // MayDriveGroundServices excludes the walkaround (campaign #79 semantics, already the
        // rule here): services must not be driven while the pilot is outside the aircraft.
        var decision = PrepStageMachine.Next(Open() with { SessionPhase = phase }, Now);

        Assert.Equal(PrepCommand.Hold, decision.Command);
        Assert.Contains("MSFS session not active", decision.Reason);
    }

    [Fact]
    public void ResyncGate_HoldsBeforeTheVoiceGate()
    {
        var decision = PrepStageMachine.Next(
            Open() with { ResyncAssessed = false, VoiceActivationMode = true },
            Now);

        Assert.Equal(PrepCommand.Hold, decision.Command);
        Assert.Contains("startup resync", decision.Reason);
    }

    [Fact]
    public void VoiceMode_HoldsUntilTheCycleStarts_ButNeverHoldsACompleteChain()
    {
        var holding = PrepStageMachine.Next(Open() with { VoiceActivationMode = true }, Now);
        Assert.Equal(PrepCommand.Hold, holding.Command);
        Assert.Contains("commence ground services", holding.Reason);

        var released = PrepStageMachine.Next(
            Open() with { VoiceActivationMode = true, CycleStarted = true },
            Now);
        Assert.Equal(PrepCommand.RunStage, released.Command);

        // A chain the resync seeded Complete must not re-hold behind the voice gate.
        var seeded = PrepStageMachine.Next(
            Open(GsxPrepStage.Complete) with { VoiceActivationMode = true },
            Now);
        Assert.Equal(PrepCommand.None, seeded.Command);
        Assert.True(seeded.ReleasesHold);
    }

    [Theory]
    [InlineData(FlightPhase.TaxiIn)]
    [InlineData(FlightPhase.Shutdown)]
    [InlineData(FlightPhase.Cruise)]
    [InlineData(FlightPhase.Climb)]
    public void ProgressedChain_ResetsWhenTheFlightMovesOn(FlightPhase phase)
    {
        var decision = PrepStageMachine.Next(
            Open(GsxPrepStage.Complete) with { FlightPhase = phase },
            Now);

        Assert.Equal(PrepCommand.Reset, decision.Command);
        Assert.Contains(phase.ToString(), decision.Reason);
    }

    [Fact]
    public void UnprogressedChain_OffPhase_JustWaits()
    {
        var decision = PrepStageMachine.Next(
            Open() with { FlightPhase = FlightPhase.TakeoffRoll },
            Now);

        Assert.Equal(PrepCommand.None, decision.Command);
    }

    [Fact]
    public void GateChange_ResetsOnlyLateStages()
    {
        var lateStage = PrepStageMachine.Next(
            Open(GsxPrepStage.JetwayStairs) with { SessionGateKey = "gate|B2" },
            Now);
        Assert.Equal(PrepCommand.Reset, lateStage.Command);
        Assert.Contains("gate changed", lateStage.Reason);

        // Early stages tolerate a shifting key — the reposition itself refreshes it.
        var earlyStage = PrepStageMachine.Next(
            Open(GsxPrepStage.AnchorGate) with { SessionGateKey = "gate|B2" },
            Now);
        Assert.Equal(PrepCommand.RunStage, earlyStage.Command);
    }

    [Fact]
    public void Settling_WaitsOutTheWindow_ThenAdvances()
    {
        var waiting = PrepStageMachine.Next(
            Open(GsxPrepStage.Settling) with { SettleUntil = Now + TimeSpan.FromSeconds(5) },
            Now);
        Assert.Equal(PrepCommand.None, waiting.Command);

        var elapsed = PrepStageMachine.Next(Open(GsxPrepStage.Settling), Now);
        Assert.Equal(PrepCommand.AdvanceFromSettling, elapsed.Command);
    }

    [Fact]
    public void NotReady_IsSilent_ButUnknownGate_HoldsVisibly()
    {
        // GSX absent: nothing to say (degrade quietly).
        Assert.Equal(
            PrepCommand.None,
            PrepStageMachine.Next(Open() with { GsxReady = false }, Now).Command);

        // GSX up but no session gate (issue #44, EGLL Stand 547): the old silent branch cost
        // the pilot four app restarts — the hold must now be visible with actionable wording.
        var unknownGate = PrepStageMachine.Next(Open() with { GateKey = null }, Now);
        Assert.Equal(PrepCommand.Hold, unknownGate.Command);
        Assert.Contains("GSX has not identified the parking", unknownGate.Reason);
    }
}
