using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx;
using ProsimCompanion.Gsx.Automation;
using ProsimCompanion.Gsx.Menu;
using ProsimCompanion.Gsx.Mirror;
using ProsimCompanion.Gsx.Services;
using ProsimCompanion.Gsx.Sync;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>
/// The pushback tick's gatherer and applier against faked collaborators (architecture review
/// C1): the live reads the shell used to make inline map onto the reading, and each effect
/// record reaches its collaborator with the arguments the shell used to pass.
/// </summary>
public sealed class PushbackTickShellTests : IDisposable
{
    private readonly Mock<IGsxRemoteApi> _api = new();
    private readonly GsxStateMirror _mirror = new();
    private readonly Mock<IGsxServiceLifecycleView> _lifecycle = new();
    private readonly Mock<IGsxAutomationPhaseView> _automation = new();
    private readonly Mock<IPushbackGroundActions> _ground = new();
    private readonly Mock<IGsxTriggerSlot> _slot = new();
    private readonly Mock<IFlightPhaseSource> _flight = new();
    private readonly LoadsheetStore _loadsheets = new();
    private readonly GsxDiagnosticsStore _diagnostics = new();
    private readonly PushbackTickLatches _latches = new();
    private readonly GsxOptions _options = new();
    private readonly Dictionary<string, Mock<IDataRefSubscription>> _refs = new(StringComparer.Ordinal);
    private readonly PushbackTickGatherer _gatherer;
    private readonly PushbackTickEffectApplier _applier;

    public PushbackTickShellTests()
    {
        _api.SetupGet(a => a.Mirror).Returns(_mirror);
        _api.SetupGet(a => a.Readiness).Returns(GsxReadiness.Ready);
        _flight.Setup(f => f.Snapshot()).Returns(new FlightStateView(FlightPhase.Preflight, null, false));
        var prosim = new Mock<IProsimDataRefs>();
        prosim.Setup(p => p.SubscribeDynamic(It.IsAny<string>(), It.IsAny<DataRefTier>()))
            .Returns((string name, DataRefTier _) => Ref(name).Object);
        var simVars = new Mock<ISimVars>();
        simVars.Setup(s => s.SubscribeDynamic(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DataRefTier>()))
            .Returns((string name, string _, DataRefTier _) => Ref(name).Object);
        var options = new Mock<IOptionsMonitor<GsxOptions>>();
        options.SetupGet(o => o.CurrentValue).Returns(() => _options);
        _slot.Setup(s => s.TryDispatchAsync(It.IsAny<GsxTriggerRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GsxTriggerDispatch(GsxTriggerDispatchStatus.Dispatched));

        _gatherer = new PushbackTickGatherer(
            _api.Object, _lifecycle.Object, _automation.Object, _ground.Object, prosim.Object, simVars.Object,
            options.Object, _loadsheets, _flight.Object);
        _applier = new PushbackTickEffectApplier(_ground.Object, _slot.Object, _latches, _diagnostics, NullLogger<GsxPushbackSequenceService>.Instance);
    }

    public void Dispose() => _gatherer.Dispose();

    private Mock<IDataRefSubscription> Ref(string name)
    {
        if (!_refs.TryGetValue(name, out var sub))
        {
            sub = new Mock<IDataRefSubscription>();
            sub.SetupGet(s => s.Name).Returns(name);
            sub.Setup(s => s.GetValue(It.IsAny<int>())).Returns((int fallback) => fallback);
            sub.Setup(s => s.GetValue(It.IsAny<bool>())).Returns((bool fallback) => fallback);
            sub.Setup(s => s.GetValue(It.IsAny<double>())).Returns((double fallback) => fallback);
            _refs[name] = sub;
        }

        return sub;
    }

    private void Set<T>(string name, T value) => Ref(name).Setup(s => s.GetValue(It.IsAny<T>())).Returns(value);

    private void Services(string json) => _mirror.ApplyState("services", JsonNode.Parse(json));

    // ---- gatherer ----

    [Fact]
    public void Read_MapsEveryCollaborator()
    {
        _automation.SetupGet(a => a.Phase).Returns(GsxAutomationPhase.PushBack);
        _automation.SetupGet(a => a.DepartureStarted).Returns(true);
        _automation.SetupGet(a => a.DepartureComplete).Returns(true);
        _ground.SetupGet(g => g.AnyDoorOpen).Returns(true);
        _lifecycle.Setup(l => l.IsCompleted("Boarding")).Returns(true);
        _lifecycle.Setup(l => l.IsPending(GsxServiceIds.Departure)).Returns(true);
        _loadsheets.SetFinal(new LoadsheetSlotView(LoadsheetSlotStatus.Sent, 1, null, 0, 0, 0, 0, 0, 0, null));
        Services("""[ { "id": "Boarding", "state": "performing" }, { "id": "Departure", "state": "available", "canTrigger": true } ]""");
        Set(ProsimDataRefNames.OhExtLtBeacon.Name, 1);
        Set(ProsimDataRefNames.ApuRunning.Name, true);
        Set(GsxLvarNames.VehiclePushbackState.Name, 12.0);
        Set(GsxLvarNames.PushbackStatus.Name, 3.0);
        Set(GsxLvarNames.BypassPin.Name, 1.0);
        _flight.Setup(f => f.Snapshot()).Returns(new FlightStateView(
            FlightPhase.Preflight,
            new FlightDataSnapshot { AnyEngineRunning = true, EngineStarting = false, ParkBrakeSet = true },
            false));
        _options.CallPushbackWhenTugAttached = "afterFinalLoadsheet";
        _options.RemoveStairsAfterDeparture = "always";
        _options.CallJetwayStairsDuringDeparture = true;
        _options.BeaconPushbackSequenceEnabled = false;
        _options.CloseDoorsOnFinal = false;
        _options.ConfirmEngineStart = false;
        _options.SeqDoorsCloseDelayMinSec = 2;
        _options.SeqGpuDisconnectDelayMaxSec = 9;

        var reading = _gatherer.Read();

        Assert.Equal(GsxAutomationPhase.PushBack, reading.Phase);
        Assert.True(reading.AutomationEnabled);
        Assert.True(reading.GsxReady);
        Assert.False(reading.BeaconSequenceEnabled);
        Assert.True(reading.DepartureComplete);
        Assert.Equal(12, reading.VehicleState);
        Assert.Equal(3.0, reading.PushbackStatus);
        Assert.Equal(1.0, reading.BypassPin);
        Assert.False(reading.ConfirmEngineStartOption);
        Assert.Equal(new EngineStartEvidence(true, false, true), reading.Engines);
        Assert.False(reading.SequenceRuns);

        var hooks = reading.HookInputs;
        Assert.True(hooks.DepartureStarted);
        Assert.True(hooks.DepartureComplete);
        Assert.True(hooks.FinalLoadsheetSent);
        Assert.True(hooks.BoardingCompleted);
        Assert.True(hooks.PushbackStatusRaised);
        Assert.True(hooks.BoardingRequestedOrActive);
        Assert.True(hooks.PushbackCallable);
        Assert.True(hooks.PushbackPending);
        Assert.False(hooks.PushbackAlreadyDone);
        Assert.True(hooks.CallJetwayStairsDuringDeparture);
        Assert.Equal("always", hooks.RemoveStairsAfterDepartureMode);
        Assert.False(hooks.BeaconSequenceEnabled);
        Assert.False(hooks.CloseDoorsOnFinal);
        Assert.True(hooks.RemoveJetwayStairsOnFinal);
        Assert.Equal("afterFinalLoadsheet", hooks.CallPushbackWhenTugAttachedMode);

        var sequence = reading.SequenceInputs;
        Assert.True(sequence.Armed);
        Assert.True(sequence.BeaconOn);
        Assert.True(sequence.ApuRunning);
        Assert.True(sequence.AnyDoorOpen);
        Assert.True(sequence.CallPushback);
        Assert.True(sequence.PushbackCallable);
        Assert.Equal(2, reading.Delays.DoorsMin);
        Assert.Equal(9, reading.Delays.GpuMax);
    }

    [Fact]
    public void Read_SequenceArmsOnlyWithDepartureCompleteInAGroundPhase()
    {
        _automation.SetupGet(a => a.DepartureComplete).Returns(true);
        _automation.SetupGet(a => a.Phase).Returns(GsxAutomationPhase.Preparation);
        Assert.True(_gatherer.Read().SequenceInputs.Armed);

        _automation.SetupGet(a => a.Phase).Returns(GsxAutomationPhase.Flight);
        Assert.False(_gatherer.Read().SequenceInputs.Armed);

        _automation.SetupGet(a => a.Phase).Returns(GsxAutomationPhase.Preparation);
        _automation.SetupGet(a => a.DepartureComplete).Returns(false);
        Assert.False(_gatherer.Read().SequenceInputs.Armed);
    }

    [Fact]
    public void Read_PushbackIsGsxsDepartureService()
    {
        // Issue #40: a locally-invented "Pushback" id made the mirror lookup miss forever.
        Services("""[ { "id": "Pushback", "state": "available", "canTrigger": true } ]""");
        Assert.False(_gatherer.Read().HookInputs.PushbackCallable);

        Services("""[ { "id": "Departure", "state": "available", "canTrigger": true } ]""");
        Assert.True(_gatherer.Read().HookInputs.PushbackCallable);
        Assert.Equal("Departure", PushbackTickGatherer.PushbackServiceId);
    }

    [Fact]
    public void Read_NoFlightData_LeavesTheEngineEvidenceNull()
    {
        Assert.Null(_gatherer.Read().Engines);
    }

    // ---- applier ----

    [Fact]
    public void Decision_ReachesTheDiagnosticsStore()
    {
        _applier.Apply(new PushbackDecisionEffect("pushback tug", "bypass pin inserted"));

        var decision = Assert.Single(_diagnostics.Snapshot().RecentDecisions);
        Assert.Equal("pushback tug", decision.Action);
        Assert.Equal("bypass pin inserted", decision.Reason);
    }

    [Fact]
    public void LatchEffects_MutateTheirOwnLatch()
    {
        var state = new PushbackTickCore.HookState(true, true, false, false, false);
        _applier.Apply(new SetLastVehicleStateEffect(8));
        _applier.Apply(new SetLastBypassPinEffect(1));
        _applier.Apply(new SetHookStateEffect(state));
        _latches.SetConfirmCooldown(2);
        _applier.Apply(new DecrementConfirmCooldownEffect());

        var snapshot = _latches.Snapshot();
        Assert.Equal(8, snapshot.LastVehicleState);
        Assert.Equal(1, snapshot.LastBypassPin);
        Assert.Equal(state, snapshot.HookState);
        Assert.Equal(1, snapshot.ConfirmCooldown);

        _applier.Apply(new ResetSegmentLatchesEffect(GsxAutomationPhase.Arrival));
        Assert.Equal(
            PushbackLatchSnapshot.Initial with { LastResetPhase = GsxAutomationPhase.Arrival, LastVehicleState = 8, LastBypassPin = 1 },
            _latches.Snapshot());
    }

    [Fact]
    public async Task GroundActions_ReachTheSeam()
    {
        _applier.Apply(new CloseDoorsEffect());
        _applier.Apply(new RetractJetwayStairsEffect());
        _applier.Apply(new RemoveGroundEquipmentEffect());
        _applier.Apply(new RunDepartureJetwayStepEffect());
        _applier.Apply(new RemoveStairsAfterDepartureEffect("onlyJetway"));
        _applier.Apply(new TickGradualRemovalEffect());
        await Task.Yield();

        _ground.Verify(g => g.CloseAllDoorsAsync(), Times.Once);
        _ground.Verify(g => g.RequestJetwayRemovalAsync(), Times.Once);
        _ground.Verify(g => g.RemoveGroundEquipmentForDepartureAsync(), Times.Once);
        _ground.Verify(g => g.RunDepartureJetwayStep(), Times.Once);
        _ground.Verify(g => g.RemoveStairsAfterDepartureAsync("onlyJetway"), Times.Once);
        _ground.Verify(g => g.TickGradualRemovalAsync(), Times.Once);
    }

    [Fact]
    public async Task CallPushback_DispatchesDepartureWithRetryOnce_AndUnlatchesTheTugWhenNotDispatched()
    {
        _latches.SetHookState(new PushbackTickCore.HookState(true, true, false, false, false));
        _slot.Setup(s => s.TryDispatchAsync(It.IsAny<GsxTriggerRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GsxTriggerDispatch(GsxTriggerDispatchStatus.Busy, BusyServiceId: "Boarding"));

        _applier.Apply(new CallPushbackEffect());
        await WaitUntilAsync(() => _diagnostics.Snapshot().RecentDecisions.Count > 0);

        _slot.Verify(s => s.TryDispatchAsync(
            It.Is<GsxTriggerRequest>(r => r.ServiceId == "Departure" && r.Source == "pushback sequence" && r.RetryOnce && r.OnResolved != null),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(_latches.Snapshot().HookState.TugPushbackCalled);
        Assert.Equal("pushback call waiting — trigger slot busy with Boarding", Assert.Single(_diagnostics.Snapshot().RecentDecisions).Reason);
    }

    [Fact]
    public async Task CallPushback_DroppedAfterDispatch_UnlatchesThroughOnResolved()
    {
        _latches.SetHookState(new PushbackTickCore.HookState(true, true, false, false, false));
        GsxTriggerRequest? sent = null;
        _slot.Setup(s => s.TryDispatchAsync(It.IsAny<GsxTriggerRequest>(), It.IsAny<CancellationToken>()))
            .Callback((GsxTriggerRequest r, CancellationToken _) => sent = r)
            .ReturnsAsync(new GsxTriggerDispatch(GsxTriggerDispatchStatus.Dispatched));

        _applier.Apply(new CallPushbackEffect());
        await WaitUntilAsync(() => sent is not null);

        Assert.True(_latches.Snapshot().HookState.TugPushbackCalled);
        sent!.OnResolved!(GsxTriggerResolution.Confirmed);
        Assert.True(_latches.Snapshot().HookState.TugPushbackCalled);
        sent.OnResolved!(GsxTriggerResolution.Dropped);
        Assert.False(_latches.Snapshot().HookState.TugPushbackCalled);
    }

    [Fact]
    public async Task ConfirmEngineStart_Success_LatchesAndLogs()
    {
        _ground.Setup(g => g.ConfirmGoodEngineStartAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GsxIntentResult(GsxIntentOutcome.Success, "picked"));

        _applier.Apply(new ConfirmEngineStartEffect());
        await WaitUntilAsync(() => _latches.Snapshot().EngineStartConfirmed);

        Assert.Equal("confirmed good engine start — tug clear to disconnect", Assert.Single(_diagnostics.Snapshot().RecentDecisions).Reason);
    }

    [Fact]
    public async Task ConfirmEngineStart_Failure_CountsAnAttempt_SetsTheRetryGap_ThenGivesUpAtTheCap()
    {
        _ground.Setup(g => g.ConfirmGoodEngineStartAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GsxIntentResult(GsxIntentOutcome.MenuTitleMismatch, "no such entry"));

        _applier.Apply(new ConfirmEngineStartEffect());
        await WaitUntilAsync(() => _latches.Snapshot().ConfirmAttempts == 1);
        Assert.Equal(PushbackTickEffectPlanner.ConfirmRetryGapTicks, _latches.Snapshot().ConfirmCooldown);
        Assert.False(_latches.Snapshot().EngineStartConfirmed);

        for (var attempt = 2; attempt <= PushbackTickEffectPlanner.ConfirmMaxAttempts; attempt++)
        {
            _applier.Apply(new ConfirmEngineStartEffect());
            var expected = attempt;
            await WaitUntilAsync(() => _latches.Snapshot().ConfirmAttempts == expected);
        }

        await WaitUntilAsync(() => _latches.Snapshot().EngineStartConfirmed);
        Assert.Contains(_diagnostics.Snapshot().RecentDecisions, d => d.Reason.StartsWith("good-engine-start confirm failed 10 times", StringComparison.Ordinal));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "condition not met within 5 s");
            await Task.Delay(10);
        }
    }
}
