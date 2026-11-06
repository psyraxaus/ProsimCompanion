using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Gsx.Automation;

/// <summary>
/// "A new plan is a new leg" (owner rule 2026-11-01, EHAM): when a different SimBrief plan
/// lands while the aircraft is parked with the engines off, the departure side starts over —
/// the service cycles, the pax target, the tracking memory — so boarding and catering run for
/// the new leg whatever the previous one left behind. Guards: never once BOARDING for the
/// current plan has started (a late re-import mid-boarding must not reset a running
/// turnaround — ADR-0019 §6, issue #213: refuel or catering already run for the old plan do
/// not hold the old leg, they are simply re-called for the new one), never for the same plan
/// twice, never away from the stand. Pure.
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
    public static string? PlanId(OfpData? ofp) => OfpPlanIdentity.PlanId(ofp);

    /// <summary>What a plan landing means for the departure cycle. <see cref="Adopt"/> is the
    /// first plan of a cycle that had none (EGLL 2026-11-06: the crew said "commence ground
    /// services" before any OFP, the automation reloaded SimBrief two minutes later, and the
    /// plan was read as a NEW LEG — the cycle was reset mid-run, its prep flag cleared, and the
    /// beacon pushback never armed for the rest of the flight). A first plan is the cycle's
    /// baseline, never a reset.</summary>
    public enum NewPlanDecision
    {
        None,
        Adopt,
        Reset,
    }

    /// <summary>The full decision; <see cref="ShouldReset"/> is its Reset case.</summary>
    public static NewPlanDecision Decide(
        OfpData? current,
        string? lastResetPlanId,
        bool boardingObserved,
        FlightDataSnapshot? data)
    {
        var id = PlanId(current);
        if (id is null)
        {
            return NewPlanDecision.None;
        }

        if (lastResetPlanId is null)
        {
            return NewPlanDecision.Adopt;
        }

        if (string.Equals(id, lastResetPlanId, StringComparison.Ordinal))
        {
            return NewPlanDecision.None;
        }

        if (data is not { IsValid: true, OnGround: true, AnyEngineRunning: false } || data.GroundSpeedKt >= 1)
        {
            return NewPlanDecision.None;
        }

        return boardingObserved ? NewPlanDecision.None : NewPlanDecision.Reset;
    }

    /// <summary>Whether the plan that just landed starts a new leg. <paramref name="lastResetPlanId"/>
    /// is the plan the last reset ran for (or the plan the cycle was started with), so the same
    /// plan never resets twice; <paramref name="boardingObserved"/> is true when GSX Boarding
    /// was really called, active or completed this cycle (a startup-resync seed does not count).
    /// Boarding is the bar, full stop (ADR-0019 §6): a regenerated OFP after boarding — the
    /// cycle complete or not — never resets a boarded aircraft's pax target, latches, gate
    /// monitor, walkaround or loadsheet editions (review 2026-11-05; "cycle complete" used to
    /// override the bar).</summary>
    public static bool ShouldReset(
        OfpData? current,
        string? lastResetPlanId,
        bool boardingObserved,
        FlightDataSnapshot? data)
        => Decide(current, lastResetPlanId, boardingObserved, data) == NewPlanDecision.Reset;
}
