using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Ofp;
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
    private readonly Mock<IGsxTriggerSlot> _dispatcher = new();
    private readonly Mock<IGsxGroundPrepStatus> _groundPrep = new();
    private readonly Mock<IFlightPhaseSource> _flightPhase = new();
    private readonly Mock<IGsxFlightPlanStatus> _flightPlan = new();
    private readonly SimSessionStore _simSession = new();
    private readonly Mock<IDataRefSubscription> _jetwayLvar = new();
    private readonly GsxOptions _options = new();

    // Fuel figure inputs (2026-09-19 confirm/top-up): FOB, the EFB planned-fuel dataref, the
    // INIT override snapshot and the OFP store.
    private readonly Mock<IDataRefSubscription> _fuelTotal = new();
    private readonly Mock<IDataRefSubscription> _plannedFuel = new();
    private readonly Mock<IEfbInitOverrides> _initOverrides = new();
    private readonly OfpStore _ofpStore = new();
    private readonly GroundOpsSignals _signals = new();
    private readonly FuelConfirmationStore _fuelConfirmation;
    private readonly GsxDiagnosticsStore _diagnostics = new();
    private double _fob;
    private double _plannedFuelKg;
    private Dictionary<string, double> _overrides = new(StringComparer.OrdinalIgnoreCase);

    public GsxServiceControlTests()
    {
        _fuelConfirmation = new FuelConfirmationStore(_ofpStore, _signals);
    }

    private GsxServiceControl CreateControl()
    {
        _api.SetupGet(a => a.Readiness).Returns(GsxReadiness.Ready);
        _api.SetupGet(a => a.Mirror).Returns(_mirror);

        _jetwayLvar.Setup(s => s.GetValue(It.IsAny<double>())).Returns(0.0);
        var simVars = new Mock<ISimVars>();
        simVars
            .Setup(s => s.SubscribeDynamic(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DataRefTier>()))
            .Returns(_jetwayLvar.Object);

        _fuelTotal.Setup(s => s.GetValue(It.IsAny<double>())).Returns(() => _fob);
        _plannedFuel.Setup(s => s.GetValue(It.IsAny<double>())).Returns(() => _plannedFuelKg);
        var prosim = new Mock<IProsimDataRefs>();
        prosim
            .Setup(p => p.SubscribeDynamic(ProsimDataRefNames.FuelTotal.Name, It.IsAny<DataRefTier>()))
            .Returns(_fuelTotal.Object);
        prosim
            .Setup(p => p.SubscribeDynamic(ProsimDataRefNames.EfbPlannedFuel.Name, It.IsAny<DataRefTier>()))
            .Returns(_plannedFuel.Object);
        _initOverrides.Setup(o => o.Snapshot()).Returns(() => _overrides);

        var options = new Mock<IOptionsMonitor<GsxOptions>>();
        options.SetupGet(o => o.CurrentValue).Returns(() => _options);

        _groundPrep.SetupGet(p => p.PrepComplete).Returns(true);
        _flightPhase.SetupGet(f => f.CurrentPhase).Returns(FlightPhase.Preflight);
        _flightPlan.SetupGet(f => f.FlightPlanAvailable).Returns(true);
        _dispatcher
            .Setup(d => d.TryDispatchAsync(
                It.IsAny<GsxTriggerRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GsxTriggerDispatch(GsxTriggerDispatchStatus.Dispatched));

        return new GsxServiceControl(
            _api.Object,
            _lifecycle,
            _dispatcher.Object,
            _groundPrep.Object,
            _flightPhase.Object,
            _flightPlan.Object,
            _simSession,
            simVars.Object,
            prosim.Object,
            _fuelConfirmation,
            _initOverrides.Object,
            _ofpStore,
            _diagnostics,
            options.Object,
            NullLogger<GsxServiceControl>.Instance);
    }

    /// <summary>Loads an OFP with the given block fuel — the "plan" every fuel decision reads.</summary>
    private void SeedOfp(double blockKg)
        => _ofpStore.Set(new OfpData { RequestId = "req-1", FuelPlanRampKg = blockKg });

    private void SetSessionPhase(SimSessionPhase phase)
        => _simSession.Publish(SimSessionSnapshot.Empty with { Phase = phase });

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
            d => d.TryDispatchAsync(It.Is<GsxTriggerRequest>(r => r.ServiceId == "Refueling"), It.IsAny<CancellationToken>()),
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
            .Setup(d => d.TryDispatchAsync(It.Is<GsxTriggerRequest>(r => r.ServiceId == "Catering"), It.IsAny<CancellationToken>()))
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
            .Setup(d => d.TryDispatchAsync(It.Is<GsxTriggerRequest>(r => r.ServiceId == "Catering"), It.IsAny<CancellationToken>()))
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
            .Setup(d => d.TryDispatchAsync(It.Is<GsxTriggerRequest>(r => r.ServiceId == "DeIce"), It.IsAny<CancellationToken>()))
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
            d => d.TryDispatchAsync(It.Is<GsxTriggerRequest>(r => r.ServiceId == "OperateJetways"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData(GsxServiceAction.RequestRefuel, "Refueling")]
    [InlineData(GsxServiceAction.RequestCatering, "Catering")]
    [InlineData(GsxServiceAction.RequestBoarding, "Boarding")]
    public async Task NoFlightPlan_InPrepPhase_RefusesPlanGatedServices(GsxServiceAction action, string serviceId)
    {
        var control = CreateControl();
        SeedService(serviceId, "available");
        _flightPlan.SetupGet(f => f.FlightPlanAvailable).Returns(false);

        var outcome = await control.TryCallAsync(action);

        Assert.Equal(GsxServiceCallStatus.NotCallable, outcome.Status);
        Assert.Contains("flight plan", outcome.Detail, StringComparison.OrdinalIgnoreCase);
        _dispatcher.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(FlightPhase.ColdAndDark)]
    [InlineData(FlightPhase.Preflight)]
    [InlineData(FlightPhase.PushbackAndStart)]
    [InlineData(FlightPhase.TaxiOut)]
    public async Task NoFlightPlan_AnyPreTakeoffGroundPhase_RefusesPlanGatedServices(FlightPhase phase)
    {
        // Issue #60: the gate used to cover only Preflight/ColdAndDark, so plan-less requests
        // slipped through in the other pre-takeoff ground phases.
        var control = CreateControl();
        SeedService("Refueling", "available");
        _flightPlan.SetupGet(f => f.FlightPlanAvailable).Returns(false);
        _flightPhase.SetupGet(f => f.CurrentPhase).Returns(phase);

        var outcome = await control.TryCallAsync(GsxServiceAction.RequestRefuel);

        Assert.Equal(GsxServiceCallStatus.NotCallable, outcome.Status);
        Assert.Contains("flight plan", outcome.Detail, StringComparison.OrdinalIgnoreCase);
        _dispatcher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task NoFlightPlan_RequireOfpDisabled_StillDispatches()
    {
        var control = CreateControl();
        SeedService("Refueling", "available");
        _flightPlan.SetupGet(f => f.FlightPlanAvailable).Returns(false);
        _options.RequireOfpBeforeDeparture = false;

        var outcome = await control.TryCallAsync(GsxServiceAction.RequestRefuel);

        Assert.Equal(GsxServiceCallStatus.Called, outcome.Status);
    }

    [Fact]
    public async Task NoFlightPlan_OutsidePrepPhases_NeverPlanGated()
    {
        // An arrival-side call (deboarding phase context) must not be held hostage to the
        // NEXT leg's plan.
        var control = CreateControl();
        SeedService("Deboarding", "available");
        _flightPlan.SetupGet(f => f.FlightPlanAvailable).Returns(false);
        _flightPhase.SetupGet(f => f.CurrentPhase).Returns(FlightPhase.Shutdown);

        var outcome = await control.TryCallAsync(GsxServiceAction.RequestDeboarding);

        Assert.Equal(GsxServiceCallStatus.Called, outcome.Status);
    }

    [Fact]
    public async Task NotInSession_IsUnavailable()
    {
        var control = CreateControl();
        SeedService("Refueling", "available");
        SetSessionPhase(SimSessionPhase.NotInSession);

        var outcome = await control.TryCallAsync(GsxServiceAction.RequestRefuel);

        Assert.Equal(GsxServiceCallStatus.Unavailable, outcome.Status);
        _dispatcher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Walkaround_IsNotCallable()
    {
        var control = CreateControl();
        SeedService("Refueling", "available");
        SetSessionPhase(SimSessionPhase.Walkaround);

        var outcome = await control.TryCallAsync(GsxServiceAction.RequestRefuel);

        Assert.Equal(GsxServiceCallStatus.NotCallable, outcome.Status);
        _dispatcher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UnknownSession_DegradesOpen_AndDispatches()
    {
        // SimConnect absent (phase Unknown) must not block the on-demand path — degrade,
        // not fail. The store's default IS Unknown; make the intent explicit anyway.
        var control = CreateControl();
        SeedService("Refueling", "available");
        SetSessionPhase(SimSessionPhase.Unknown);

        var outcome = await control.TryCallAsync(GsxServiceAction.RequestRefuel);

        Assert.Equal(GsxServiceCallStatus.Called, outcome.Status);
    }

    [Fact]
    public async Task RequestGpu_MapsToTheGpuServiceId()
    {
        var control = CreateControl();
        SeedService("GPU", "available");

        var outcome = await control.TryCallAsync(GsxServiceAction.RequestGpu);

        Assert.Equal(GsxServiceCallStatus.Called, outcome.Status);
        _dispatcher.Verify(
            d => d.TryDispatchAsync(It.Is<GsxTriggerRequest>(r => r.ServiceId == "GPU"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ---- Fuel confirmation + top-up (2026-09-19, real-world SOP) ----

    /// <summary>Runs one complete Refueling cycle through the lifecycle tracker (performing →
    /// available = completed by the return-to-available rule).</summary>
    private void CompleteRefuelCycle()
    {
        SeedService("Refueling", "performing");
        _lifecycle.Process(_mirror.Services);
        SeedService("Refueling", "available");
        _lifecycle.Process(_mirror.Services);
    }

    [Fact]
    public async Task ConfirmFuel_RecordsTheFigure_AndOrdersTheTruck()
    {
        var control = CreateControl();
        SeedService("Refueling", "available");
        SeedOfp(7000);
        _fob = 3000;

        var outcome = await control.TryCallAsync(GsxServiceAction.ConfirmFuel);

        Assert.Equal(GsxServiceCallStatus.Called, outcome.Status);
        Assert.Contains("7000 kg confirmed", outcome.Detail, StringComparison.Ordinal);
        Assert.True(_fuelConfirmation.Confirmed);
        Assert.Equal(7000, _fuelConfirmation.Snapshot().ConfirmedKg);
        _dispatcher.Verify(
            d => d.TryDispatchAsync(It.Is<GsxTriggerRequest>(r => r.ServiceId == "Refueling"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ConfirmFuel_UsesTheInitOverride_OverTheOfp()
    {
        var control = CreateControl();
        SeedService("Refueling", "available");
        SeedOfp(7000);
        _overrides[IEfbInitOverrides.FuelRampKg] = 7850; // rounds up to 7900

        var outcome = await control.TryCallAsync(GsxServiceAction.ConfirmFuel);

        Assert.Equal(GsxServiceCallStatus.Called, outcome.Status);
        Assert.Equal(7900, _fuelConfirmation.Snapshot().ConfirmedKg);
    }

    [Fact]
    public async Task ConfirmFuel_WithoutAnyFigure_IsNotCallable_AndConfirmsNothing()
    {
        var control = CreateControl();
        SeedService("Refueling", "available");

        var outcome = await control.TryCallAsync(GsxServiceAction.ConfirmFuel);

        Assert.Equal(GsxServiceCallStatus.NotCallable, outcome.Status);
        Assert.False(_fuelConfirmation.Confirmed);
        _dispatcher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ConfirmFuel_StandsEvenWhenTheTruckCannotBeOrderedYet()
    {
        // Plan gate refuses the call (no flight plan) — the confirmation is still recorded so
        // the sequencer orders the truck itself once the plan arrives.
        var control = CreateControl();
        _flightPlan.SetupGet(f => f.FlightPlanAvailable).Returns(false);
        SeedService("Refueling", "available");
        _plannedFuelKg = 6500;

        var outcome = await control.TryCallAsync(GsxServiceAction.ConfirmFuel);

        Assert.Equal(GsxServiceCallStatus.NotCallable, outcome.Status);
        Assert.True(_fuelConfirmation.Confirmed);
        _dispatcher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DirectRefuelRequest_ImpliesConfirmation()
    {
        var control = CreateControl();
        SeedService("Refueling", "available");
        SeedOfp(7000);

        await control.TryCallAsync(GsxServiceAction.RequestRefuel);

        Assert.True(_fuelConfirmation.Confirmed);
        Assert.Equal("request", _fuelConfirmation.Snapshot().Source);
    }

    [Fact]
    public async Task CompletedRefuel_WithFobShortOfARaisedFigure_IsReCalledAsATopUp()
    {
        var control = CreateControl();
        SeedOfp(7000);
        CompleteRefuelCycle();
        _fob = 7000;
        _overrides[IEfbInitOverrides.FuelRampKg] = 8000; // the crew raised it afterwards

        var outcome = await control.TryCallAsync(GsxServiceAction.RequestRefuel);

        Assert.Equal(GsxServiceCallStatus.Called, outcome.Status);
        Assert.Contains("Top-up", outcome.Detail, StringComparison.Ordinal);
        Assert.False(_lifecycle.IsCompleted("Refueling")); // re-armed for the second run
        _dispatcher.Verify(
            d => d.TryDispatchAsync(It.Is<GsxTriggerRequest>(r => r.ServiceId == "Refueling"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CompletedRefuel_WithFobMeetingTheFigure_StaysAlreadySatisfied()
    {
        var control = CreateControl();
        SeedOfp(7000);
        CompleteRefuelCycle();
        _fob = 6990; // within the 25 kg tolerance

        var outcome = await control.TryCallAsync(GsxServiceAction.ConfirmFuel);

        Assert.Equal(GsxServiceCallStatus.AlreadySatisfied, outcome.Status);
        Assert.Contains("no top-up needed", outcome.Detail, StringComparison.Ordinal);
        Assert.True(_lifecycle.IsCompleted("Refueling"));
        _dispatcher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CompletedRefuel_WhileGsxStillReportsCompleting_AsksToRetry()
    {
        var control = CreateControl();
        SeedOfp(8000);
        SeedService("Refueling", "performing");
        _lifecycle.Process(_mirror.Services);
        SeedService("Refueling", "completed");
        _lifecycle.Process(_mirror.Services);
        _fob = 7000;

        var outcome = await control.TryCallAsync(GsxServiceAction.RequestRefuel);

        Assert.Equal(GsxServiceCallStatus.NotCallable, outcome.Status);
        Assert.True(_lifecycle.IsCompleted("Refueling")); // not re-armed
        _dispatcher.VerifyNoOtherCalls();
    }
}
