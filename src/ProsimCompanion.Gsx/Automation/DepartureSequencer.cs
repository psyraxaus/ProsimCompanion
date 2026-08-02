using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Gsx.Mirror;
using ProsimCompanion.Gsx.Protocol;

namespace ProsimCompanion.Gsx.Automation;

/// <summary>What one departure service's cycle has observably reached this turnaround, from the
/// lifecycle tracker: <c>Called</c> is a CONFIRMED call (GSX visibly picked the request up) —
/// a trigger merely sent is the sequencer's <c>awaitingConfirmation</c> input instead.</summary>
public readonly record struct DepartureCycleView(
    bool Called,
    bool ReachedRequested,
    bool ReachedActive,
    bool Completed);

/// <summary>One evaluation's outcome: at most ONE service to trigger now (GSX drops rapid-fire
/// requests — dispatch is strictly one confirmed call at a time), services holding and skipped
/// with reasons, and overall completion.</summary>
public sealed record DeparturePlan(
    string? Trigger,
    string? TriggerReason,
    IReadOnlyList<(string ServiceId, string Reason)> Holds,
    IReadOnlyList<(string ServiceId, string Reason)> Skipped,
    bool AllDone);

/// <summary>
/// Pure departure sequencing — the Prosim2GSX cursor model. The ordered step list is walked
/// front to back: settled steps (completed, skipped, or already running) are passed over, and
/// the first unsettled step is the <b>cursor</b>, the only step that may trigger this
/// evaluation. Its activation rule is a predicate over the nearest earlier <i>called</i> step's
/// observed GSX state (<c>AfterAllCompleted</c> scans everything earlier), so concurrency is
/// per-service: an <c>AfterCalled</c> chain dispatches one confirmed call after another a few
/// seconds apart while GSX runs them side by side — never a rapid-fire burst, which GSX
/// silently drops (round-7 smoke test: five simultaneous triggers, only Cleaning ran).
/// <c>Manual</c> parks the cursor until the service is called externally or forced (INT/RAD /
/// web); <c>forceNext</c> bypasses the activation rule — but never the flight-plan gate.
/// </summary>
public static class DepartureSequencer
{
    private const string PlanHoldReason = "waiting for a flight plan (SimBrief OFP import or MCDU FMS plan)";

    public static DeparturePlan Next(
        IReadOnlyList<DepartureServiceStep> steps,
        IReadOnlyDictionary<string, GsxServiceInfo> services,
        Func<string, DepartureCycleView> cycle,
        string? awaitingConfirmation,
        bool flightPlanAvailable,
        bool requireOfp,
        bool isTurnaround,
        bool forceNext)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(cycle);

        // Defensive: a duplicated order (mis-merged settings) must never double-run a service.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = steps
            .Where(s => !string.IsNullOrWhiteSpace(s.Service) && seen.Add(s.Service))
            .ToList();

        var holds = new List<(string, string)>();
        var skipped = new List<(string, string)>();
        string? trigger = null;
        string? triggerReason = null;

        // "Previous service" for the activation predicates: the nearest earlier step that was
        // actually called (skipped steps are transparent — legacy `IsSkipped == true` escape).
        Previous? prev = null;
        var allEarlierSettled = true; // every earlier step completed or skipped (the barrier)
        var settled = 0;
        var cursorSeen = false;

        foreach (var step in ordered)
        {
            var id = step.Service;

            if (step.Activation == GsxServiceActivation.Skip)
            {
                skipped.Add((id, "activation is Skip"));
                settled++;
                continue;
            }

            if (step.Constraint == GsxServiceConstraint.FirstLeg && isTurnaround)
            {
                skipped.Add((id, "first-leg only (this is a turnaround)"));
                settled++;
                continue;
            }

            if (step.Constraint == GsxServiceConstraint.TurnAround && !isTurnaround)
            {
                skipped.Add((id, "turnaround only (this is the first leg)"));
                settled++;
                continue;
            }

            if (!services.TryGetValue(id, out var info))
            {
                skipped.Add((id, "not offered by GSX for this aircraft/gate"));
                settled++;
                continue;
            }

            if (info.State == GsxServiceState.NotAvailable)
            {
                skipped.Add((id, "unavailable"));
                settled++;
                continue;
            }

            if (info.State == GsxServiceState.Bypassed)
            {
                skipped.Add((id, "bypassed"));
                settled++;
                continue;
            }

            var view = cycle(id);
            if (view.Completed)
            {
                settled++;
                prev = Previous.Done;
                continue;
            }

            // Running or confirmed-called (by us, or externally via the GSX menu/EFB): the
            // cursor moves past it — its cycle finishes on its own; later steps gate on it.
            var mirrorRunning = info.State is GsxServiceState.Requested or GsxServiceState.Active or GsxServiceState.Completed;
            if (mirrorRunning || (view.Called && !view.Completed))
            {
                prev = new Previous(
                    ReachedRequested: view.ReachedRequested || mirrorRunning,
                    ReachedActive: view.ReachedActive || info.State is GsxServiceState.Active or GsxServiceState.Completed,
                    Completed: false);
                allEarlierSettled = false;
                continue;
            }

            if (cursorSeen)
            {
                continue; // a step already owns the turn; the rest wait behind it
            }
            cursorSeen = true;

            // A trigger for this service is in flight and GSX has not visibly picked it up yet
            // — hold here; the dispatcher confirms or times out and retries.
            if (awaitingConfirmation is not null && awaitingConfirmation.Equals(id, StringComparison.OrdinalIgnoreCase))
            {
                holds.Add((id, "trigger sent — waiting for GSX to confirm"));
                continue;
            }

            if (requireOfp && !flightPlanAvailable)
            {
                holds.Add((id, PlanHoldReason));
                continue;
            }

            var gate = EvaluateActivation(step.Activation, prev, allEarlierSettled, forceNext);
            if (gate is not null)
            {
                holds.Add((id, gate));
                continue;
            }

            if (info.State != GsxServiceState.Callable)
            {
                holds.Add((id, $"state {info.State} — waiting"));
                continue;
            }

            if (!info.CanTrigger)
            {
                holds.Add((id, "callable but not triggerable yet"));
                continue;
            }

            trigger = id;
            triggerReason = forceNext
                ? "forced ahead of its activation rule (INT/RAD / web)"
                : $"next in departure order ({step.Activation})";
        }

        return new DeparturePlan(trigger, triggerReason, holds, skipped, settled == ordered.Count);
    }

    /// <summary>Null = the activation rule is satisfied; otherwise the hold reason. The first
    /// called step has no previous service and is always satisfied (except Manual).</summary>
    private static string? EvaluateActivation(
        GsxServiceActivation activation,
        Previous? prev,
        bool allEarlierSettled,
        bool forceNext)
    {
        if (forceNext)
        {
            return null;
        }

        return activation switch
        {
            GsxServiceActivation.Manual => "waiting for a manual call (INT/RAD, web button, or the GSX menu)",
            GsxServiceActivation.AfterRequested when prev is { ReachedRequested: false, Completed: false }
                => "waiting for the previous service to be requested",
            GsxServiceActivation.AfterActive when prev is { ReachedActive: false, Completed: false }
                => "waiting for the previous service to become active",
            GsxServiceActivation.AfterPrevCompleted when prev is { Completed: false }
                => "waiting for the previous service to complete",
            GsxServiceActivation.AfterAllCompleted when !allEarlierSettled
                => "waiting for all earlier services to complete",
            _ => null,
        };
    }

    private readonly record struct Previous(bool ReachedRequested, bool ReachedActive, bool Completed)
    {
        public static Previous Done { get; } = new(true, true, true);
    }
}
