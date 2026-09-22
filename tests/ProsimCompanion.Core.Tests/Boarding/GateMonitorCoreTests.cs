using ProsimCompanion.Core.Boarding;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using Xunit;

namespace ProsimCompanion.Core.Tests.Boarding;

public sealed class GateMonitorCoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 22, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Std = T0.AddMinutes(45);

    private static GateMonitorInputs Inputs(
        GsxServiceStage? stage = GsxServiceStage.Waiting,
        int? target = null,
        int? boarded = null,
        bool? door = false,
        bool bridge = false,
        FlightPhase phase = FlightPhase.Preflight,
        DateTimeOffset? now = null,
        DateTimeOffset? std = null,
        bool gsx = true)
        => new(stage, target, boarded, door, bridge, phase, std ?? Std, now ?? T0, gsx);

    private static GateStatusSnapshot Eval(GateMonitorCore core, GateMonitorInputs inputs, int pct = 90, int minutes = 10)
        => core.Evaluate(inputs, "B12", pct, minutes);

    [Fact]
    public void FreshCycle_IsClosedNotCalled()
    {
        var s = Eval(new GateMonitorCore(), Inputs());

        Assert.Equal(GateState.Closed, s.State);
        Assert.Equal("Not called", s.Detail);
        Assert.Equal("B12", s.GateId);
        Assert.Equal(45, s.MinutesToStd);
        Assert.False(s.Dimmed);
        Assert.Equal(0, s.Progress);
    }

    [Fact]
    public void GsxOffline_SaysSo()
        => Assert.Equal("GSX offline", Eval(new GateMonitorCore(), Inputs(stage: null, gsx: false)).Detail);

    [Fact]
    public void BoardingCalled_OpensTheGate()
    {
        var core = new GateMonitorCore();
        Eval(core, Inputs());

        var s = Eval(core, Inputs(stage: GsxServiceStage.Called, target: 156, boarded: 0));

        Assert.Equal(GateState.Open, s.State);
        Assert.Equal("Boarding called", s.Detail);
    }

    [Fact]
    public void JetwayOnAndDoorOpen_OpensTheGate_WithoutAGsxCall()
    {
        var core = new GateMonitorCore();

        var s = Eval(core, Inputs(bridge: true, door: true));

        Assert.Equal(GateState.Open, s.State);
        Assert.Equal("Jetway · door 1L", s.Detail);
    }

    [Fact]
    public void PaxFlowing_SkipsStraightToBoarding()
    {
        var core = new GateMonitorCore();

        var s = Eval(core, Inputs(stage: GsxServiceStage.Active, target: 156, boarded: 12, door: true));

        Assert.Equal(GateState.Boarding, s.State);
        Assert.Equal(12, s.PaxBoarded);
        Assert.Equal(156, s.PaxTarget);
        Assert.InRange(s.Progress, 0.07, 0.08);
    }

    [Fact]
    public void FinalCall_OnPaxShare()
    {
        var core = new GateMonitorCore();
        Eval(core, Inputs(stage: GsxServiceStage.Active, target: 156, boarded: 100, door: true));

        Assert.Equal(GateState.Boarding, Eval(core, Inputs(stage: GsxServiceStage.Active, target: 156, boarded: 139, door: true)).State);
        Assert.Equal(GateState.FinalCall, Eval(core, Inputs(stage: GsxServiceStage.Active, target: 156, boarded: 141, door: true)).State);
    }

    [Fact]
    public void FinalCall_OnStdCountdown()
    {
        var core = new GateMonitorCore();
        Eval(core, Inputs(stage: GsxServiceStage.Active, target: 156, boarded: 40, door: true));

        var s = Eval(core, Inputs(stage: GsxServiceStage.Active, target: 156, boarded: 41, door: true, now: Std.AddMinutes(-9)));

        Assert.Equal(GateState.FinalCall, s.State);
        Assert.Equal(9, s.MinutesToStd);
    }

    [Fact]
    public void FinalCall_Thresholds_CanBeDisabled()
    {
        var core = new GateMonitorCore();
        Eval(core, Inputs(stage: GsxServiceStage.Active, target: 156, boarded: 40, door: true));

        var s = Eval(core, Inputs(stage: GsxServiceStage.Active, target: 156, boarded: 155, door: true, now: Std.AddMinutes(-2)), pct: 100, minutes: 0);

        Assert.Equal(GateState.Boarding, s.State);
    }

    [Fact]
    public void BoardingCompleted_ClosesTheGate_AndHolds()
    {
        var core = new GateMonitorCore();
        Eval(core, Inputs(stage: GsxServiceStage.Active, target: 156, boarded: 150, door: true));
        var closing = T0.AddMinutes(30);

        var s = Eval(core, Inputs(stage: GsxServiceStage.Completed, target: 156, boarded: 156, door: false, now: closing));
        Assert.Equal(GateState.ClosedAfterBoarding, s.State);
        Assert.Equal("Doors closed 10:30Z", s.Detail);
        Assert.Equal(1, s.Progress);
        Assert.Equal(closing, s.ChangedAtUtc);

        // GSX flips the finished service back to "available" — the latch must not care.
        var later = Eval(core, Inputs(stage: GsxServiceStage.Waiting, target: 156, boarded: 156, door: false, now: closing.AddMinutes(1)));
        Assert.Equal(GateState.ClosedAfterBoarding, later.State);
    }

    [Fact]
    public void DoorClosedWithEverybodyOnBoard_ClosesBeforeGsxSaysCompleted()
    {
        var core = new GateMonitorCore();
        Eval(core, Inputs(stage: GsxServiceStage.Active, target: 156, boarded: 156, door: true));

        Assert.Equal(GateState.ClosedAfterBoarding,
            Eval(core, Inputs(stage: GsxServiceStage.Active, target: 156, boarded: 156, door: false)).State);
    }

    [Fact]
    public void DoorSwingMidBoarding_IsIgnored()
    {
        var core = new GateMonitorCore();
        Eval(core, Inputs(stage: GsxServiceStage.Active, target: 156, boarded: 60, door: true));

        Assert.Equal(GateState.Boarding,
            Eval(core, Inputs(stage: GsxServiceStage.Active, target: 156, boarded: 60, door: false)).State);
    }

    [Fact]
    public void NeverMovesBackwards()
    {
        var core = new GateMonitorCore();
        Eval(core, Inputs(stage: GsxServiceStage.Active, target: 156, boarded: 145, door: true));
        Assert.Equal(GateState.FinalCall, core.State);

        // A counter glitch back to zero and a "called" stage must not reopen the gate.
        Assert.Equal(GateState.FinalCall, Eval(core, Inputs(stage: GsxServiceStage.Called, target: 156, boarded: 0, door: true)).State);
    }

    [Fact]
    public void PushbackWithoutGsxEvidence_ClosesAndDims()
    {
        var core = new GateMonitorCore();
        Eval(core, Inputs(stage: GsxServiceStage.Active, target: 156, boarded: 80, door: true));

        var s = Eval(core, Inputs(stage: GsxServiceStage.Active, target: 156, boarded: 80, door: false, phase: FlightPhase.TaxiOut));

        Assert.Equal(GateState.ClosedAfterBoarding, s.State);
        Assert.True(s.Dimmed);
    }

    [Fact]
    public void AppStartedInFlight_ShowsClosedDimmed()
    {
        var s = Eval(new GateMonitorCore(), Inputs(stage: null, phase: FlightPhase.Cruise));

        Assert.Equal(GateState.ClosedAfterBoarding, s.State);
        Assert.True(s.Dimmed);
    }

    [Fact]
    public void NextTurnaround_ResetsAutomatically()
    {
        var core = new GateMonitorCore();
        Eval(core, Inputs(stage: GsxServiceStage.Completed, target: 156, boarded: 156, door: false));
        Eval(core, Inputs(stage: GsxServiceStage.Waiting, target: 156, boarded: 156, phase: FlightPhase.Cruise));
        Eval(core, Inputs(stage: GsxServiceStage.Waiting, target: 156, boarded: 156, phase: FlightPhase.Shutdown));

        // New leg: back at the gate, no boarding evidence yet.
        var s = Eval(core, Inputs(stage: GsxServiceStage.Waiting, target: null, boarded: null, phase: FlightPhase.Preflight, now: T0.AddHours(3)));

        Assert.Equal(GateState.Closed, s.State);
        Assert.False(s.Dimmed);
    }

    [Fact]
    public void ExplicitReset_ClearsTheLatch()
    {
        var core = new GateMonitorCore();
        Eval(core, Inputs(stage: GsxServiceStage.Completed, target: 156, boarded: 156));

        core.Reset(T0.AddHours(2));

        Assert.Equal(GateState.Closed, core.State);
        Assert.Equal(GateState.Closed, Eval(core, Inputs()).State);
    }

    [Fact]
    public void UnchangedInputs_ReturnEqualSnapshots()
    {
        // The store relies on record equality to stay quiet between ticks.
        var core = new GateMonitorCore();
        var a = Eval(core, Inputs(stage: GsxServiceStage.Active, target: 156, boarded: 50, door: true));
        var b = Eval(core, Inputs(stage: GsxServiceStage.Active, target: 156, boarded: 50, door: true));

        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData("B12: Confirmed — confirmed as B12", "B12")]
    [InlineData("A3: Pending — waiting for the menu", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void GateId_OnlyFromAConfirmedRequest(string? request, string? expected)
        => Assert.Equal(expected, GateMonitorService.GateIdFrom(request));
}
