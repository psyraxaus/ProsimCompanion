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
using ProsimCompanion.Gsx.Mirror;
using ProsimCompanion.Gsx.Services;
using ProsimCompanion.Gsx.Sync;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

public sealed class GsxServiceControlTests
{
    private readonly Mock<IGsxRemoteApi> _api = new();
    private readonly GsxStateMirror _mirror = new();
    private readonly GsxServiceLifecycleTracker _lifecycle = new(NullLogger<GsxServiceLifecycleTracker>.Instance);
    private readonly Mock<IGsxTriggerDispatcher> _dispatcher = new();
    private readonly Mock<IGsxGroundPrepStatus> _groundPrep = new();
    private readonly Mock<IFlightPhaseSource> _flightPhase = new();
    private readonly Mock<IDataRefSubscription> _jetwayLvar = new();
    private readonly GsxOptions _options = new();

    private GsxServiceControl CreateControl()
    {
        _api.SetupGet(a => a.Readiness).Returns(GsxReadiness.Ready);
        _api.SetupGet(a => a.Mirror).Returns(_mirror);

        _jetwayLvar.Setup(s => s.GetValue(It.IsAny<double>())).Returns(0.0);
        var simVars = new Mock<ISimVars>();
        simVars
            .Setup(s => s.Subscribe(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DataRefTier>()))
            .Returns(_jetwayLvar.Object);

        var options = new Mock<IOptionsMonitor<GsxOptions>>();
        options.SetupGet(o => o.CurrentValue).Returns(() => _options);

        _groundPrep.SetupGet(p => p.PrepComplete).Returns(true);
        _flightPhase.SetupGet(f => f.CurrentPhase).Returns(FlightPhase.Preflight);
        _dispatcher
            .Setup(d => d.TryDispatchServiceTriggerAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GsxTriggerDispatch(GsxTriggerDispatchStatus.Dispatched));

        return new GsxServiceControl(
            _api.Object,
            _lifecycle,
            _dispatcher.Object,
            _groundPrep.Object,
            _flightPhase.Object,
            simVars.Object,
            options.Object,
            NullLogger<GsxServiceControl>.Instance);
    }

    /// <summary>Seeds the mirror with one service in the given semantic wire state.</summary>
    private void SeedService(string id, string state, bool canTrigger = true)
        => _mirror.ApplyState("services", new JsonArray(new JsonObject
        {
            ["id"] = id,
            ["state"] = state,
            ["canTrigger"] = canTrigger,
        }));

    [Fact]
    public async Task Disconnected_IsUnavailable()
    {
        var control = CreateControl();
        _api.SetupGet(a => a.Readiness).Returns(GsxReadiness.Disconnected);

        var outcome = await control.TryCallAsync(GsxServiceAction.RequestRefuel);

        Assert.Equal(GsxServiceCallStatus.Unavailable, outcome.Status);
    }

    [Fact]
    public async Task ServiceMissingFromMirror_IsNotCallable()
    {
        var control = CreateControl();

        var outcome = await control.TryCallAsync(GsxServiceAction.RequestCatering);

        Assert.Equal(GsxServiceCallStatus.NotCallable, outcome.Status);
    }

    [Fact]
    public async Task CallableService_DispatchesThroughTheSerializedSlot()
    {
        var control = CreateControl();
        SeedService("Refueling", "available");

        var outcome = await control.TryCallAsync(GsxServiceAction.RequestRefuel);

        Assert.Equal(GsxServiceCallStatus.Called, outcome.Status);
        _dispatcher.Verify(
            d => d.TryDispatchServiceTriggerAsync("Refueling", It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData("requested")]
    [InlineData("performing")]
    [InlineData("completed")]
    public async Task ServiceAlreadyUnderway_IsAlreadySatisfied_AndNeverReFired(string state)
    {
        var control = CreateControl();
        SeedService("Boarding", state);

        var outcome = await control.TryCallAsync(GsxServiceAction.RequestBoarding);

        Assert.Equal(GsxServiceCallStatus.AlreadySatisfied, outcome.Status);
        _dispatcher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CompletedCycle_WithMirrorBackToAvailable_IsAlreadySatisfied()
    {
        // Quick services bounce back to "available" after completing (return-to-available
        // rule) — the lifecycle latch, not the mirror, remembers the completed cycle.
        var control = CreateControl();
        SeedService("Refueling", "performing");
        _lifecycle.Process(_mirror.Services);
        SeedService("Refueling", "available");
        _lifecycle.Process(_mirror.Services);

        var outcome = await control.TryCallAsync(GsxServiceAction.RequestRefuel);

        Assert.Equal(GsxServiceCallStatus.AlreadySatisfied, outcome.Status);
        _dispatcher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PendingCall_IsAlreadySatisfied()
    {
        var control = CreateControl();
        SeedService("Catering", "available");
        _lifecycle.MarkCalled("Catering");

        var outcome = await control.TryCallAsync(GsxServiceAction.RequestCatering);

        Assert.Equal(GsxServiceCallStatus.AlreadySatisfied, outcome.Status);
        _dispatcher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CannotTrigger_IsNotCallable()
    {
        var control = CreateControl();
        SeedService("Boarding", "available", canTrigger: false);

        var outcome = await control.TryCallAsync(GsxServiceAction.RequestBoarding);

        Assert.Equal(GsxServiceCallStatus.NotCallable, outcome.Status);
    }

    [Fact]
    public async Task BusySlot_OtherService_IsNotCallable_WithTheBlockingService()
    {
        var control = CreateControl();
        SeedService("Catering", "available");
        _dispatcher
            .Setup(d => d.TryDispatchServiceTriggerAsync("Catering", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GsxTriggerDispatch(GsxTriggerDispatchStatus.Busy, BusyServiceId: "Refueling"));

        var outcome = await control.TryCallAsync(GsxServiceAction.RequestCatering);

        Assert.Equal(GsxServiceCallStatus.NotCallable, outcome.Status);
        Assert.Contains("Refueling", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BusySlot_SameService_IsAlreadySatisfied()
    {
        var control = CreateControl();
        SeedService("Catering", "available");
        _dispatcher
            .Setup(d => d.TryDispatchServiceTriggerAsync("Catering", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GsxTriggerDispatch(GsxTriggerDispatchStatus.Busy, BusyServiceId: "Catering"));

        var outcome = await control.TryCallAsync(GsxServiceAction.RequestCatering);

        Assert.Equal(GsxServiceCallStatus.AlreadySatisfied, outcome.Status);
    }

    [Fact]
    public async Task GsxRejection_IsRejected_WithTheWireCode()
    {
        var control = CreateControl();
        SeedService("DeIce", "available");
        _dispatcher
            .Setup(d => d.TryDispatchServiceTriggerAsync("DeIce", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GsxTriggerDispatch(GsxTriggerDispatchStatus.Rejected, RejectCode: "no_gate"));

        var outcome = await control.TryCallAsync(GsxServiceAction.RequestDeice);

        Assert.Equal(GsxServiceCallStatus.Rejected, outcome.Status);
        Assert.Contains("no_gate", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pushback_IsNotCallableByDesign_BeaconFlowExplained()
    {
        var control = CreateControl();
        SeedService("Pushback", "available");

        var outcome = await control.TryCallAsync(GsxServiceAction.RequestPushback);

        Assert.Equal(GsxServiceCallStatus.NotCallable, outcome.Status);
        Assert.Contains("beacon", outcome.Detail, StringComparison.OrdinalIgnoreCase);
        _dispatcher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Jetway_AtJetwaylessStand_IsNotCallable_EvenThoughGsxListsIt()
    {
        var control = CreateControl();
        SeedService("OperateJetways", "available");
        _jetwayLvar.Setup(s => s.GetValue(It.IsAny<double>())).Returns(2.0);

        var outcome = await control.TryCallAsync(GsxServiceAction.RequestJetway);

        Assert.Equal(GsxServiceCallStatus.NotCallable, outcome.Status);
        Assert.Contains("stairs", outcome.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task JetwayConnect_WhileGroundPrepRuns_YieldsToTheCoordinator()
    {
        var control = CreateControl();
        SeedService("OperateJetways", "available");
        _groundPrep.SetupGet(p => p.PrepComplete).Returns(false);

        var outcome = await control.TryCallAsync(GsxServiceAction.RequestJetway);

        Assert.Equal(GsxServiceCallStatus.NotCallable, outcome.Status);
        _dispatcher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task JetwayConnect_PrepIncompleteButOutsidePrepWindow_StillDispatches()
    {
        // PrepComplete is false after flight too (the coordinator resets) — that must not
        // block an arrival-side jetway call.
        var control = CreateControl();
        SeedService("OperateJetways", "available");
        _groundPrep.SetupGet(p => p.PrepComplete).Returns(false);
        _flightPhase.SetupGet(f => f.CurrentPhase).Returns(FlightPhase.Shutdown);

        var outcome = await control.TryCallAsync(GsxServiceAction.RequestJetway);

        Assert.Equal(GsxServiceCallStatus.Called, outcome.Status);
    }

    [Fact]
    public async Task ConnectedJetway_RequestConnect_IsAlreadySatisfied_NeverToggledOff()
    {
        var control = CreateControl();
        SeedService("OperateJetways", "performing");

        var outcome = await control.TryCallAsync(GsxServiceAction.RequestJetway);

        Assert.Equal(GsxServiceCallStatus.AlreadySatisfied, outcome.Status);
        _dispatcher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RetractJetway_NothingConnected_IsAlreadySatisfied()
    {
        var control = CreateControl();
        SeedService("OperateJetways", "available");

        var outcome = await control.TryCallAsync(GsxServiceAction.RetractJetway);

        Assert.Equal(GsxServiceCallStatus.AlreadySatisfied, outcome.Status);
        _dispatcher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RetractJetway_Connected_TriggersTheToggle()
    {
        var control = CreateControl();
        SeedService("OperateJetways", "completed");

        var outcome = await control.TryCallAsync(GsxServiceAction.RetractJetway);

        Assert.Equal(GsxServiceCallStatus.Called, outcome.Status);
        _dispatcher.Verify(
            d => d.TryDispatchServiceTriggerAsync("OperateJetways", It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RequestGpu_MapsToTheGpuServiceId()
    {
        var control = CreateControl();
        SeedService("GPU", "available");

        var outcome = await control.TryCallAsync(GsxServiceAction.RequestGpu);

        Assert.Equal(GsxServiceCallStatus.Called, outcome.Status);
        _dispatcher.Verify(
            d => d.TryDispatchServiceTriggerAsync("GPU", It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
