using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Gsx.Automation;

/// <summary>
/// "A new plan is a new leg" (owner rule 2026-11-01, EHAM): when a different SimBrief plan
/// lands while the aircraft is parked with the engines off, the departure side starts over —
/// the service cycles, the pax target, the tracking memory — so boarding and catering run for
/// the new leg whatever the previous one left behind. Guards: never while a departure service
/// for the current plan is already under way (a late re-import mid-boarding must not reset a
/// running turnaround), never for the same plan twice, never away from the stand. Pure.
/// </summary>
public static class NewPlanCycleReset
{
    /// <summary>Services that are the arrival's, never the departure's: left alone by the reset.</summary>
    public static readonly IReadOnlySet<string> ArrivalSide =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { GsxServiceIds.Deboarding, GsxServiceIds.Gpu, GsxServiceIds.OperateJetways };

    /// <summary>The departure one-shots whose tracking memory the reset clears.</summary>
    public static readonly IReadOnlyList<string> DepartureServices =
    [
        GsxServiceIds.Refueling, GsxServiceIds.Catering, GsxServiceIds.Boarding,
        GsxServiceIds.Water, GsxServiceIds.Lavatory, GsxServiceIds.Cleaning, GsxServiceIds.DeIce,
    ];

    /// <summary>The plan's identity: the SimBrief request when it has one, else the route.</summary>
    public static string? PlanId(OfpData? ofp)
    {
        if (ofp is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(ofp.RequestId))
        {
            return "req:" + ofp.RequestId.Trim();
        }

        return string.IsNullOrWhiteSpace(ofp.OriginIcao) || string.IsNullOrWhiteSpace(ofp.DestinationIcao)
            ? null
            : $"route:{ofp.OriginIcao.Trim().ToUpperInvariant()}-{ofp.DestinationIcao.Trim().ToUpperInvariant()}";
    }

    /// <summary>Whether the plan that just landed starts a new leg. <paramref name="lastResetPlanId"/>
    /// is the plan the last reset ran for (or the plan the cycle was started with), so the same
    /// plan never resets twice; <paramref name="observedProgress"/> is true when a departure
    /// service outside the arrival side was really called, active or completed this cycle.</summary>
    public static bool ShouldReset(
        OfpData? current,
        string? lastResetPlanId,
        bool cycleComplete,
        bool observedProgress,
        FlightDataSnapshot? data)
    {
        var id = PlanId(current);
        if (id is null || string.Equals(id, lastResetPlanId, StringComparison.Ordinal))
        {
            return false;
        }

        if (data is not { IsValid: true, OnGround: true, AnyEngineRunning: false } || data.GroundSpeedKt >= 1)
        {
            return false;
        }

        // A cycle that is complete is over whatever it did; an open one is reset only while
        // nothing of the departure has begun for the plan it was opened with.
        return cycleComplete || !observedProgress;
    }
}
