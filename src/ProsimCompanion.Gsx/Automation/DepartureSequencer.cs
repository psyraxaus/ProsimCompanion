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
/// board-last). Refueling and Boarding additionally hold until a flight plan is available.
/// </summary>
public static class DepartureSequencer
{
    private const string BoardingId = "Boarding";

    private static readonly HashSet<string> PlanGatedServices =
        new(StringComparer.OrdinalIgnoreCase) { "Refueling", BoardingId };

    public static DeparturePlan Next(
        IReadOnlyList<string> order,
        IReadOnlyDictionary<string, GsxServiceInfo> services,
        Func<string, bool> isCompleted,
        bool flightPlanAvailable,
        bool requireOfp,
        bool concurrentServices,
        IReadOnlyList<string> boardingAfter)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(isCompleted);
        ArgumentNullException.ThrowIfNull(boardingAfter);

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

            var reason = EvaluateService(serviceId, services[serviceId], flightPlanAvailable, requireOfp);
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

        EvaluateBoarding(order, services, isCompleted, flightPlanAvailable, requireOfp, boardingAfter, trigger, holds, skipped);

        var allDone = order.All(id =>
            isCompleted(id) || IsSkippable(id, services, out _));

        return new DeparturePlan(trigger, holds, skipped, allDone);
    }

    /// <summary>Null = triggerable now; otherwise the hold reason.</summary>
    private static string? EvaluateService(
        string serviceId,
        GsxServiceInfo service,
        bool flightPlanAvailable,
        bool requireOfp)
    {
        return service.State switch
        {
            GsxServiceState.Requested or GsxServiceState.Active => "in progress",
            GsxServiceState.Callable when requireOfp && !flightPlanAvailable && PlanGatedServices.Contains(serviceId)
                => "waiting for a flight plan (SimBrief OFP import or MCDU FMS plan)",
            GsxServiceState.Callable when !service.CanTrigger => "callable but not triggerable yet",
            GsxServiceState.Callable => null,
            _ => $"state {service.State} — waiting",
        };
    }

    private static void EvaluateBoarding(
        IReadOnlyList<string> order,
        IReadOnlyDictionary<string, GsxServiceInfo> services,
        Func<string, bool> isCompleted,
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
            ? boardingAfter
            : [.. order.Where(id => !id.Equals(BoardingId, StringComparison.OrdinalIgnoreCase))];
        var outstanding = prerequisites
            .Where(id => !isCompleted(id) && !IsSkippable(id, services, out _))
            .ToList();
        if (outstanding.Count > 0)
        {
            holds.Add((BoardingId, $"waiting for {string.Join(", ", outstanding)}"));
            return;
        }

        var reason = EvaluateService(BoardingId, services[BoardingId], flightPlanAvailable, requireOfp);
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
