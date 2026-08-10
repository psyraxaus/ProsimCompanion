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
        Assert.Equal(SimSessionPhase.NotInSession, evaluator.ProcessTick(Inputs()));
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

    [Fact]
    public void Disconnect_ResetsToUnknown_AndReentryIsDebounced()
    {
        var evaluator = new SimSessionEvaluator();
        EnterSession(evaluator, Inputs());
        Assert.Equal(SimSessionPhase.Unknown, evaluator.ProcessTick(Inputs(connected: false)));
        Assert.Equal(SimSessionPhase.NotInSession, evaluator.ProcessTick(Inputs()));
        Assert.Equal(SimSessionPhase.InSession, evaluator.ProcessTick(Inputs()));
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
    public void SnapshotInSession_CoversWalkaround()
    {
        Assert.True(SimSessionSnapshot.Empty with { Phase = SimSessionPhase.Walkaround } is { InSession: true });
        Assert.True(SimSessionSnapshot.Empty with { Phase = SimSessionPhase.InSession } is { InSession: true });
        Assert.False(SimSessionSnapshot.Empty with { Phase = SimSessionPhase.NotInSession } is { InSession: true });
        Assert.False(SimSessionSnapshot.Empty.InSession);
    }
}
