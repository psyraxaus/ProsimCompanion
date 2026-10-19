using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Gsx.Automation;

/// <summary>Applies one departure pump effect to its collaborator. The seam the host walks
/// its effect list through — a fake records the sequence and the lock state it saw.</summary>
internal interface IDeparturePumpEffectApplier
{
    void Apply(DeparturePumpEffect effect);
}

/// <summary>
/// The departure pump's effect applier: one effect record → one collaborator call. Holds no
/// policy and no ordering — both belong to <see cref="DeparturePumpEffectPlanner"/>. The two
/// composite effects (<see cref="StartCycleEffect"/>, the SimBrief import) are composite in
/// the collaborators themselves: "mark started" has always meant cycle + decision + event +
/// implicit yes, and the public start paths share that one method through
/// <see cref="StartCycle"/>.
/// </summary>
public sealed class DeparturePumpEffectApplier : IDeparturePumpEffectApplier
{
    private readonly DepartureCycleState _cycle;
    private readonly DepartureCycleLatches _latches;
    private readonly IGsxDecisionLog _decisions;
    private readonly TurnaroundStartStore _turnaroundStart;
    private readonly DeiceRequestStore _deiceRequest;
    private readonly GroundOpsSignals _groundOpsSignals;
    private readonly IGsxTriggerSlot _slot;
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly ISimbriefImporter _simbrief;
    private readonly GsxPaxTargetArmer _paxTarget;
    private readonly ILogger<GsxAutomationService> _logger;

    public DeparturePumpEffectApplier(
        DepartureCycleState cycle,
        DepartureCycleLatches latches,
        IGsxDecisionLog decisions,
        TurnaroundStartStore turnaroundStart,
        DeiceRequestStore deiceRequest,
        GroundOpsSignals groundOpsSignals,
        IGsxTriggerSlot slot,
        GsxDiagnosticsStore diagnostics,
        ISimbriefImporter simbrief,
        GsxPaxTargetArmer paxTarget,
        ILogger<GsxAutomationService> logger)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        ArgumentNullException.ThrowIfNull(latches);
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(turnaroundStart);
        ArgumentNullException.ThrowIfNull(deiceRequest);
        ArgumentNullException.ThrowIfNull(groundOpsSignals);
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(simbrief);
        ArgumentNullException.ThrowIfNull(paxTarget);
        ArgumentNullException.ThrowIfNull(logger);
        _cycle = cycle;
        _latches = latches;
        _decisions = decisions;
        _turnaroundStart = turnaroundStart;
        _deiceRequest = deiceRequest;
        _groundOpsSignals = groundOpsSignals;
        _slot = slot;
        _diagnostics = diagnostics;
        _simbrief = simbrief;
        _paxTarget = paxTarget;
        _logger = logger;
    }

    /// <summary>Raised when an automation-started SimBrief import landed — the host pumps again.</summary>
    public event Action? SimbriefImported;

    /// <summary>The one place the cycle is marked started: records the source, settles an
    /// open ask-first question (any other trigger is an implicit yes) and raises the event.
    /// False when already started.</summary>
    public bool StartCycle(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (_cycle.Started)
        {
            return false;
        }

        _cycle.MarkStarted();
        _decisions.Record("departure sequence", $"started (source: {source})");
        _decisions.RecordEvent("gsx-departure-started", new { source, turnaround = _cycle.IsTurnaround });
        _turnaroundStart.Accept(source);
        return true;
    }

    /// <inheritdoc />
    void IDeparturePumpEffectApplier.Apply(DeparturePumpEffect effect) => Apply(effect);

    /// <summary>Applies one effect (the internal entry the host and tests use).</summary>
    internal void Apply(DeparturePumpEffect effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        switch (effect)
        {
            case RecordDecisionEffect { Once: true } decision:
                _decisions.RecordOnce(decision.Action, decision.Reason);
                break;
            case RecordDecisionEffect decision:
                _decisions.Record(decision.Action, decision.Reason);
                break;
            case RecordEventEffect sessionEvent:
                _decisions.RecordEvent(sessionEvent.Type, sessionEvent.Payload);
                break;
            case SetPlanAvailableSinceEffect stamp:
                _latches.SetPlanAvailableSince(stamp.Value);
                break;
            case SetTurnaroundReadySinceEffect stamp:
                _latches.SetTurnaroundReadySince(stamp.Value);
                break;
            case StartCycleEffect start:
                StartCycle(start.Source);
                break;
            case MarkTurnaroundQuestionAskedEffect:
                _latches.MarkTurnaroundQuestionAsked();
                break;
            case AskTurnaroundStartEffect ask:
                _turnaroundStart.Ask(ask.Prompt);
                break;
            case PublishBoardEffect board:
                _diagnostics.UpdateServiceBoard(board.Rows);
                break;
            case MarkDeiceStepNextEffect:
                _deiceRequest.MarkStepNext();
                break;
            case SetLastImportAttemptEffect stamp:
                _latches.SetLastImportAttempt(stamp.Value);
                break;
            case StartSimbriefImportEffect:
                StartSimbriefImport();
                break;
            case ArmPaxTargetEffect:
                _ = _paxTarget.TryArmAsync();
                break;
            case ClearForceNextEffect:
                _latches.SetForceNext(false);
                break;
            case MarkTankeringPrelimRaisedEffect:
                _latches.MarkTankeringPrelimRaised();
                break;
            case RaiseRefuelServiceActiveEffect:
                _groundOpsSignals.RaiseRefuelServiceActive();
                break;
            case RecordTriggerAttemptEffect attempt:
                _latches.SetTriggerAttempt(attempt.ServiceId, attempt.Attempt);
                break;
            case DispatchTriggerEffect dispatch:
                _ = _slot.TryDispatchAsync(new GsxTriggerRequest(dispatch.ServiceId, dispatch.Source)
                {
                    SlotWait = TimeSpan.Zero,
                });
                break;
            case MarkCycleCompleteEffect:
                _cycle.MarkComplete();
                break;
            case RaiseDepartureServicesCompletedEffect:
                _groundOpsSignals.RaiseDepartureServicesCompleted();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(effect), effect.GetType().Name, "No applier for this departure pump effect");
        }
    }

    /// <summary>Fires the SimBrief import in the background; the importer itself decision-logs
    /// its progress, and a success re-pumps the sequencer (through <see cref="SimbriefImported"/>).</summary>
    private void StartSimbriefImport()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var outcome = await _simbrief.TryImportAsync(source: "automation (MCDU plan detected)").ConfigureAwait(false);
                if (outcome == SimbriefImportOutcome.Imported)
                {
                    SimbriefImported?.Invoke();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SimBrief import attempt failed");
            }
        });
    }
}
