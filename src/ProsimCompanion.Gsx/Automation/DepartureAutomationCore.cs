using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Gsx.Mirror;

namespace ProsimCompanion.Gsx.Automation;

/// <summary>
/// The departure pump's decision function, pure (campaign #78): every gate, in the exact
/// owner-specified order, with no I/O — the shell (<see cref="GsxAutomationService"/>) only
/// gathers inputs and performs the outcome's effects. The gate ORDER is the safety-critical
/// part (resync before sequencing #30, prep before services, plan gate never bypassed by
/// force), so it lives here where a table test can pin it.
/// </summary>
internal static class DepartureAutomationCore
{
    internal sealed record PumpInputs(
        bool AutomationEnabled,
        bool GsxReady,
        bool ResyncAssessed,
        bool AutoStartOption,
        GsxAutomationPhase Phase,
        bool CycleStarted,
        bool CycleComplete,
        bool PrepComplete,
        bool RequireOfp,
        bool FmsPlanPresent,
        bool OfpImported,
        bool FlightPlanAvailable,
        string? FmsOrigin,
        string? FmsDestination,
        bool Forced,
        string? InFlightServiceId,
        bool IsTurnaround,
        bool IsCompanyHub,
        TimeSpan? EstimatedEnroute,
        IReadOnlyList<DepartureServiceStep> Steps,
        IReadOnlyDictionary<string, GsxServiceInfo> MirrorServices,
        Func<string, DepartureCycleView> Cycle,
        Func<string, string?>? PreSkip = null);

    /// <summary>What one evaluation decided. Effects run in the shell, in this order:
    /// auto-start decision → waiting board (stop) → hold decision → import/diagnostic/pax →
    /// board publish + skip/hold decisions → trigger dispatch → all-done.</summary>
    internal sealed record PumpOutcome(
        bool AutoStarted,
        string? WaitingBoard,
        string? HoldReason,
        bool StartSimbriefImport,
        string? PlanDiagnostic,
        bool ArmPaxTarget,
        DeparturePlan? Plan,
        bool ConsumedForce,
        string? Trigger,
        bool AllDone)
    {
        internal static readonly PumpOutcome Idle = new(
            false, null, null, false, null, false, null, false, null, false);
    }

    internal static PumpOutcome Evaluate(PumpInputs inputs)
    {
        if (!inputs.AutomationEnabled || !inputs.GsxReady)
        {
            return PumpOutcome.Idle;
        }

        // Never sequence before the startup resync has assessed prior progress (#30) — the
        // assessment is terminal (it times out if GSX/ProSim never come up), so the hold is
        // bounded.
        if (!inputs.ResyncAssessed)
        {
            return PumpOutcome.Idle with { WaitingBoard = "waiting for the startup state resync" };
        }

        var autoStarted = !inputs.CycleStarted
            && inputs.AutoStartOption
            && inputs.Phase == GsxAutomationPhase.Preparation;
        var started = inputs.CycleStarted || autoStarted;

        if (!started || inputs.CycleComplete
            || inputs.Phase is not (GsxAutomationPhase.Preparation or GsxAutomationPhase.SessionStart))
        {
            return PumpOutcome.Idle with
            {
                AutoStarted = autoStarted,
                WaitingBoard = inputs.CycleComplete ? null : "departure sequence not started",
            };
        }

        // Order (owner-specified): reposition → GPU/chocks → jetway/stairs must all finish
        // before any departure service is called.
        if (!inputs.PrepComplete)
        {
            return PumpOutcome.Idle with
            {
                AutoStarted = autoStarted,
                WaitingBoard = "ground preparation running",
                HoldReason = "waiting for ground preparation to complete",
            };
        }

        // Flight plan = SimBrief OFP imported into the EFB, OR a plan the PILOT loaded in the
        // MCDU. The SimBrief importer only runs AFTER the MCDU plan is detected — never on its
        // own (round-5 smoke test: the auto-import satisfied the plan gate two seconds after
        // ground prep, before the pilot had loaded anything).
        var startImport = inputs.FmsPlanPresent && !inputs.OfpImported && inputs.RequireOfp;
        var planDiagnostic = !inputs.FlightPlanAvailable && inputs.RequireOfp
            ? "none detected — simbriefImported=" + inputs.OfpImported
                + $", fmsOrigin='{inputs.FmsOrigin ?? ""}', fmsDestination='{inputs.FmsDestination ?? ""}'"
            : null;

        var plan = DepartureSequencer.Next(
            inputs.Steps,
            inputs.MirrorServices,
            inputs.Cycle,
            inputs.InFlightServiceId,
            inputs.FlightPlanAvailable,
            inputs.RequireOfp,
            inputs.IsTurnaround,
            inputs.Forced,
            inputs.IsCompanyHub,
            inputs.EstimatedEnroute,
            inputs.PreSkip);

        return new PumpOutcome(
            AutoStarted: autoStarted,
            WaitingBoard: null,
            HoldReason: null,
            StartSimbriefImport: startImport,
            PlanDiagnostic: planDiagnostic,
            ArmPaxTarget: inputs.FlightPlanAvailable,
            Plan: plan,
            ConsumedForce: inputs.Forced,
            Trigger: inputs.InFlightServiceId is null ? plan.Trigger : null,
            AllDone: plan.AllDone);
    }
}
