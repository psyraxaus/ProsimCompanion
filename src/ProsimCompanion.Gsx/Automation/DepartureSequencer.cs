using ProsimCompanion.Gsx.Mirror;
using ProsimCompanion.Gsx.Protocol;

namespace ProsimCompanion.Gsx.Automation;

/// <summary>One evaluation's outcome: services to trigger now, services holding (with reasons),
/// services skipped (unavailable/bypassed/not offered), and overall completion.</summary>
public sealed record DeparturePlan(
    IReadOnlyList<string> Trigger,
    IReadOnlyList<(string ServiceId, string Reason)> Holds,
    IReadOnlyList<(string ServiceId, string Reason)> Skipped,
    bool AllDone);

/// <summary>
/// Pure departure sequencing. Two modes: <b>concurrent</b> (every non-boarding service is
/// triggered as soon as it is callable — refuel and catering side by side) or strict
/// one-at-a-time in order. Boarding always waits for its prerequisites: the configured
/// <c>boardingAfter</c> services, or every other ordered service when unset (the classic
/// board-last). No service is called before a flight plan exists (when required), and a
/// service that has already been called holds until its cycle completes — GSX keeps quick
/// services "callable" while they run, so without that guard they re-trigger forever
/// (round-4 smoke test: Water called on every pump).
/// </summary>
public static class DepartureSequencer
{
    private const string BoardingId = "Boarding";
    private const string PlanHoldReason = "waiting for a flight plan (SimBrief OFP import or MCDU FMS plan)";
    private const string PendingHoldReason = "already called — waiting for GSX to run it";

    public static DeparturePlan Next(
        IReadOnlyList<string> order,
        IReadOnlyDictionary<string, GsxServiceInfo> services,
        Func<string, bool> isCompleted,
        Func<string, bool> isPending,
        bool flightPlanAvailable,
        bool requireOfp,
        bool concurrentServices,
        IReadOnlyList<string> boardingAfter)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(isCompleted);
        ArgumentNullException.ThrowIfNull(isPending);
        ArgumentNullException.ThrowIfNull(boardingAfter);

        // Defensive: a duplicated order (mis-merged settings) must never double-trigger.
        order = order.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var trigger = new List<string>();
        var holds = new List<(string, string)>();
        var skipped = new List<(string, string)>();
        var sequenceBlocked = false;

        foreach (var serviceId in order)
        {
            if (serviceId.Equals(BoardingId, StringComparison.OrdinalIgnoreCase) || isCompleted(serviceId))
            {
                continue;
            }

            if (IsSkippable(serviceId, services, out var skipReason))
            {
                skipped.Add((serviceId, skipReason));
                continue;
            }

            if (sequenceBlocked)
            {
                continue; // sequential mode: an earlier service owns the turn
            }

            var reason = EvaluateService(services[serviceId], isPending(serviceId), flightPlanAvailable, requireOfp);
            if (reason is null)
            {
                trigger.Add(serviceId);
                if (!concurrentServices)
                {
                    sequenceBlocked = true;
                }
            }
            else
            {
                holds.Add((serviceId, reason));
                if (!concurrentServices)
                {
                    sequenceBlocked = true;
                }
            }
        }

        EvaluateBoarding(order, services, isCompleted, isPending, flightPlanAvailable, requireOfp, boardingAfter, trigger, holds, skipped);

        var allDone = order.All(id =>
            isCompleted(id) || IsSkippable(id, services, out _));

        return new DeparturePlan(trigger, holds, skipped, allDone);
    }

    /// <summary>Null = triggerable now; otherwise the hold reason.</summary>
    private static string? EvaluateService(
        GsxServiceInfo service,
        bool pending,
        bool flightPlanAvailable,
        bool requireOfp)
    {
        return service.State switch
        {
            GsxServiceState.Requested or GsxServiceState.Active => "in progress",
            GsxServiceState.Callable when requireOfp && !flightPlanAvailable => PlanHoldReason,
            GsxServiceState.Callable when pending => PendingHoldReason,
            GsxServiceState.Callable when !service.CanTrigger => "callable but not triggerable yet",
            GsxServiceState.Callable => null,
            _ => $"state {service.State} — waiting",
        };
    }

    private static void EvaluateBoarding(
        IReadOnlyList<string> order,
        IReadOnlyDictionary<string, GsxServiceInfo> services,
        Func<string, bool> isCompleted,
        Func<string, bool> isPending,
        bool flightPlanAvailable,
        bool requireOfp,
        IReadOnlyList<string> boardingAfter,
        List<string> trigger,
        List<(string, string)> holds,
        List<(string, string)> skipped)
    {
        if (!order.Contains(BoardingId, StringComparer.OrdinalIgnoreCase) || isCompleted(BoardingId))
        {
            return;
        }

        if (IsSkippable(BoardingId, services, out var skipReason))
        {
            skipped.Add((BoardingId, skipReason));
            return;
        }

        // Prerequisites: the configured list, or every other ordered service. A prerequisite
        // that is itself skippable (unavailable/not offered) counts as satisfied.
        IReadOnlyList<string> prerequisites = boardingAfter.Count > 0
            ? boardingAfter.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : [.. order.Where(id => !id.Equals(BoardingId, StringComparison.OrdinalIgnoreCase))];
        var outstanding = prerequisites
            .Where(id => !isCompleted(id) && !IsSkippable(id, services, out _))
            .ToList();
        if (outstanding.Count > 0)
        {
            holds.Add((BoardingId, $"waiting for {string.Join(", ", outstanding)}"));
            return;
        }

        var reason = EvaluateService(services[BoardingId], isPending(BoardingId), flightPlanAvailable, requireOfp);
        if (reason is null)
        {
            trigger.Add(BoardingId);
        }
        else
        {
            holds.Add((BoardingId, reason));
        }
    }

    private static bool IsSkippable(
        string serviceId,
        IReadOnlyDictionary<string, GsxServiceInfo> services,
        out string reason)
    {
        if (!services.TryGetValue(serviceId, out var service))
        {
            reason = "not offered by GSX for this aircraft/gate";
            return true;
        }

        switch (service.State)
        {
            case GsxServiceState.NotAvailable:
                reason = "unavailable";
                return true;
            case GsxServiceState.Bypassed:
                reason = "bypassed";
                return true;
            default:
                reason = "";
                return false;
        }
    }
}
