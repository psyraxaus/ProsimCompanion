using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Mirror;
using ProsimCompanion.Gsx.Protocol;
using ProsimCompanion.Gsx.Services;

namespace ProsimCompanion.Gsx.Automation;

/// <summary>
/// The departure pump's effect planner, pure: (reading, outcome, latches) → the ordered list
/// of effects the shell must perform. The ORDER is the behaviour — it used to live inline in
/// the shell's pump with no test able to reach it. Each rule carries the archaeology that
/// put it there; a table test pins the sequence per scenario.
/// <para>
/// Order: plan-availability stamp → turnaround settle stamp → auto-start → ask-first question
/// → hold decision → waiting board (stop) → de-ice step next (deferred past the lock) →
/// SimBrief import → plan diagnostic → pax target → force consumed → board publish → skip
/// records (+ tankering prelim raise) → hold records → trigger dispatch (attempt counting) →
/// all done (complete + event + signal).
/// </para>
/// </summary>
internal static class DeparturePumpEffectPlanner
{
    /// <summary>Fires the SimBrief import at most this often — the importer decision-logs
    /// its own progress and a success re-pumps the sequencer.</summary>
    internal static readonly TimeSpan ImportCooldown = TimeSpan.FromSeconds(60);

    /// <summary>The FO's ask-first question text (issue #169). Probed by
    /// <c>turnaround-auto-start</c> — keep the wording stable.</summary>
    internal const string TurnaroundStartPrompt = "Next plan loaded — commence ground services?";

    internal static IReadOnlyList<DeparturePumpEffect> Plan(
        DeparturePumpReading reading,
        DepartureAutomationCore.PumpOutcome outcome,
        DepartureLatchSnapshot latches)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(latches);

        var effects = new List<DeparturePumpEffect>();
        var inputs = reading.Inputs;

        // Plan-figure settling for the tankering decision (#118): the gatherer derived the
        // new stamp; store it when it moved.
        if (reading.PlanAvailableSinceUtc != latches.PlanAvailableSinceUtc)
        {
            effects.Add(new SetPlanAvailableSinceEffect(reading.PlanAvailableSinceUtc));
        }

        // Settle stamp for the next evaluation (issue #169): the core reports when the
        // turnaround conditions first held (or null once they lapse). The decision is
        // written once, when the stamp is first taken.
        if (latches.TurnaroundReadySinceUtc is null && outcome.TurnaroundReadySinceUtc is { } readySince)
        {
            effects.Add(new RecordDecisionEffect(
                "departure sequence",
                $"turnaround with the next plan loaded — auto-start in {reading.TurnaroundAutoStartDelaySeconds} s"
                + (reading.TurnaroundAskFirst ? " (the FO will ask first)" : "")));
            effects.Add(new SetTurnaroundReadySinceEffect(readySince));
        }
        else if (outcome.TurnaroundReadySinceUtc is null && latches.TurnaroundReadySinceUtc is not null)
        {
            effects.Add(new SetTurnaroundReadySinceEffect(null));
        }

        if (outcome.AutoStartSource is { } autoSource)
        {
            effects.Add(new StartCycleEffect(autoSource));
            effects.Add(new RecordDecisionEffect(
                "departure sequence",
                autoSource == DepartureAutomationCore.TurnaroundAutoStartSource
                    ? "auto-started (turnaround)"
                    : "auto-started (Preparation phase)"));
        }

        if (outcome.AskTurnaroundStart && !latches.TurnaroundQuestionAsked)
        {
            effects.Add(new MarkTurnaroundQuestionAskedEffect());
            effects.Add(new RecordDecisionEffect("departure sequence", "turnaround — asking the captain to commence ground services"));
            effects.Add(new RecordEventEffect("gsx-turnaround-question", new TurnaroundQuestionPayload(TurnaroundStartPrompt)));
            effects.Add(new AskTurnaroundStartEffect(TurnaroundStartPrompt));
        }

        if (outcome.HoldReason is not null)
        {
            effects.Add(new RecordDecisionEffect("hold departure services", outcome.HoldReason, Once: true));
        }

        if (outcome.WaitingBoard is not null)
        {
            effects.Add(new PublishBoardEffect(WaitingBoardRows(reading.ConfiguredSteps, inputs.Cycle, outcome.WaitingBoard)));
            return effects;
        }

        if (outcome.Plan is not { } plan)
        {
            return effects; // gated before sequencing with no board to publish (disabled / complete)
        }

        // Issue #166: the de-icing policy runs when the DeIce step's turn comes. The store is
        // marked AFTER the pump lock is released (the effect says so) — its observers pump
        // again, and a nested pump would bounce off the lock and lose the verdict until the
        // next unrelated edge. The decision line is written now, in order.
        if (!reading.DeiceStepNext
            && plan.HeldAtTurn.Any(id => id.Equals(GsxServiceIds.DeIce, StringComparison.OrdinalIgnoreCase)))
        {
            effects.Add(new RecordDecisionEffect("de-ice step next", "every earlier service done — the de-icing policy decides now", Once: true));
            effects.Add(new MarkDeiceStepNextEffect());
        }

        // Only invoked once the pilot's MCDU plan is detected — never preemptively; 60 s
        // cooldown between attempts.
        if (outcome.StartSimbriefImport && reading.Now - latches.LastImportAttemptUtc >= ImportCooldown)
        {
            effects.Add(new SetLastImportAttemptEffect(reading.Now));
            effects.Add(new StartSimbriefImportEffect());
        }

        if (outcome.PlanDiagnostic is not null)
        {
            effects.Add(new RecordDecisionEffect("flight plan detection", outcome.PlanDiagnostic, Once: true));
        }

        if (outcome.ArmPaxTarget)
        {
            effects.Add(new ArmPaxTargetEffect());
        }

        if (outcome.ConsumedForce)
        {
            effects.Add(new ClearForceNextEffect()); // single-shot, consumed by this evaluation
            effects.Add(new RecordDecisionEffect(
                "force next service",
                plan.Trigger is not null ? $"calling {plan.Trigger}" : "nothing eligible to force right now"));
        }

        effects.Add(new PublishBoardEffect(BoardRows(inputs.Steps, plan, inputs.MirrorServices, inputs.Cycle, inputs.InFlightServiceId)));

        var tankeringPrelimRaised = latches.TankeringPrelimRaised;
        foreach (var (serviceId, reason) in plan.Skipped)
        {
            effects.Add(new RecordDecisionEffect($"skip {serviceId}", reason, Once: true));

            // Tankering skip (#117): the truck is never ordered, so the refuel-active
            // edge that fires the preliminary loadsheet never happens — raise it here
            // once per cycle so autoPrelimOnRefuel still produces the prelim.
            if (serviceId.Equals(GsxServiceIds.Refueling, StringComparison.OrdinalIgnoreCase)
                && reason.Contains("tankering", StringComparison.OrdinalIgnoreCase)
                && !tankeringPrelimRaised)
            {
                tankeringPrelimRaised = true;
                effects.Add(new MarkTankeringPrelimRaisedEffect());
                effects.Add(new RecordDecisionEffect("refuel skipped for tankering", "preliminary loadsheet trigger raised in place of the refuel-active edge"));
                effects.Add(new RaiseRefuelServiceActiveEffect());
            }
        }

        foreach (var (serviceId, reason) in plan.Holds)
        {
            effects.Add(new RecordDecisionEffect($"hold {serviceId}", reason, Once: true));
        }

        if (outcome.Trigger is { } trigger)
        {
            // A tankering skip is a live judgement, not a latch — when the inputs
            // materially change (2026-09-05: the pilot reset the FOB to ~300 kg after the
            // skip had been published), calling Refueling after all is CORRECT, but the
            // flight log must say why the skip reversed instead of contradicting itself
            // silently (#118).
            if (trigger.Equals(GsxServiceIds.Refueling, StringComparison.OrdinalIgnoreCase)
                && latches.TankeringPrelimRaised)
            {
                effects.Add(new RecordDecisionEffect(
                    "tankering skip reversed",
                    $"FOB {reading.FuelOnBoardKg:F0} kg no longer meets the plan — Refueling called after all",
                    Once: true));
            }

            // No watcher retry: a dropped call frees the slot and this pump re-offers the
            // same service — the sequencer IS the retry-forever policy. SlotWait zero so
            // the pump never stalls waiting on an on-demand caller's trigger.
            var attempt = latches.TriggerAttempts.GetValueOrDefault(trigger) + 1;
            var reason = attempt == 1
                ? plan.TriggerReason ?? "next in departure order"
                : $"{plan.TriggerReason} (attempt {attempt})";
            effects.Add(new RecordTriggerAttemptEffect(trigger, attempt));
            effects.Add(new DispatchTriggerEffect(trigger, $"sequencer — {reason}"));
        }

        if (outcome.AllDone)
        {
            effects.Add(new MarkCycleCompleteEffect());
            effects.Add(new RecordDecisionEffect("departure sequence", "all departure services completed or skipped"));
            effects.Add(new RecordEventEffect("gsx-departure-complete"));
            effects.Add(new RaiseDepartureServicesCompletedEffect()); // "ready for pushback" (#151)
        }

        return effects;
    }

    /// <summary>The Prosim2GSX-style departure status board from the current plan, mirror
    /// states and lifecycle cycles, in configured step order. "Called" is truthful: it means
    /// a trigger is in flight or GSX confirmed the call — never a dropped request.</summary>
    internal static IReadOnlyList<GsxServiceBoardRow> BoardRows(
        IReadOnlyList<DepartureServiceStep> steps,
        DeparturePlan plan,
        IReadOnlyDictionary<string, GsxServiceInfo> services,
        Func<string, DepartureCycleView> cycle,
        string? inFlight)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(cycle);

        var holds = plan.Holds.ToDictionary(h => h.ServiceId, h => h.Reason, StringComparer.OrdinalIgnoreCase);
        var skips = plan.Skipped.ToDictionary(s => s.ServiceId, s => s.Reason, StringComparer.OrdinalIgnoreCase);

        var rows = new List<GsxServiceBoardRow>();
        foreach (var id in DistinctServiceIds(steps))
        {
            services.TryGetValue(id, out var info);
            var view = cycle(id);
            // Pending = called this cycle and not yet completed (the tracker's IsPending rule).
            var called = (view.Called && !view.Completed)
                || string.Equals(id, plan.Trigger, StringComparison.OrdinalIgnoreCase)
                || string.Equals(id, inFlight, StringComparison.OrdinalIgnoreCase);
            var row = view.Completed ? new GsxServiceBoardRow(id, GsxServiceStage.Completed, null)
                : info?.State == GsxServiceState.Active ? new GsxServiceBoardRow(id, GsxServiceStage.Active, info.ProgressText)
                : info?.State == GsxServiceState.Requested ? new GsxServiceBoardRow(id, GsxServiceStage.Requested, null)
                : called ? new GsxServiceBoardRow(id, GsxServiceStage.Called, holds.GetValueOrDefault(id))
                : skips.TryGetValue(id, out var skipReason) ? new GsxServiceBoardRow(id, GsxServiceStage.Skipped, skipReason)
                : holds.TryGetValue(id, out var holdReason) ? new GsxServiceBoardRow(id, GsxServiceStage.Held, holdReason)
                : new GsxServiceBoardRow(id, GsxServiceStage.Waiting, null);
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>Board shown before sequencing runs: every non-completed service Waiting with
    /// one shared reason (sequence not started / ground prep).</summary>
    internal static IReadOnlyList<GsxServiceBoardRow> WaitingBoardRows(
        IReadOnlyList<DepartureServiceStep> configuredSteps,
        Func<string, DepartureCycleView> cycle,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(configuredSteps);
        ArgumentNullException.ThrowIfNull(cycle);
        return DistinctServiceIds(configuredSteps)
            .Select(id => cycle(id).Completed
                ? new GsxServiceBoardRow(id, GsxServiceStage.Completed, null)
                : new GsxServiceBoardRow(id, GsxServiceStage.Waiting, reason))
            .ToList();
    }

    private static IEnumerable<string> DistinctServiceIds(IReadOnlyList<DepartureServiceStep> steps)
        => steps
            .Select(step => step.Service)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase);
}
