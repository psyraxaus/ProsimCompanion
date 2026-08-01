using ProsimCompanion.Gsx.Mirror;
using ProsimCompanion.Gsx.Protocol;

namespace ProsimCompanion.Gsx.Automation;

public enum DepartureDecisionKind
{
    /// <summary>Trigger <see cref="DepartureDecision.ServiceId"/> now.</summary>
    Trigger,

    /// <summary>Waiting on <see cref="DepartureDecision.ServiceId"/> (in progress or blocked).</summary>
    Hold,

    /// <summary>Every ordered service is completed or skipped.</summary>
    AllDone,
}

/// <summary>One pass's outcome, plus any services skipped along the way (unavailable/bypassed/
/// not offered) with their reasons — skips feed the decision log, not control flow.</summary>
public sealed record DepartureDecision(
    DepartureDecisionKind Kind,
    string? ServiceId,
    string Reason,
    IReadOnlyList<(string ServiceId, string Reason)> Skipped);

/// <summary>
/// Pure, one-service-at-a-time departure sequencing: walk the configured order; completed and
/// unavailable services pass through; the first service still in progress or blocked holds the
/// sequence; the first callable+triggerable service is the one to trigger. Refueling/Boarding
/// are held until the OFP is imported when required (the predecessors' OFP gating).
/// </summary>
public static class DepartureSequencer
{
    private static readonly HashSet<string> OfpGatedServices =
        new(StringComparer.OrdinalIgnoreCase) { "Refueling", "Boarding" };

    public static DepartureDecision Next(
        IReadOnlyList<string> order,
        IReadOnlyDictionary<string, GsxServiceInfo> services,
        Func<string, bool> isCompleted,
        bool flightPlanAvailable,
        bool requireOfp)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(isCompleted);

        var skipped = new List<(string, string)>();

        foreach (var serviceId in order)
        {
            if (isCompleted(serviceId))
            {
                continue;
            }

            if (!services.TryGetValue(serviceId, out var service))
            {
                skipped.Add((serviceId, "not offered by GSX for this aircraft/gate"));
                continue;
            }

            switch (service.State)
            {
                case GsxServiceState.NotAvailable:
                    skipped.Add((serviceId, "unavailable"));
                    continue;

                case GsxServiceState.Bypassed:
                    skipped.Add((serviceId, "bypassed"));
                    continue;

                case GsxServiceState.Requested or GsxServiceState.Active:
                    return new(DepartureDecisionKind.Hold, serviceId, "in progress", skipped);

                case GsxServiceState.Callable:
                    if (requireOfp && !flightPlanAvailable && OfpGatedServices.Contains(serviceId))
                    {
                        return new(DepartureDecisionKind.Hold, serviceId, "waiting for a flight plan (SimBrief OFP import or MCDU FMS plan)", skipped);
                    }

                    if (!service.CanTrigger)
                    {
                        return new(DepartureDecisionKind.Hold, serviceId, "callable but not triggerable yet", skipped);
                    }

                    return new(DepartureDecisionKind.Trigger, serviceId, "next in departure order", skipped);

                default:
                    return new(DepartureDecisionKind.Hold, serviceId, $"state {service.State} — waiting", skipped);
            }
        }

        return new(DepartureDecisionKind.AllDone, null, "all departure services completed or skipped", skipped);
    }
}
