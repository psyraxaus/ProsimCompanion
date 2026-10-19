using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx;
using ProsimCompanion.Gsx.Automation;
using ProsimCompanion.Gsx.Mirror;
using ProsimCompanion.Gsx.Services;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>
/// The departure pump's inputs gatherer against faked collaborators (architecture review
/// C1): every live read the shell used to make inline is one fake here, so the mapping into
/// the pure core's inputs — and the two shell-derived figures, the #118 plan stamp and the
/// tankering pre-skip — is pinned without a GSX.
/// </summary>
public sealed class DeparturePumpGathererTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 23, 25, 21, TimeSpan.Zero);

    private readonly Mock<IGsxRemoteApi> _api = new();
    private readonly GsxStateMirror _mirror = new();
    private readonly Mock<IGsxTriggerSlot> _slot = new();
    private readonly Mock<IGsxServiceLifecycleView> _lifecycle = new();
    private readonly DepartureCycleState _cycle = new();
    private readonly Mock<IGsxFlightPlanStatus> _flightPlan = new();
    private readonly OfpStore _ofpStore = new();
    private readonly GsxResyncState _resyncState = new();
    private readonly GroundOpsSignals _signals = new();
    private readonly FuelConfirmationStore _fuelConfirmation;
    private readonly Mock<IEfbInitOverrides> _initOverrides = new();
    private readonly DeiceRequestStore _deiceRequest;
    private readonly GsxOptions _options = new();
    private readonly Mock<IDataRefSubscription> _fuelTotal = new();
    private readonly Mock<IDataRefSubscription> _plannedFuel = new();
    private readonly DeparturePumpGatherer _gatherer;
    private double _fob;
    private double _plannedFuelKg;

    public DeparturePumpGathererTests()
    {
        _fuelConfirmation = new FuelConfirmationStore(_ofpStore, _signals);
        _deiceRequest = new DeiceRequestStore(_signals);
        _api.SetupGet(a => a.Mirror).Returns(_mirror);
        _api.SetupGet(a => a.Readiness).Returns(GsxReadiness.Ready);
        _lifecycle.Setup(l => l.SnapshotCycles()).Returns(new Dictionary<string, GsxServiceLifecycleTracker.ServiceCycleSnapshot>(StringComparer.OrdinalIgnoreCase));
        _initOverrides.Setup(o => o.Snapshot()).Returns(new Dictionary<string, double>());
        _fuelTotal.Setup(s => s.GetValue(It.IsAny<double>())).Returns(() => _fob);
        _plannedFuel.Setup(s => s.GetValue(It.IsAny<double>())).Returns(() => _plannedFuelKg);
        var prosim = new Mock<IProsimDataRefs>();
        prosim.Setup(p => p.SubscribeDynamic(ProsimDataRefNames.FuelTotal.Name, It.IsAny<DataRefTier>())).Returns(_fuelTotal.Object);
        prosim.Setup(p => p.SubscribeDynamic(ProsimDataRefNames.EfbPlannedFuel.Name, It.IsAny<DataRefTier>())).Returns(_plannedFuel.Object);
        var options = new Mock<IOptionsMonitor<GsxOptions>>();
        options.SetupGet(o => o.CurrentValue).Returns(() => _options);

        _gatherer = new DeparturePumpGatherer(
            _api.Object, _slot.Object, _lifecycle.Object, _cycle, _flightPlan.Object, _ofpStore,
            _resyncState, _fuelConfirmation, _initOverrides.Object, _deiceRequest, options.Object, prosim.Object);
    }

    public void Dispose()
    {
        _gatherer.Dispose();
        _fuelConfirmation.Dispose();
        _deiceRequest.Dispose();
    }

    private DeparturePumpReading Read(DepartureLatchSnapshot? latches = null, DateTimeOffset? now = null, GsxAutomationPhase phase = GsxAutomationPhase.Preparation)
        => _gatherer.Read(phase, latches ?? DepartureLatchSnapshot.Initial, now ?? T0);

    private void PlanLoaded(bool ofpImported = true)
    {
        _flightPlan.SetupGet(f => f.FlightPlanAvailable).Returns(true);
        _flightPlan.SetupGet(f => f.FmsPlanPresent).Returns(true);
        _flightPlan.SetupGet(f => f.OfpImported).Returns(ofpImported);
        _flightPlan.SetupGet(f => f.FmsOrigin).Returns("EFHK");
        _flightPlan.SetupGet(f => f.FmsDestination).Returns("EGCC");
    }

    [Fact]
    public void MapsTheCollaboratorsIntoTheCoreInputs()
    {
        PlanLoaded();
        _resyncState.MarkAssessed(turnaroundDetected: false, loadsheetPrelimEdition: 0, loadsheetFinalSent: false);
        _cycle.MarkStarted();
        _cycle.MarkPrepComplete();
        _slot.SetupGet(s => s.InFlightServiceId).Returns("Catering");
        _ofpStore.Set(new OfpData { EstimatedEnroute = TimeSpan.FromHours(3) });
        _options.CompanyHubs = ["EF"];
        _mirror.ApplyState("airport", System.Text.Json.Nodes.JsonNode.Parse("""{ "icao": "EFHK" }"""));
        _options.GroundPrepActivation = "voiceThenAuto";
        _options.TurnaroundAutoStartDelaySeconds = 45;
        _options.TurnaroundAskFirst = true;
        var latches = DepartureLatchSnapshot.Initial with { ForceNext = true, TurnaroundReadySinceUtc = T0, TurnaroundQuestionAsked = true, TurnaroundDeclined = true };

        var reading = Read(latches, phase: GsxAutomationPhase.PushBack);
        var inputs = reading.Inputs;

        Assert.True(inputs.AutomationEnabled);
        Assert.True(inputs.GsxReady);
        Assert.True(inputs.ResyncAssessed);
        Assert.Equal(GsxAutomationPhase.PushBack, inputs.Phase);
        Assert.True(inputs.CycleStarted);
        Assert.False(inputs.CycleComplete);
        Assert.True(inputs.PrepComplete);
        Assert.True(inputs.RequireOfp);
        Assert.True(inputs.FmsPlanPresent);
        Assert.True(inputs.OfpImported);
        Assert.True(inputs.FlightPlanAvailable);
        Assert.Equal("EFHK", inputs.FmsOrigin);
        Assert.Equal("EGCC", inputs.FmsDestination);
        Assert.True(inputs.Forced);
        Assert.Equal("Catering", inputs.InFlightServiceId);
        Assert.False(inputs.IsTurnaround);
        Assert.Equal(TimeSpan.FromHours(3), inputs.EstimatedEnroute);
        Assert.Same(_mirror.Services, inputs.MirrorServices);
        Assert.True(inputs.VoiceActivationMode);
        // The effective steps are the configured ones shaped by the de-icing policy (an
        // undecided DeIce step is appended last); the waiting board uses the configured list.
        Assert.Equal(_options.DepartureServices, inputs.Steps.Take(_options.DepartureServices.Count));
        Assert.Same(_options.DepartureServices, reading.ConfiguredSteps);

        var turnaround = Assert.IsType<DepartureAutomationCore.TurnaroundStartInputs>(inputs.Turnaround);
        Assert.True(turnaround.Enabled);
        Assert.Equal(T0, turnaround.ReadySinceUtc);
        Assert.Equal(T0, turnaround.Now);
        Assert.Equal(TimeSpan.FromSeconds(45), turnaround.Delay);
        Assert.True(turnaround.AskFirst);
        Assert.True(turnaround.QuestionAsked);
        Assert.True(turnaround.Declined);
        Assert.Equal(45, reading.TurnaroundAutoStartDelaySeconds);
        Assert.True(reading.TurnaroundAskFirst);
        Assert.Equal(T0, reading.Now);
    }

    [Fact]
    public void CompanyHub_FromTheMirrorAirportAndTheConfiguredPrefixes()
    {
        _mirror.ApplyState("airport", System.Text.Json.Nodes.JsonNode.Parse("""{ "icao": "EFHK" }"""));
        _options.CompanyHubs = ["ef"];
        Assert.True(Read().Inputs.IsCompanyHub);

        _options.CompanyHubs = ["EG"];
        Assert.False(Read().Inputs.IsCompanyHub);
    }

    [Theory]
    [InlineData(null, new string[] { "EF" }, false)]
    [InlineData("EFHK", new string[0], false)]
    [InlineData("EFHK", new[] { " ef " }, true)]
    [InlineData("EFHK", new[] { "", "EGLL" }, false)]
    public void IsCompanyHub_Table(string? icao, string[] hubs, bool expected)
        => Assert.Equal(expected, DeparturePumpGatherer.IsCompanyHub(icao, hubs));

    [Fact]
    public void GsxNotReady_ReadsAsNotReady()
    {
        _api.SetupGet(a => a.Readiness).Returns(GsxReadiness.ConnectedGsxNotRunning);

        Assert.False(Read().Inputs.GsxReady);
    }

    [Fact]
    public void LifecycleCycles_MapOntoTheCycleView()
    {
        _lifecycle.Setup(l => l.SnapshotCycles()).Returns(new Dictionary<string, GsxServiceLifecycleTracker.ServiceCycleSnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            ["Refueling"] = new(Called: true, Requested: true, Active: false, Completed: false),
            ["Catering"] = new(Called: true, Requested: false, Active: false, Completed: true),
        });

        var cycle = Read().Inputs.Cycle;

        Assert.Equal(new DepartureCycleView(true, true, false, false), cycle("Refueling"));
        // Completed implies the earlier stages were reached, whatever edges were observed.
        Assert.Equal(new DepartureCycleView(true, true, true, true), cycle("catering"));
        Assert.Equal(default, cycle("Boarding"));
    }

    // ---- the #118 plan-availability stamp ----

    [Fact]
    public void PlanStamp_TakenWhenThePlanFirstAppears_CarriedAfterwards_ClearedWhenItLapses()
    {
        Assert.Null(Read().PlanAvailableSinceUtc);

        PlanLoaded();
        Assert.Equal(T0, Read().PlanAvailableSinceUtc);
        Assert.Equal(T0, Read(DepartureLatchSnapshot.Initial with { PlanAvailableSinceUtc = T0 }, now: T0.AddMinutes(5)).PlanAvailableSinceUtc);

        _flightPlan.SetupGet(f => f.FlightPlanAvailable).Returns(false);
        Assert.Null(Read(DepartureLatchSnapshot.Initial with { PlanAvailableSinceUtc = T0 }).PlanAvailableSinceUtc);
    }

    // ---- the tankering pre-skip (#117 / #118) ----

    [Fact]
    public void TankeringPreSkip_WaitsForThePlanFiguresToSettle_WithoutAnOfpBlock()
    {
        // 2026-09-05: the MCDU plan is detected (no OFP yet), efb.plannedfuel still holds
        // the previous leg's 2,300 kg while the FOB sits at 9,576 kg.
        PlanLoaded(ofpImported: false);
        _fob = 9_576;
        _plannedFuelKg = 2_300;

        var fresh = Read(DepartureLatchSnapshot.Initial with { PlanAvailableSinceUtc = T0 }, now: T0.AddSeconds(10));
        Assert.Null(fresh.Inputs.PreSkip!("Refueling"));

        var settled = Read(DepartureLatchSnapshot.Initial with { PlanAvailableSinceUtc = T0 }, now: T0.AddSeconds(90));
        Assert.Contains("tankering", settled.Inputs.PreSkip!("Refueling"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(9_576, settled.FuelOnBoardKg);
    }

    [Fact]
    public void TankeringPreSkip_UsesTheInitOverrideAsThePlan()
    {
        // 2026-09-19: the crew's INIT FUEL RAMP override is the plan when set.
        PlanLoaded();
        _ofpStore.Set(new OfpData { FuelPlanRampKg = 7_100 });
        _fob = 7_500;
        Assert.NotNull(Read().Inputs.PreSkip!("Refueling"));

        _initOverrides.Setup(o => o.Snapshot()).Returns(new Dictionary<string, double> { [IEfbInitOverrides.FuelRampKg] = 8_000 });
        Assert.Null(Read().Inputs.PreSkip!("Refueling"));
    }

    [Fact]
    public void TankeringPreSkip_Off_NeverSkips_AndOtherServicesAreNeverPreSkipped()
    {
        PlanLoaded();
        _ofpStore.Set(new OfpData { FuelPlanRampKg = 7_100 });
        _fob = 9_000;
        _options.SkipRefuelOnTankering = false;

        var preSkip = Read().Inputs.PreSkip!;
        Assert.Null(preSkip("Refueling"));
        Assert.Null(preSkip("Catering"));
    }

    // ---- the refuel-on-confirmation hold (2026-09-19) and the de-icing hold ----

    [Fact]
    public void RefuelingPreHold_OnlyInOnFuelConfirmedMode_UntilTheCrewConfirms()
    {
        var automatic = Read().Inputs.PreHold!;
        Assert.Null(automatic("Refueling"));

        _options.RefuelCall = GsxOptions.RefuelCallOnFuelConfirmed;
        var held = Read().Inputs.PreHold!;
        Assert.Equal(DeparturePumpGatherer.FuelConfirmationHoldReason, held("Refueling"));
        Assert.Null(held("Catering"));

        _fuelConfirmation.Confirm(7_100, "confirm");
        Assert.Null(Read().Inputs.PreHold!("Refueling"));
    }

    [Fact]
    public void DeiceStep_IsHeldWhileUndecided_AndSkippedWhenDeclined()
    {
        _options.DepartureServices = [.. _options.DepartureServices, new DepartureServiceStep(GsxServiceIds.DeIce, GsxServiceActivation.AfterAllCompleted, GsxServiceConstraint.Always)];

        var undecided = Read();
        Assert.False(undecided.DeiceStepNext);
        Assert.NotNull(undecided.Inputs.PreHold!(GsxServiceIds.DeIce));
        Assert.Null(undecided.Inputs.PreSkip!(GsxServiceIds.DeIce));

        _deiceRequest.MarkStepNext();
        _deiceRequest.Decline("voice", "declined by the captain (voice)");
        var declined = Read();
        Assert.True(declined.DeiceStepNext);
        Assert.Contains("declined", declined.Inputs.PreSkip!(GsxServiceIds.DeIce), StringComparison.OrdinalIgnoreCase);
    }

    // ---- next-leg OFP detection (issue #169) ----

    [Fact]
    public void NextLegOfpLoaded_Table()
    {
        var arrived = new OfpData { RequestId = "111" };
        var reimported = new OfpData { RequestId = "111" };
        var next = new OfpData { RequestId = "222" };
        var anonymous = new OfpData { RequestId = "" };

        Assert.False(DeparturePumpGatherer.NextLegOfpLoaded(arrivalOfpKnown: false, arrivalOfp: null, current: next));
        Assert.False(DeparturePumpGatherer.NextLegOfpLoaded(arrivalOfpKnown: true, arrivalOfp: arrived, current: null));
        Assert.True(DeparturePumpGatherer.NextLegOfpLoaded(arrivalOfpKnown: true, arrivalOfp: null, current: next));
        Assert.False(DeparturePumpGatherer.NextLegOfpLoaded(arrivalOfpKnown: true, arrivalOfp: arrived, current: arrived));
        Assert.False(DeparturePumpGatherer.NextLegOfpLoaded(arrivalOfpKnown: true, arrivalOfp: arrived, current: reimported));
        Assert.True(DeparturePumpGatherer.NextLegOfpLoaded(arrivalOfpKnown: true, arrivalOfp: arrived, current: next));
        Assert.True(DeparturePumpGatherer.NextLegOfpLoaded(arrivalOfpKnown: true, arrivalOfp: arrived, current: anonymous));
    }

    [Fact]
    public void NextLegOfpLoaded_FlowsFromTheLatchesAndTheStore()
    {
        var arrived = new OfpData { RequestId = "111" };
        _ofpStore.Set(arrived);

        Assert.False(Read(DepartureLatchSnapshot.Initial with { ArrivalOfpKnown = true, ArrivalOfp = arrived }).Inputs.Turnaround!.NextLegOfpLoaded);

        _ofpStore.Set(new OfpData { RequestId = "222" });
        Assert.True(Read(DepartureLatchSnapshot.Initial with { ArrivalOfpKnown = true, ArrivalOfp = arrived }).Inputs.Turnaround!.NextLegOfpLoaded);
    }
}
