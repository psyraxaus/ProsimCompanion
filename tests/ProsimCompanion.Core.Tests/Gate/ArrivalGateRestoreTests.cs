using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Gate;
using ProsimCompanion.Core.State;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gate;

/// <summary>The persisted arrival gate (2026-09-20 EGLL: two in-flight app restarts lost the
/// confirmed gate 545R, GSX asked "Select Position" on the landing roll).</summary>
public sealed class ArrivalGateRestoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 3, 0, 0, TimeSpan.Zero);
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"arrival-gate-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    private static ArrivalGateState State(bool fired = false, string? destination = "EGLL", DateTimeOffset? savedAt = null)
        => new("545R", destination, fired, savedAt ?? Now - TimeSpan.FromMinutes(30));

    [Fact]
    public void Decide_NothingPersisted_Ignores()
        => Assert.Equal(ArrivalGateRestoreAction.Ignore, ArrivalGateRestorePlan.Decide(null, Now, FlightPhase.Descent, "EGLL"));

    [Fact]
    public void Decide_StaleFile_Ignores()
        => Assert.Equal(
            ArrivalGateRestoreAction.Ignore,
            ArrivalGateRestorePlan.Decide(State(savedAt: Now - TimeSpan.FromHours(30)), Now, FlightPhase.Descent, "EGLL"));

    [Fact]
    public void Decide_OtherDestinationLoaded_Ignores()
        => Assert.Equal(ArrivalGateRestoreAction.Ignore, ArrivalGateRestorePlan.Decide(State(), Now, FlightPhase.Descent, "LIRF"));

    [Fact]
    public void Decide_NoOfpYet_StillRestores()
        => Assert.Equal(ArrivalGateRestoreAction.DispatchBoth, ArrivalGateRestorePlan.Decide(State(), Now, FlightPhase.Descent, ""));

    [Theory]
    [InlineData(FlightPhase.Unknown)]
    [InlineData(FlightPhase.Preflight)]
    [InlineData(FlightPhase.Climb)]
    public void Decide_BeforeCruise_QueuesForTheCruiseEdge(FlightPhase phase)
        => Assert.Equal(ArrivalGateRestoreAction.Queue, ArrivalGateRestorePlan.Decide(State(), Now, phase, "EGLL"));

    [Theory]
    [InlineData(FlightPhase.Cruise)]
    [InlineData(FlightPhase.Descent)]
    [InlineData(FlightPhase.Approach)]
    [InlineData(FlightPhase.TaxiIn)]
    public void Decide_PastCruiseEntry_DispatchesBoth(FlightPhase phase)
        => Assert.Equal(ArrivalGateRestoreAction.DispatchBoth, ArrivalGateRestorePlan.Decide(State(), Now, phase, "EGLL"));

    [Fact]
    public void Decide_AlreadyFired_ReArmsGsxOnly()
        => Assert.Equal(
            ArrivalGateRestoreAction.DispatchGsxOnly,
            ArrivalGateRestorePlan.Decide(State(fired: true), Now, FlightPhase.Climb, "EGLL"));

    [Fact]
    public void StateFile_RoundTrips_AndClears()
    {
        var file = new ArrivalGateStateFile(NullLogger<ArrivalGateStateFile>.Instance, _path);

        file.Save(State(fired: true));
        var loaded = file.Load();

        Assert.NotNull(loaded);
        Assert.Equal("545R", loaded.Gate);
        Assert.Equal("EGLL", loaded.DestinationIcao);
        Assert.True(loaded.Fired);

        file.Clear();
        Assert.Null(file.Load());
    }

    [Fact]
    public void StateFile_CorruptFile_ReadsAsEmpty()
    {
        File.WriteAllText(_path, "{ not json");
        var file = new ArrivalGateStateFile(NullLogger<ArrivalGateStateFile>.Instance, _path);

        Assert.Null(file.Load());
        Assert.False(File.Exists(_path));
        foreach (var backup in Directory.GetFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + ".corrupt-*"))
        {
            File.Delete(backup);
        }
    }

    [Fact]
    public async Task Coordinator_ConfirmPersists_RestartInDescent_ReFiresBothTargets()
    {
        var phase = new Mock<IFlightPhaseSource>();
        phase.SetupGet(p => p.CurrentPhase).Returns(FlightPhase.Climb);
        var gsx = new Mock<IGsxGateControl>();
        var atc = new Mock<ISayIntentionsGateAssign>();
        atc.SetupGet(a => a.IsActive).Returns(true);
        atc.Setup(a => a.AssignGateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SayIntentionsGateAssignResult(true, false, "545R"));
        var ofp = new OfpStore();
        ofp.Set(new OfpData { DestinationIcao = "EGLL", FetchedAtUtc = DateTimeOffset.UtcNow });
        var file = new ArrivalGateStateFile(NullLogger<ArrivalGateStateFile>.Instance, _path);

        // Run 1: confirm in the climb — persisted, not fired.
        using (var first = new ArrivalGateCoordinator(phase.Object, ofp, gsx.Object, atc.Object,
            NullLogger<ArrivalGateCoordinator>.Instance, file))
        {
            first.Confirm("545r");
        }
        var persisted = file.Load();
        Assert.NotNull(persisted);
        Assert.False(persisted.Fired);

        // Run 2: the app restarts in the descent — the cruise edge is gone.
        phase.SetupGet(p => p.CurrentPhase).Returns(FlightPhase.Descent);
        using var second = new ArrivalGateCoordinator(phase.Object, ofp, gsx.Object, atc.Object,
            NullLogger<ArrivalGateCoordinator>.Instance, file);
        var (action, gate) = second.Restore();
        await second.LastDispatch;

        Assert.Equal(ArrivalGateRestoreAction.DispatchBoth, action);
        Assert.Equal("545R", gate);
        Assert.True(second.Snapshot().Sent);
        gsx.Verify(g => g.RequestGate("545R"), Times.Once);
        atc.Verify(a => a.AssignGateAsync("EGLL", "545R", It.IsAny<CancellationToken>()), Times.Once);
        Assert.True(file.Load()!.Fired);
    }

    [Fact]
    public async Task Coordinator_RestartAfterFire_ReArmsGsxWithoutResendingAtc()
    {
        var phase = new Mock<IFlightPhaseSource>();
        phase.SetupGet(p => p.CurrentPhase).Returns(FlightPhase.Cruise);
        var gsx = new Mock<IGsxGateControl>();
        var atc = new Mock<ISayIntentionsGateAssign>();
        atc.SetupGet(a => a.IsActive).Returns(true);
        var ofp = new OfpStore();
        ofp.Set(new OfpData { DestinationIcao = "EGLL", FetchedAtUtc = DateTimeOffset.UtcNow });
        var file = new ArrivalGateStateFile(NullLogger<ArrivalGateStateFile>.Instance, _path);
        file.Save(State(fired: true));

        using var coordinator = new ArrivalGateCoordinator(phase.Object, ofp, gsx.Object, atc.Object,
            NullLogger<ArrivalGateCoordinator>.Instance, file);
        var (action, _) = coordinator.Restore();
        await coordinator.LastDispatch;

        Assert.Equal(ArrivalGateRestoreAction.DispatchGsxOnly, action);
        gsx.Verify(g => g.RequestGate("545R"), Times.Once);
        atc.Verify(a => a.AssignGateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Coordinator_RestoredUnfiredQueue_FiresOnFirstCommitPastCruise()
    {
        var phase = new Mock<IFlightPhaseSource>();
        phase.SetupGet(p => p.CurrentPhase).Returns(FlightPhase.Unknown);
        var gsx = new Mock<IGsxGateControl>();
        var ofp = new OfpStore();
        var file = new ArrivalGateStateFile(NullLogger<ArrivalGateStateFile>.Instance, _path);
        file.Save(State());

        using var coordinator = new ArrivalGateCoordinator(phase.Object, ofp, gsx.Object, null,
            NullLogger<ArrivalGateCoordinator>.Instance, file);
        var (action, _) = coordinator.Restore();
        Assert.Equal(ArrivalGateRestoreAction.Queue, action);
        gsx.Verify(g => g.RequestGate(It.IsAny<string>()), Times.Never);

        // The engine's first classification after startup lands straight in the descent.
        phase.Raise(p => p.PhaseChanged += null, phase.Object,
            new FlightPhaseChangedEventArgs(FlightPhase.Unknown, FlightPhase.Descent));
        await coordinator.LastDispatch;

        gsx.Verify(g => g.RequestGate("545R"), Times.Once);
    }

    [Fact]
    public void Coordinator_ShutdownClearsTheFile_CancelClearsTheFile()
    {
        var phase = new Mock<IFlightPhaseSource>();
        phase.SetupGet(p => p.CurrentPhase).Returns(FlightPhase.Climb);
        var ofp = new OfpStore();
        var file = new ArrivalGateStateFile(NullLogger<ArrivalGateStateFile>.Instance, _path);

        using var coordinator = new ArrivalGateCoordinator(phase.Object, ofp, null, null,
            NullLogger<ArrivalGateCoordinator>.Instance, file);
        coordinator.Confirm("545R");
        Assert.NotNull(file.Load());

        phase.Raise(p => p.PhaseChanged += null, phase.Object,
            new FlightPhaseChangedEventArgs(FlightPhase.TaxiIn, FlightPhase.Shutdown));
        Assert.Null(file.Load());

        coordinator.Confirm("545R");
        coordinator.Cancel();
        Assert.Null(file.Load());
    }
}
