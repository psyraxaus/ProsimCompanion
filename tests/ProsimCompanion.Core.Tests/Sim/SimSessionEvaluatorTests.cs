using ProsimCompanion.Core.State;
using ProsimCompanion.Sim;
using Xunit;

namespace ProsimCompanion.Core.Tests.Sim;

public sealed class SimSessionEvaluatorTests
{
    private const int CockpitCamera = 2;
    private const int LoadingCamera = 11;
    private const int WorldMapCamera = 12;
    private const int WalkaroundCamera = 30;

    private static SimSessionInputs Inputs(
        bool connected = true,
        bool simRunning = true,
        bool paused = false,
        int? camera = CockpitCamera,
        bool? isAvatar = null)
        => new(connected, simRunning, paused, camera, isAvatar);

    /// <summary>Runs enough ready ticks to clear the entry debounce.</summary>
    private static SimSessionPhase EnterSession(SimSessionEvaluator evaluator, SimSessionInputs inputs)
    {
        var phase = SimSessionPhase.Unknown;
        for (var i = 0; i < SimSessionEvaluator.EntryDebounceTicks; i++)
        {
            phase = evaluator.ProcessTick(inputs);
        }
        return phase;
    }

    [Fact]
    public void Disconnected_IsUnknown()
        => Assert.Equal(SimSessionPhase.Unknown, new SimSessionEvaluator().ProcessTick(Inputs(connected: false)));

    [Fact]
    public void ConnectedWithoutCameraData_IsUnknown()
        => Assert.Equal(SimSessionPhase.Unknown, new SimSessionEvaluator().ProcessTick(Inputs(camera: null)));

    [Theory]
    [InlineData(0)]                 // unset
    [InlineData(LoadingCamera)]     // load/wait screen
    [InlineData(WorldMapCamera)]    // world map
    [InlineData(15)]                // menu RTC
    [InlineData(32)]
    [InlineData(35)]
    public void NonSessionCameras_AreNotInSession(int camera)
    {
        var evaluator = new SimSessionEvaluator();
        Assert.Equal(SimSessionPhase.NotInSession, EnterSession(evaluator, Inputs(camera: camera)));
    }

    [Fact]
    public void ReadyToFlyHold_PausedOnValidCamera_StaysOut()
    {
        // MSFS parks the loaded flight paused on a cockpit camera until "Ready to Fly" —
        // the FlowPro lesson from the predecessor: this must NOT count as in-session.
        var evaluator = new SimSessionEvaluator();
        Assert.Equal(SimSessionPhase.NotInSession, EnterSession(evaluator, Inputs(paused: true)));
    }

    [Fact]
    public void Entry_RequiresConsecutiveReadyTicks()
    {
        var evaluator = new SimSessionEvaluator();
        // Until 2026-11-15 the first ready tick read NotInSession. From Unknown it now stays
        // Unknown: NotInSession means the sim was seen outside a flight (issue #246).
        Assert.Equal(SimSessionPhase.Unknown, evaluator.ProcessTick(Inputs()));
        Assert.Equal(SimSessionPhase.InSession, evaluator.ProcessTick(Inputs()));
    }

    [Fact]
    public void Entry_DebounceResetsOnCameraFlicker()
    {
        // The camera flickers through valid values while a flight loads; a single valid tick
        // followed by an invalid one must not accumulate toward entry.
        var evaluator = new SimSessionEvaluator();
        evaluator.ProcessTick(Inputs());
        evaluator.ProcessTick(Inputs(camera: LoadingCamera));
        Assert.Equal(SimSessionPhase.NotInSession, evaluator.ProcessTick(Inputs()));
        Assert.Equal(SimSessionPhase.InSession, evaluator.ProcessTick(Inputs()));
    }

    [Fact]
    public void PauseMidSession_DoesNotEndTheSession()
    {
        var evaluator = new SimSessionEvaluator();
        EnterSession(evaluator, Inputs());
        Assert.Equal(SimSessionPhase.InSession, evaluator.ProcessTick(Inputs(paused: true)));
    }

    [Fact]
    public void CameraToMenu_EndsTheSessionImmediately()
    {
        var evaluator = new SimSessionEvaluator();
        EnterSession(evaluator, Inputs());
        Assert.Equal(SimSessionPhase.NotInSession, evaluator.ProcessTick(Inputs(camera: WorldMapCamera)));
    }

    [Fact]
    public void SimStateStop_EndsTheSession()
    {
        var evaluator = new SimSessionEvaluator();
        EnterSession(evaluator, Inputs());
        Assert.Equal(SimSessionPhase.NotInSession, evaluator.ProcessTick(Inputs(simRunning: false)));
    }

    // ---- issue #246: a lost SimConnect link is not the end of the flight ---------------------

    [Fact]
    public void LinkLostAtTheStand_AsOnTheFlightOf20261115_KeepsTheSession()
    {
        // EGLL 2026-11-15 08:53:42 local: the link dropped at engine shutdown with MSFS
        // running and was back 5 s later. The session read InSession -> Unknown ->
        // NotInSession -> InSession, and the NotInSession tick reset Shutdown to Preflight.
        var evaluator = new SimSessionEvaluator();
        EnterSession(evaluator, Inputs());

        var seen = new List<SimSessionPhase>();
        for (var tick = 0; tick < 10; tick++)                       // 5 s without the link
        {
            seen.Add(evaluator.ProcessTick(Inputs(connected: false, simRunning: false, paused: true, camera: null)));
            Assert.True(evaluator.HoldingAcrossLinkLoss);
        }

        seen.Add(evaluator.ProcessTick(Inputs(camera: null)));       // connected, camera not yet delivered
        seen.Add(evaluator.ProcessTick(Inputs()));                   // camera 2, sim running
        seen.Add(evaluator.ProcessTick(Inputs()));

        Assert.All(seen, phase => Assert.Equal(SimSessionPhase.InSession, phase));
        Assert.False(evaluator.HoldingAcrossLinkLoss);
    }

    [Fact]
    public void LinkLost_OnTheWalkaround_KeepsTheWalkaround()
    {
        var evaluator = new SimSessionEvaluator();
        EnterSession(evaluator, Inputs(camera: WalkaroundCamera, isAvatar: true));

        Assert.Equal(SimSessionPhase.Walkaround, evaluator.ProcessTick(Inputs(connected: false, camera: null)));
        Assert.Equal(SimSessionPhase.Walkaround, evaluator.ProcessTick(Inputs(camera: WalkaroundCamera, isAvatar: true)));
    }

    [Fact]
    public void LinkLost_LongerThanTheHold_IsUnknown_AndTheWayBackNeverSaysNotInSession()
    {
        var evaluator = new SimSessionEvaluator();
        EnterSession(evaluator, Inputs());

        for (var tick = 0; tick < SimSessionEvaluator.LinkLossHoldTicks; tick++)
        {
            Assert.Equal(SimSessionPhase.InSession, evaluator.ProcessTick(Inputs(connected: false, camera: null)));
        }

        Assert.Equal(SimSessionPhase.Unknown, evaluator.ProcessTick(Inputs(connected: false, camera: null)));
        Assert.False(evaluator.HoldingAcrossLinkLoss);

        // The flight is still there when the link returns: the debounce tick is "not known
        // yet", not "outside a flight" — the phase engine resets the flight on NotInSession.
        Assert.Equal(SimSessionPhase.Unknown, evaluator.ProcessTick(Inputs()));
        Assert.Equal(SimSessionPhase.InSession, evaluator.ProcessTick(Inputs()));
    }

    [Fact]
    public void MsfsSaysItQuits_EndsTheSessionAtOnce_NoHold()
    {
        var evaluator = new SimSessionEvaluator();
        EnterSession(evaluator, Inputs());

        Assert.Equal(
            SimSessionPhase.Unknown,
            evaluator.ProcessTick(new SimSessionInputs(false, false, true, null, null, SimQuit: true)));
        Assert.False(evaluator.HoldingAcrossLinkLoss);
    }

    [Fact]
    public void LinkBack_AtTheMainMenu_EndsTheSession_AfterTheSettle()
    {
        // The pilot left the flight while the link was down: the exit is real, and is
        // believed once the settle after the reconnect has passed.
        var evaluator = new SimSessionEvaluator();
        EnterSession(evaluator, Inputs());
        evaluator.ProcessTick(Inputs(connected: false, camera: null));

        for (var tick = 0; tick < SimSessionEvaluator.ReconnectSettleTicks; tick++)
        {
            Assert.Equal(SimSessionPhase.InSession, evaluator.ProcessTick(Inputs(camera: WorldMapCamera)));
        }

        Assert.Equal(SimSessionPhase.NotInSession, evaluator.ProcessTick(Inputs(camera: WorldMapCamera)));
        Assert.False(evaluator.HoldingAcrossLinkLoss);
    }

    [Fact]
    public void LinkBack_SimRunningEventLate_IsNotAnExit()
    {
        // "Sim" is sent on subscribe, but nothing promises it lands before the first tick.
        var evaluator = new SimSessionEvaluator();
        EnterSession(evaluator, Inputs());
        evaluator.ProcessTick(Inputs(connected: false, camera: null));

        Assert.Equal(SimSessionPhase.InSession, evaluator.ProcessTick(Inputs(simRunning: false)));
        Assert.Equal(SimSessionPhase.InSession, evaluator.ProcessTick(Inputs(simRunning: false)));
        Assert.Equal(SimSessionPhase.InSession, evaluator.ProcessTick(Inputs()));
        Assert.False(evaluator.HoldingAcrossLinkLoss);
    }

    [Fact]
    public void WithTheLinkUp_AnExitIsStillImmediate_AfterAnEarlierHold()
    {
        var evaluator = new SimSessionEvaluator();
        EnterSession(evaluator, Inputs());
        evaluator.ProcessTick(Inputs(connected: false, camera: null));
        evaluator.ProcessTick(Inputs());                             // link back, session kept

        Assert.Equal(SimSessionPhase.NotInSession, evaluator.ProcessTick(Inputs(camera: WorldMapCamera)));
    }

    [Fact]
    public void Disconnected_OutsideASession_IsUnknown_WithNothingToHold()
    {
        var evaluator = new SimSessionEvaluator();
        EnterSession(evaluator, Inputs(camera: WorldMapCamera));     // at the menu

        Assert.Equal(SimSessionPhase.Unknown, evaluator.ProcessTick(Inputs(connected: false, camera: null)));
        Assert.False(evaluator.HoldingAcrossLinkLoss);
    }

    [Fact]
    public void AvatarActive_ClassifiesAsWalkaround()
    {
        var evaluator = new SimSessionEvaluator();
        Assert.Equal(SimSessionPhase.Walkaround, EnterSession(evaluator, Inputs(isAvatar: true)));
    }

    [Fact]
    public void WalkaroundCameraFamily_ClassifiesAsWalkaround()
    {
        // MSFS 2024 walkaround cameras count even when the avatar SimVar has no value yet.
        var evaluator = new SimSessionEvaluator();
        Assert.Equal(SimSessionPhase.Walkaround, EnterSession(evaluator, Inputs(camera: WalkaroundCamera)));
    }

    [Fact]
    public void BoardingTheAircraft_WalkaroundBecomesInSession()
    {
        var evaluator = new SimSessionEvaluator();
        EnterSession(evaluator, Inputs(camera: WalkaroundCamera, isAvatar: true));
        Assert.Equal(
            SimSessionPhase.InSession,
            evaluator.ProcessTick(Inputs(camera: CockpitCamera, isAvatar: false)));
    }

    [Fact]
    public void SessionGatePredicates_SplitOnTheWalkaround()
    {
        // The two named questions (campaign #79): data is meaningful on the walkaround, but
        // ground services may not act there.
        Assert.True(SimSessionSnapshot.Empty with { Phase = SimSessionPhase.Walkaround } is { DataIsMeaningful: true });
        Assert.True(SimSessionSnapshot.Empty with { Phase = SimSessionPhase.InSession } is { DataIsMeaningful: true });
        Assert.False(SimSessionSnapshot.Empty with { Phase = SimSessionPhase.NotInSession } is { DataIsMeaningful: true });
        Assert.False(SimSessionSnapshot.Empty.DataIsMeaningful);

        Assert.True(SimSessionSnapshot.Empty with { Phase = SimSessionPhase.InSession } is { MayDriveGroundServices: true });
        Assert.False(SimSessionSnapshot.Empty with { Phase = SimSessionPhase.Walkaround } is { MayDriveGroundServices: true });
        Assert.False(SimSessionSnapshot.Empty.MayDriveGroundServices);
    }
}
