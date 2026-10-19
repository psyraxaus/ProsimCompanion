using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx;
using ProsimCompanion.Gsx.Automation;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>
/// One effect record → one collaborator call (architecture review C1). The applier holds no
/// policy, so these tests only pin that each record reaches its collaborator with the same
/// arguments the shell used to pass inline.
/// </summary>
public sealed class DeparturePumpEffectApplierTests : IDisposable
{
    private readonly DepartureCycleState _cycle = new();
    private readonly DepartureCycleLatches _latches = new();
    private readonly Mock<IGsxDecisionLog> _decisions = new();
    private readonly GroundOpsSignals _signals = new();
    private readonly TurnaroundStartStore _turnaroundStart;
    private readonly DeiceRequestStore _deiceRequest;
    private readonly Mock<IGsxTriggerSlot> _slot = new();
    private readonly GsxDiagnosticsStore _diagnostics = new();
    private readonly Mock<ISimbriefImporter> _simbrief = new();
    private readonly Mock<ISimVars> _simVars = new();
    private readonly Mock<IDataRefSubscription> _bookedSeats = new();
    private readonly GsxPaxTargetArmer _paxTarget;
    private readonly DeparturePumpEffectApplier _applier;
    private readonly GsxOptions _options = new();
    private string _seatString = "";

    public DeparturePumpEffectApplierTests()
    {
        _turnaroundStart = new TurnaroundStartStore(_signals);
        _deiceRequest = new DeiceRequestStore(_signals);
        _bookedSeats.Setup(s => s.GetValue(It.IsAny<string?>())).Returns(() => _seatString);
        var prosim = new Mock<IProsimDataRefs>();
        prosim.Setup(p => p.SubscribeDynamic(ProsimDataRefNames.PaxBookedString.Name, It.IsAny<DataRefTier>())).Returns(_bookedSeats.Object);
        var options = new Mock<IOptionsMonitor<GsxOptions>>();
        options.SetupGet(o => o.CurrentValue).Returns(() => _options);
        _slot.Setup(s => s.TryDispatchAsync(It.IsAny<GsxTriggerRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GsxTriggerDispatch(GsxTriggerDispatchStatus.Dispatched));

        _paxTarget = new GsxPaxTargetArmer(prosim.Object, _simVars.Object, options.Object, _decisions.Object, NullLogger<GsxAutomationService>.Instance);
        _applier = new DeparturePumpEffectApplier(
            _cycle, _latches, _decisions.Object, _turnaroundStart, _deiceRequest, _signals, _slot.Object,
            _diagnostics, _simbrief.Object, _paxTarget, NullLogger<GsxAutomationService>.Instance);
    }

    public void Dispose()
    {
        _paxTarget.Dispose();
        _turnaroundStart.Dispose();
        _deiceRequest.Dispose();
    }

    [Fact]
    public void StartCycle_MarksStarted_RecordsDecisionAndEvent_AcceptsAnOpenQuestion_Once()
    {
        _cycle.MarkTurnaround();
        _turnaroundStart.Ask("Next plan loaded — commence ground services?");
        var answers = new List<(bool Accepted, string Source)>();
        _turnaroundStart.Answered += (_, answer) => answers.Add(answer);

        Assert.True(_applier.StartCycle("voice"));

        Assert.True(_cycle.Started);
        _decisions.Verify(d => d.Record("departure sequence", "started (source: voice)"), Times.Once);
        _decisions.Verify(d => d.RecordEvent("gsx-departure-started", It.Is<object>(p => p.ToString()!.Contains("voice") && p.ToString()!.Contains("True"))), Times.Once);
        Assert.Equal([(true, "voice")], answers);

        Assert.False(_applier.StartCycle("web"));
        _decisions.Verify(d => d.Record("departure sequence", It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void StartCycleEffect_GoesThroughStartCycle()
    {
        _applier.Apply(new StartCycleEffect("auto"));

        Assert.True(_cycle.Started);
        _decisions.Verify(d => d.Record("departure sequence", "started (source: auto)"), Times.Once);
    }

    [Fact]
    public void DecisionEffects_ReachTheLog_OnceOrAlways()
    {
        _applier.Apply(new RecordDecisionEffect("hold Refueling", "waiting", Once: true));
        _applier.Apply(new RecordDecisionEffect("force next service", "calling Boarding"));
        _applier.Apply(new RecordEventEffect("gsx-departure-complete"));

        _decisions.Verify(d => d.RecordOnce("hold Refueling", "waiting"), Times.Once);
        _decisions.Verify(d => d.Record("force next service", "calling Boarding"), Times.Once);
        _decisions.Verify(d => d.RecordEvent("gsx-departure-complete", null), Times.Once);
    }

    [Fact]
    public void LatchEffects_MutateOnlyTheirOwnLatch()
    {
        var t = new DateTimeOffset(2026, 10, 10, 23, 25, 21, TimeSpan.Zero);
        _latches.SetForceNext(true);

        _applier.Apply(new SetPlanAvailableSinceEffect(t));
        _applier.Apply(new SetTurnaroundReadySinceEffect(t.AddSeconds(1)));
        _applier.Apply(new MarkTurnaroundQuestionAskedEffect());
        _applier.Apply(new SetLastImportAttemptEffect(t.AddSeconds(2)));
        _applier.Apply(new MarkTankeringPrelimRaisedEffect());
        _applier.Apply(new RecordTriggerAttemptEffect("Refueling", 2));

        var snapshot = _latches.Snapshot();
        Assert.Equal(t, snapshot.PlanAvailableSinceUtc);
        Assert.Equal(t.AddSeconds(1), snapshot.TurnaroundReadySinceUtc);
        Assert.True(snapshot.TurnaroundQuestionAsked);
        Assert.Equal(t.AddSeconds(2), snapshot.LastImportAttemptUtc);
        Assert.True(snapshot.TankeringPrelimRaised);
        Assert.Equal(2, snapshot.TriggerAttempts["refueling"]);
        Assert.True(snapshot.ForceNext); // untouched by the other writes

        _applier.Apply(new ClearForceNextEffect());
        Assert.False(_latches.Snapshot().ForceNext);
    }

    [Fact]
    public void DispatchTrigger_SendsThroughTheSlot_WithSlotWaitZero()
    {
        _applier.Apply(new DispatchTriggerEffect("Refueling", "sequencer — next in departure order"));

        _slot.Verify(s => s.TryDispatchAsync(
            It.Is<GsxTriggerRequest>(r => r.ServiceId == "Refueling" && r.Source == "sequencer — next in departure order" && r.SlotWait == TimeSpan.Zero && !r.RetryOnce),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void SignalsAndStores_ReceiveTheirEffects()
    {
        var refuelActive = 0;
        var departureComplete = 0;
        var stepNext = 0;
        _signals.RefuelServiceActive += () => refuelActive++;
        _signals.DepartureServicesCompleted += () => departureComplete++;
        _deiceRequest.StepBecameNext += (_, _) => stepNext++;
        TurnaroundStartQuestion? asked = null;
        _turnaroundStart.QuestionRaised += (_, q) => asked = q;

        _applier.Apply(new RaiseRefuelServiceActiveEffect());
        _applier.Apply(new MarkCycleCompleteEffect());
        _applier.Apply(new RaiseDepartureServicesCompletedEffect());
        _applier.Apply(new MarkDeiceStepNextEffect());
        _applier.Apply(new AskTurnaroundStartEffect("Next plan loaded — commence ground services?"));
        _applier.Apply(new PublishBoardEffect([new GsxServiceBoardRow("Refueling", GsxServiceStage.Waiting, null)]));

        Assert.Equal(1, refuelActive);
        Assert.True(_cycle.Complete);
        Assert.Equal(1, departureComplete);
        Assert.Equal(1, stepNext);
        Assert.True(_deiceRequest.Snapshot().StepNext);
        Assert.Equal("Next plan loaded — commence ground services?", asked?.Prompt);
        Assert.Equal("Refueling", Assert.Single(_diagnostics.Snapshot().ServiceBoard).ServiceId);
    }

    [Fact]
    public async Task ArmPaxTarget_WritesTheBookedCountOnce_AndRetriesWhileTheManifestIsEmpty()
    {
        _applier.Apply(new ArmPaxTargetEffect());
        _simVars.Verify(s => s.WriteAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(0, _paxTarget.BookedPax());

        _seatString = "true,true,false,true";
        Assert.True(await _paxTarget.TryArmAsync());
        _simVars.Verify(s => s.WriteAsync(GsxLvarNames.NumPassengers.Name, 3, It.IsAny<CancellationToken>()), Times.Once);
        _decisions.Verify(d => d.Record("pax target", "armed GSX with 3 passengers (booked manifest)"), Times.Once);

        Assert.True(await _paxTarget.TryArmAsync());
        _simVars.Verify(s => s.WriteAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()), Times.Once);

        _paxTarget.Reset();
        Assert.True(await _paxTarget.TryArmAsync());
        _simVars.Verify(s => s.WriteAsync(GsxLvarNames.NumPassengers.Name, 3, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ArmPaxTarget_MsfsNotConnected_RearmsForALaterPump()
    {
        _seatString = "true,true";
        _simVars.Setup(s => s.WriteAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("MSFS not connected"));

        Assert.False(await _paxTarget.TryArmAsync());

        _simVars.Setup(s => s.WriteAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        Assert.True(await _paxTarget.TryArmAsync());
    }

    [Fact]
    public async Task ArmPaxTarget_SkipCrewQuestion_WritesTheFourFlags()
    {
        _seatString = "true";
        _options.SkipCrewBoardingQuestion = true;

        Assert.True(await _paxTarget.TryArmAsync());

        _simVars.Verify(s => s.WriteAsync(GsxLvarNames.CrewNotBoarding, 1, It.IsAny<CancellationToken>()), Times.Once);
        _simVars.Verify(s => s.WriteAsync(GsxLvarNames.PilotsNotBoarding, 1, It.IsAny<CancellationToken>()), Times.Once);
        _simVars.Verify(s => s.WriteAsync(GsxLvarNames.CrewNotDeboarding, 1, It.IsAny<CancellationToken>()), Times.Once);
        _simVars.Verify(s => s.WriteAsync(GsxLvarNames.PilotsNotDeboarding, 1, It.IsAny<CancellationToken>()), Times.Once);
        _decisions.Verify(d => d.Record("pax target", "crew/pilot boarding questions suppressed via LVARs"), Times.Once);
    }

    [Fact]
    public async Task SimbriefImport_Landed_RaisesTheRePump()
    {
        var landed = new TaskCompletionSource();
        _applier.SimbriefImported += () => landed.TrySetResult();
        _simbrief.Setup(s => s.TryImportAsync(false, "automation (MCDU plan detected)", It.IsAny<CancellationToken>()))
            .ReturnsAsync(SimbriefImportOutcome.Imported);

        _applier.Apply(new StartSimbriefImportEffect());

        await landed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void LatchesResetForArrival_KeepsTheArrivalBaseline_ClearsEverythingElse()
    {
        _latches.SetForceNext(true);
        _latches.MarkTankeringPrelimRaised();
        _latches.SetTriggerAttempt("Refueling", 3);
        _latches.MarkTurnaroundQuestionAsked();
        _latches.MarkTurnaroundDeclined();
        var arrived = new Core.Aircraft.Ofp.OfpData { RequestId = "111" };

        _latches.ResetForArrival(arrived);

        var snapshot = _latches.Snapshot();
        Assert.Equal(DepartureLatchSnapshot.Initial with { ArrivalOfpKnown = true, ArrivalOfp = arrived, TriggerAttempts = snapshot.TriggerAttempts }, snapshot);
        Assert.Empty(snapshot.TriggerAttempts);
    }
}
