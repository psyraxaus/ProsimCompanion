using ProsimCompanion.Core.State;

namespace ProsimCompanion.Gsx.Automation;

/// <summary>
/// One thing the departure pump does to one collaborator, as a record. The effect planner
/// turns an evaluation outcome into an ordered list of these; the applier performs each
/// against its collaborator; the host only walks the list under its lock. Keeping the effects
/// as data is what lets a table test assert the ORDER the shell used to encode inline — the
/// pillar's recent shell bugs (#117/#118 tankering, #166 nested pump, #169 settle stamp) were
/// all ordering bugs.
/// </summary>
internal abstract record DeparturePumpEffect
{
    /// <summary>True when the host must apply this effect only after releasing the pump lock.
    /// Issue #166: a store mark whose observers pump again would have its nested pump bounce
    /// off the lock and lose the verdict until the next unrelated edge.</summary>
    public virtual bool RunsAfterLockRelease => false;
}

/// <summary>A decision-log line; <paramref name="Once"/> = deduplicated per action.</summary>
internal sealed record RecordDecisionEffect(string Action, string Reason, bool Once = false) : DeparturePumpEffect;

/// <summary>A session event (JSONL) — the record the flight-verification probes read.</summary>
internal sealed record RecordEventEffect(string Type, object? Payload = null) : DeparturePumpEffect;

/// <summary>Payload of the <c>gsx-turnaround-question</c> event (issue #169).</summary>
internal sealed record TurnaroundQuestionPayload(string Prompt);

/// <summary>Store the plan-availability stamp for the tankering settle window (#118).</summary>
internal sealed record SetPlanAvailableSinceEffect(DateTimeOffset? Value) : DeparturePumpEffect;

/// <summary>Store the turnaround auto-start settle stamp the core reported (issue #169).</summary>
internal sealed record SetTurnaroundReadySinceEffect(DateTimeOffset? Value) : DeparturePumpEffect;

/// <summary>Mark the departure cycle started under <paramref name="Source"/> (the one place
/// the cycle is marked started: decision, <c>gsx-departure-started</c> event, implicit yes to
/// an open ask-first question).</summary>
internal sealed record StartCycleEffect(string Source) : DeparturePumpEffect;

/// <summary>Latch that the FO's turnaround question has been raised this cycle.</summary>
internal sealed record MarkTurnaroundQuestionAskedEffect : DeparturePumpEffect;

/// <summary>Raise the FO's "commence ground services?" question on the turnaround store.</summary>
internal sealed record AskTurnaroundStartEffect(string Prompt) : DeparturePumpEffect;

/// <summary>Replace the departure status board on the diagnostics store.</summary>
internal sealed record PublishBoardEffect(IReadOnlyList<GsxServiceBoardRow> Rows) : DeparturePumpEffect;

/// <summary>Tell the de-icing store the DeIce step's turn has come (issue #166). Deferred until
/// after the lock is released: the store's observers pump again, and a nested pump would
/// bounce off the lock and lose the verdict until the next unrelated edge.</summary>
internal sealed record MarkDeiceStepNextEffect : DeparturePumpEffect
{
    public override bool RunsAfterLockRelease => true;
}

/// <summary>Fire the SimBrief import in the background (the MCDU plan was detected).</summary>
internal sealed record StartSimbriefImportEffect : DeparturePumpEffect;

/// <summary>Stamp the import cooldown.</summary>
internal sealed record SetLastImportAttemptEffect(DateTimeOffset Value) : DeparturePumpEffect;

/// <summary>Arm GSX's pax counter from the booked manifest (once per cycle, retried while the
/// manifest is empty or MSFS is not connected).</summary>
internal sealed record ArmPaxTargetEffect : DeparturePumpEffect;

/// <summary>The single-shot force-next was consumed by this evaluation.</summary>
internal sealed record ClearForceNextEffect : DeparturePumpEffect;

/// <summary>Latch that the tankering-skip prelim trigger was raised this cycle (#117).</summary>
internal sealed record MarkTankeringPrelimRaisedEffect : DeparturePumpEffect;

/// <summary>Raise the refuel-active ground-ops signal (the preliminary-loadsheet trigger).</summary>
internal sealed record RaiseRefuelServiceActiveEffect : DeparturePumpEffect;

/// <summary>Store the attempt number for this dispatch of <paramref name="ServiceId"/>.</summary>
internal sealed record RecordTriggerAttemptEffect(string ServiceId, int Attempt) : DeparturePumpEffect;

/// <summary>Send <c>service.trigger</c> through the trigger slot (slot wait zero).</summary>
internal sealed record DispatchTriggerEffect(string ServiceId, string Source) : DeparturePumpEffect;

/// <summary>Mark the departure cycle complete (every service completed or skipped).</summary>
internal sealed record MarkCycleCompleteEffect : DeparturePumpEffect;

/// <summary>Raise the "ready for pushback" milestone (#151).</summary>
internal sealed record RaiseDepartureServicesCompletedEffect : DeparturePumpEffect;
