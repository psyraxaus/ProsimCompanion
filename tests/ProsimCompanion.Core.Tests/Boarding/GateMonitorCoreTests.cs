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
        bool gsx = true,
        bool? cabinEmpty = null)
        => new(stage, target, boarded, door, bridge, phase, std ?? Std, now ?? T0, gsx, null, cabinEmpty);

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

    // ---- issue #173: the turnaround leg (EGCC 2026-10-11) ----

    [Fact]
    public void CycleResetInShutdown_StaysClosed_UntilTheNextLegsBoardingIsCalled()
    {
        // The arrival reset lands in Shutdown; the old past-gate rule re-closed the strip on
        // the very next tick and the stale 122/122 counter then blocked the Preflight reset.
        var core = new GateMonitorCore();
        Eval(core, Inputs(stage: GsxServiceStage.Completed, target: 122, boarded: 122, door: false));
        Eval(core, Inputs(stage: GsxServiceStage.Waiting, target: 122, boarded: 122, phase: FlightPhase.Cruise));
        Eval(core, Inputs(stage: GsxServiceStage.Waiting, target: 122, boarded: 122, phase: FlightPhase.Shutdown));
        Assert.Equal(GateState.ClosedAfterBoarding, core.State);

        core.Reset(T0.AddHours(3));
        var afterReset = Eval(core, Inputs(stage: GsxServiceStage.Waiting, target: 122, boarded: 122, phase: FlightPhase.Shutdown, now: T0.AddHours(3)));
        Assert.Equal(GateState.Closed, afterReset.State);
        Assert.False(afterReset.Dimmed);

        // Preflight with last leg's counter still showing: still "Not called".
        var preflight = Eval(core, Inputs(stage: GsxServiceStage.Waiting, target: 122, boarded: 122, phase: FlightPhase.Preflight, now: T0.AddHours(3.2)));
        Assert.Equal(GateState.Closed, preflight.State);
    }

    [Fact]
    public void SecondBoardingInOneSession_ProducesANewChain()
    {
        var core = new GateMonitorCore();
        // Leg 1.
        Eval(core, Inputs(stage: GsxServiceStage.Called, target: 122, boarded: 0));
        Eval(core, Inputs(stage: GsxServiceStage.Active, target: 122, boarded: 60));
        Eval(core, Inputs(stage: GsxServiceStage.Completed, target: 122, boarded: 122, door: false));
        Eval(core, Inputs(stage: GsxServiceStage.Waiting, target: 122, boarded: 122, phase: FlightPhase.Cruise));
        Eval(core, Inputs(stage: GsxServiceStage.Waiting, target: 122, boarded: 122, phase: FlightPhase.Shutdown));
        Assert.Equal(GateState.ClosedAfterBoarding, core.State);

        // Arrival reset, then leg 2 boards.
        core.Reset(T0.AddHours(3));
        var chain = new List<GateState>();
        chain.Add(Eval(core, Inputs(stage: GsxServiceStage.Waiting, target: 122, boarded: 122, phase: FlightPhase.Shutdown, now: T0.AddHours(3))).State);
        chain.Add(Eval(core, Inputs(stage: GsxServiceStage.Waiting, target: 122, boarded: 122, phase: FlightPhase.Preflight, now: T0.AddHours(3.2))).State);
        chain.Add(Eval(core, Inputs(stage: GsxServiceStage.Called, target: 130, boarded: 0, phase: FlightPhase.Preflight, now: T0.AddHours(3.3), std: T0.AddHours(4.5))).State);
        chain.Add(Eval(core, Inputs(stage: GsxServiceStage.Active, target: 130, boarded: 40, phase: FlightPhase.Departure, now: T0.AddHours(3.4), std: T0.AddHours(4.5))).State);
        chain.Add(Eval(core, Inputs(stage: GsxServiceStage.Completed, target: 130, boarded: 130, door: false, phase: FlightPhase.Departure, now: T0.AddHours(3.6), std: T0.AddHours(4.5))).State);

        Assert.Equal(
            [GateState.Closed, GateState.Closed, GateState.Open, GateState.Boarding, GateState.ClosedAfterBoarding],
            chain);
    }

    [Fact]
    public void StaleCounterWithoutABoardingStage_IsNotPaxEvidence()
    {
        // App restart mid-turnaround: GSX still reports last leg's 122/122, stage Waiting.
        var s = Eval(new GateMonitorCore(), Inputs(stage: GsxServiceStage.Waiting, target: 122, boarded: 122));

        Assert.Equal(GateState.Closed, s.State);
        Assert.Equal("Not called", s.Detail);
    }

    [Fact]
    public void NewOfpRequestId_ResetsAClosedLatch_FromThePreviousPlan()
    {
        // Restart in flight: the strip restores ClosedAfterBoarding from leg 1's plan; leg 2's
        // OFP arrives during the turnaround and must start its own chain.
        var core = new GateMonitorCore();
        Eval(core, Inputs(stage: GsxServiceStage.Completed, target: 122, boarded: 122, phase: FlightPhase.Cruise) with { OfpRequestId = "189301241" });
        Eval(core, Inputs(stage: GsxServiceStage.Completed, target: 122, boarded: 122, phase: FlightPhase.Shutdown) with { OfpRequestId = "189301241" });
        Assert.Equal(GateState.ClosedAfterBoarding, core.State);

        // Leg 2's plan arrives; GSX's arrival reset has put Boarding back to Waiting, the
        // counter still shows last leg's 122/122.
        var s = Eval(core, Inputs(stage: GsxServiceStage.Waiting, target: 122, boarded: 122, phase: FlightPhase.Shutdown, now: T0.AddHours(3)) with { OfpRequestId = "189328867" });

        Assert.Equal(GateState.Closed, s.State);

        // The same id afterwards keeps the forward-only latch within the leg.
        Eval(core, Inputs(stage: GsxServiceStage.Active, target: 130, boarded: 20, phase: FlightPhase.Departure, now: T0.AddHours(3.5), std: T0.AddHours(4.5)) with { OfpRequestId = "189328867" });
        Assert.Equal(GateState.Boarding, core.State);
        Eval(core, Inputs(stage: GsxServiceStage.Active, target: 130, boarded: 20, phase: FlightPhase.Departure, now: T0.AddHours(3.6), std: T0.AddHours(4.5)) with { OfpRequestId = "189328867" });
        Assert.Equal(GateState.Boarding, core.State);
    }

    [Fact]
    public void NoOfpId_NeverResets()
    {
        var core = new GateMonitorCore();
        Eval(core, Inputs(stage: GsxServiceStage.Active, target: 130, boarded: 20) with { OfpRequestId = "1" });
        Eval(core, Inputs(stage: GsxServiceStage.Active, target: 130, boarded: 25) with { OfpRequestId = null });

        Assert.Equal(GateState.Boarding, core.State);
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

    [Theory]
    [InlineData("Terminal 3 | Gate  311", "Gate 311")]
    [InlineData("Gate  311", "Gate 311")]
    [InlineData("Stand 547", "Stand 547")]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void StandLabel_IsTheStandPartOfGsxsKey(string? key, string? expected)
        => Assert.Equal(expected, GateMonitorService.StandLabel(key));

    [Fact]
    public void GateId_FallsBackToTheOccupiedStand_WhenNoRequestIsConfirmed()
    {
        // Owner report 2026-10-03: the departure anchor's gate.select fails by design, so the
        // board read "GATE —" although GSX knew the stand.
        var unconfirmed = ProsimCompanion.Core.State.GsxDiagnosticsSnapshot.Empty with
        {
            GateContextKey = "Terminal 3 | Gate  311",
            GateRequest = "Gate  311: Failed — not_found",
        };
        var confirmed = unconfirmed with { GateRequest = "B12: Confirmed — confirmed as B12" };
        var nothing = ProsimCompanion.Core.State.GsxDiagnosticsSnapshot.Empty;

        Assert.Equal("Gate 311", GateMonitorService.GateIdFrom(unconfirmed));
        Assert.Equal("B12", GateMonitorService.GateIdFrom(confirmed));
        Assert.Null(GateMonitorService.GateIdFrom(nothing));
    }

    // EHAM 2026-11-01 14:41Z: boarding requested for leg two with GSX's counter still showing
    // leg one's 119 aboard (target 129) — FINAL CALL the instant boarding was called.
    [Fact]
    public void StaleCountFromThePreviousLeg_NeverTriggersTheFinalCall()
    {
        var core = new GateMonitorCore();
        Eval(core, Inputs());
        Assert.Equal(GateState.Boarding, Eval(core, Inputs(stage: GsxServiceStage.Requested, target: 129, boarded: 119, door: true)).State);
        // Still the old figure once GSX starts: not trusted until the counter moves.
        Assert.Equal(GateState.Boarding, Eval(core, Inputs(stage: GsxServiceStage.Active, target: 129, boarded: 119, door: true)).State);
        // GSX restarts its count: now the share is this leg's.
        Assert.Equal(GateState.Boarding, Eval(core, Inputs(stage: GsxServiceStage.Active, target: 129, boarded: 5, door: true)).State);
        Assert.Equal(GateState.FinalCall, Eval(core, Inputs(stage: GsxServiceStage.Active, target: 129, boarded: 117, door: true)).State);
    }

    // EHAM 2026-11-01: the turnaround reset landed during the taxi in and the strip re-closed
    // itself at once with leg one's figures. From CLOSED, the arrival's ground phases are not
    // "closed after boarding"; the next leg's boarding opens it as usual.
    [Fact]
    public void AfterTheArrivalReset_TheStripStaysClosed_UntilTheNextBoarding()
    {
        var core = new GateMonitorCore();
        Eval(core, Inputs(stage: GsxServiceStage.Completed, target: 119, boarded: 119, door: false, phase: FlightPhase.Cruise));
        Assert.Equal(GateState.ClosedAfterBoarding, core.State);

        core.Reset(T0.AddHours(2));
        Assert.Equal(GateState.Closed, Eval(core, Inputs(stage: GsxServiceStage.Waiting, target: 119, boarded: 119, door: true, phase: FlightPhase.TaxiIn, now: T0.AddHours(2))).State);
        Assert.Equal(GateState.Closed, Eval(core, Inputs(stage: GsxServiceStage.Waiting, target: 119, boarded: 119, door: true, phase: FlightPhase.Shutdown, now: T0.AddHours(2))).State);
        Assert.Equal("Not called", Eval(core, Inputs(stage: GsxServiceStage.Waiting, target: 119, boarded: 119, door: true, phase: FlightPhase.Preflight, now: T0.AddHours(2))).Detail);

        // A strip that was OPEN when we taxied out still closes behind us.
        var departing = new GateMonitorCore();
        Eval(departing, Inputs(stage: GsxServiceStage.Called, target: 129, boarded: 0, door: true));
        Assert.Equal(GateState.ClosedAfterBoarding, Eval(departing, Inputs(stage: GsxServiceStage.Waiting, target: 129, boarded: 129, door: false, phase: FlightPhase.TaxiOut)).State);
        // And an app started in the cruise still reads closed-after-boarding.
        Assert.Equal(GateState.ClosedAfterBoarding, Eval(new GateMonitorCore(), Inputs(stage: null, phase: FlightPhase.Cruise)).State);
    }

    // ---- issue #213: the restart hole #173 left (EHAM 2026-11-01) ----

    [Fact]
    public void RestoredClosedAfterBoarding_OverAnEmptyCabin_ResetsOnTheFirstOfpAfterARestart()
    {
        // The app restarted at the gate after the arrival: GSX still showed leg one's 119/119
        // with Boarding "completed", the cabin was empty, and the first OFP after the restart
        // (leg two's) was adopted with the stale close in place until the next restart.
        var core = new GateMonitorCore();
        // The seat string arrives a moment after GSX's first read: the close latches first …
        Eval(core, Inputs(stage: GsxServiceStage.Completed, target: 119, boarded: 119, door: false, cabinEmpty: null) with { OfpRequestId = null });
        Assert.Equal(GateState.ClosedAfterBoarding, core.State);

        // … and is undone the moment the empty cabin is known, OFP or no OFP.
        var s = Eval(core, Inputs(stage: GsxServiceStage.Completed, target: 119, boarded: 119, door: false, cabinEmpty: true, now: T0.AddMinutes(1)) with { OfpRequestId = "191949682" });
        Assert.Equal(GateState.Closed, s.State);
        Assert.Equal("Not called", s.Detail);

        // The stale reading never re-closes it on later ticks either.
        var later = Eval(core, Inputs(stage: GsxServiceStage.Completed, target: 119, boarded: 119, door: false, cabinEmpty: true, now: T0.AddMinutes(2)) with { OfpRequestId = "191949682" });
        Assert.Equal(GateState.Closed, later.State);
    }

    [Fact]
    public void AStaleCompletedBoarding_OverAnEmptyCabin_NeverLatchesInTheFirstPlace()
    {
        // Seat string known from the first read: nothing to undo.
        var core = new GateMonitorCore();
        var s = Eval(core, Inputs(stage: GsxServiceStage.Completed, target: 119, boarded: 119, door: false, cabinEmpty: true) with { OfpRequestId = null });
        Assert.Equal(GateState.Closed, s.State);
    }

    [Fact]
    public void ClosedAfterBoarding_OverAFullCabin_Stays()
    {
        // Restart after boarding, before pushback (9cbd82b): the cabin is full, the close is real.
        var core = new GateMonitorCore();
        Eval(core, Inputs(stage: GsxServiceStage.Completed, target: 119, boarded: 119, door: false, cabinEmpty: false) with { OfpRequestId = "191949682" });
        var s = Eval(core, Inputs(stage: GsxServiceStage.Completed, target: 119, boarded: 119, door: false, cabinEmpty: false, now: T0.AddMinutes(1)) with { OfpRequestId = "191949682" });
        Assert.Equal(GateState.ClosedAfterBoarding, s.State);

        // Unknown occupancy (ProSim absent) never moves the state on its own either.
        var unknown = Eval(core, Inputs(stage: GsxServiceStage.Completed, target: 119, boarded: 119, door: false, cabinEmpty: null, now: T0.AddMinutes(2)) with { OfpRequestId = "191949682" });
        Assert.Equal(GateState.ClosedAfterBoarding, unknown.State);
    }
}
