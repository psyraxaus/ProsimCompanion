using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Gsx.Automation;
using ProsimCompanion.Gsx.Mirror;
using ProsimCompanion.Gsx.Protocol;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

public sealed class DepartureSequencerTests
{
    private static readonly DepartureCycleView CalledOnly = new(true, false, false, false);
    private static readonly DepartureCycleView CalledRequested = new(true, true, false, false);
    private static readonly DepartureCycleView CalledActive = new(true, true, true, false);
    private static readonly DepartureCycleView Done = new(true, true, true, true);

    private static DepartureServiceStep Step(
        string id,
        GsxServiceActivation activation = GsxServiceActivation.AfterCalled,
        GsxServiceConstraint constraint = GsxServiceConstraint.Always,
        int minimumFlightMinutes = 0)
        => new(id, activation, constraint) { MinimumFlightMinutes = minimumFlightMinutes };

    private static Dictionary<string, GsxServiceInfo> Services(
        params (string Id, GsxServiceState State, bool CanTrigger)[] entries)
        => entries.ToDictionary(
            e => e.Id,
            e => new GsxServiceInfo(e.Id, e.Id, null, e.State, e.CanTrigger, false, null, null),
            StringComparer.OrdinalIgnoreCase);

    private static DeparturePlan Next(
        IReadOnlyList<DepartureServiceStep> steps,
        Dictionary<string, GsxServiceInfo> services,
        Dictionary<string, DepartureCycleView>? cycles = null,
        string? awaitingConfirmation = null,
        bool plan = true,
        bool requireOfp = true,
        bool turnaround = false,
        bool force = false,
        bool companyHub = false,
        TimeSpan? flightDuration = null,
        Func<string, string?>? preSkip = null)
        => DepartureSequencer.Next(
            steps,
            services,
            id => cycles?.GetValueOrDefault(id) ?? default,
            awaitingConfirmation,
            plan,
            requireOfp,
            turnaround,
            force,
            companyHub,
            flightDuration,
            preSkip);

    // ---- Situational pre-skip (issue #117: tankering) ----

    [Fact]
    public void PreSkip_RetiresTheStep_AndTheNextStepTakesTheTurn()
    {
        var steps = new[] { Step("Refueling"), Step("Catering"), Step("Boarding", GsxServiceActivation.AfterAllCompleted) };
        var services = Services(
            ("Refueling", GsxServiceState.Callable, true),
            ("Catering", GsxServiceState.Callable, true),
            ("Boarding", GsxServiceState.Callable, true));

        var plan = Next(steps, services, preSkip: id => id == "Refueling" ? "FOB covers the plan (tankering)" : null);

        Assert.Equal("Catering", plan.Trigger);
        Assert.Contains(plan.Skipped, s => s.ServiceId == "Refueling" && s.Reason.Contains("tankering"));
        Assert.DoesNotContain(plan.Holds, h => h.ServiceId == "Refueling");
    }

    [Fact]
    public void PreSkip_CountsAsSettled_ForAfterAllCompleted()
    {
        var steps = new[] { Step("Refueling"), Step("Boarding", GsxServiceActivation.AfterAllCompleted) };
        var services = Services(
            ("Refueling", GsxServiceState.Callable, true),
            ("Boarding", GsxServiceState.Callable, true));

        var plan = Next(steps, services, preSkip: id => id == "Refueling" ? "tankering" : null);

        Assert.Equal("Boarding", plan.Trigger);
    }

    [Fact]
    public void PreSkip_NeverTouchesAServiceAlreadyRunning()
    {
        var steps = new[] { Step("Refueling"), Step("Catering") };
        var services = Services(
            ("Refueling", GsxServiceState.Active, false),
            ("Catering", GsxServiceState.Callable, true));

        var plan = Next(steps, services, preSkip: id => id == "Refueling" ? "tankering" : null);

        Assert.DoesNotContain(plan.Skipped, s => s.ServiceId == "Refueling");
        Assert.Equal("Catering", plan.Trigger);
    }

    [Fact]
    public void PreSkip_DoesNotBypassTheFlightPlanGate_WhenItAnswersNull()
    {
        var steps = new[] { Step("Refueling") };
        var services = Services(("Refueling", GsxServiceState.Callable, true));

        var plan = Next(steps, services, plan: false, preSkip: _ => null);

        Assert.Null(plan.Trigger);
        Assert.Contains(plan.Holds, h => h.ServiceId == "Refueling" && h.Reason.Contains("flight plan"));
    }

    // ---- Voice activation (ADR-0006 / issue #50) ----

    [Fact]
    public void VoiceActivation_ParksTheCursor_WithASpokenHoldReason()
    {
        var steps = new[] { Step("Refueling", GsxServiceActivation.Voice), Step("Catering") };
        var services = Services(
            ("Refueling", GsxServiceState.Callable, true), ("Catering", GsxServiceState.Callable, true));

        var plan = Next(steps, services);

        Assert.Null(plan.Trigger);
        Assert.Contains(plan.Holds, h => h.ServiceId == "Refueling" && h.Reason.Contains("say 'request"));
    }

    [Fact]
    public void VoiceActivation_ForceNext_BypassesTheHold()
    {
        // Voice is an additional trigger, never the only one — INT/RAD or the web button
        // must advance a Voice step exactly like a Manual one.
        var steps = new[] { Step("Refueling", GsxServiceActivation.Voice) };
        var services = Services(("Refueling", GsxServiceState.Callable, true));

        var plan = Next(steps, services, force: true);

        Assert.Equal("Refueling", plan.Trigger);
    }

    [Fact]
    public void VoiceActivation_ExternallyCalledService_GatesLaterStepsLikeManual()
    {
        // The pilot voice-requested refueling (on-demand path → mirror shows Requested):
        // the cursor moves past it and the next step evaluates normally.
        var steps = new[] { Step("Refueling", GsxServiceActivation.Voice), Step("Catering") };
        var services = Services(
            ("Refueling", GsxServiceState.Requested, false), ("Catering", GsxServiceState.Callable, true));

        var plan = Next(steps, services);

        Assert.Equal("Catering", plan.Trigger);
    }

    // ---- Minimum flight time (Prosim2GSX "Min. Flight Time" parity) ----

    [Fact]
    public void MinimumFlightTime_ShortHop_SkipsService_AndCursorMovesOn()
    {
        var steps = new[] { Step("Catering", minimumFlightMinutes: 60), Step("Refueling") };
        var services = Services(
            ("Catering", GsxServiceState.Callable, true), ("Refueling", GsxServiceState.Callable, true));

        var plan = Next(steps, services, flightDuration: TimeSpan.FromMinutes(30));

        Assert.Equal("Refueling", plan.Trigger);
        Assert.Contains(plan.Skipped, s => s.ServiceId == "Catering" && s.Reason.Contains("minimum"));
    }

    [Fact]
    public void MinimumFlightTime_LongEnoughFlight_RunsService()
    {
        var steps = new[] { Step("Catering", minimumFlightMinutes: 60) };
        var services = Services(("Catering", GsxServiceState.Callable, true));

        var plan = Next(steps, services, flightDuration: TimeSpan.FromMinutes(90));

        Assert.Equal("Catering", plan.Trigger);
    }

    [Fact]
    public void MinimumFlightTime_ExactlyAtThreshold_RunsService()
    {
        var steps = new[] { Step("Catering", minimumFlightMinutes: 60) };
        var services = Services(("Catering", GsxServiceState.Callable, true));

        var plan = Next(steps, services, flightDuration: TimeSpan.FromMinutes(60));

        Assert.Equal("Catering", plan.Trigger);
    }

    [Fact]
    public void MinimumFlightTime_UnknownDuration_NeverSkips()
    {
        var steps = new[] { Step("Catering", minimumFlightMinutes: 60) };
        var services = Services(("Catering", GsxServiceState.Callable, true));

        var plan = Next(steps, services, flightDuration: null);

        Assert.Equal("Catering", plan.Trigger);
    }

    [Fact]
    public void MinimumFlightTime_SkippedServiceCountsAsSettled_ForCompletion()
    {
        var steps = new[] { Step("Catering", minimumFlightMinutes: 60) };
        var services = Services(("Catering", GsxServiceState.Callable, true));

        var plan = Next(steps, services, flightDuration: TimeSpan.FromMinutes(10));

        Assert.Null(plan.Trigger);
        Assert.True(plan.AllDone);
    }

    // ---- Company-hub constraints (Prosim2GSX parity) ----

    [Fact]
    public void CompanyHubConstraint_SkipsAwayFromHub_AndRunsAtHub()
    {
        var steps = new[] { Step("Catering", constraint: GsxServiceConstraint.CompanyHub), Step("Refueling") };
        var services = Services(
            ("Catering", GsxServiceState.Callable, true), ("Refueling", GsxServiceState.Callable, true));

        var awayPlan = Next(steps, services, companyHub: false);
        Assert.Equal("Refueling", awayPlan.Trigger); // Catering skipped transparently

        var hubPlan = Next(steps, services, companyHub: true);
        Assert.Equal("Catering", hubPlan.Trigger);
    }

    [Fact]
    public void NonCompanyHubConstraint_SkipsAtHub()
    {
        var steps = new[] { Step("Catering", constraint: GsxServiceConstraint.NonCompanyHub), Step("Refueling") };
        var services = Services(
            ("Catering", GsxServiceState.Callable, true), ("Refueling", GsxServiceState.Callable, true));

        var hubPlan = Next(steps, services, companyHub: true);
        Assert.Equal("Refueling", hubPlan.Trigger);

        var awayPlan = Next(steps, services, companyHub: false);
        Assert.Equal("Catering", awayPlan.Trigger);
    }

    // ---- Single-dispatch discipline (the round-7 regression: rapid-fire triggers) ----

    [Fact]
    public void AfterCalledChain_TriggersOnlyTheFirstService()
    {
        // Both callable, nothing called yet: ONE trigger comes out, never a burst.
        var plan = Next(
            [Step("Refueling"), Step("Catering")],
            Services(("Refueling", GsxServiceState.Callable, true), ("Catering", GsxServiceState.Callable, true)));

        Assert.Equal("Refueling", plan.Trigger);
    }

    [Fact]
    public void AfterCalledChain_NextTriggersOnceThePreviousCallIsConfirmed()
    {
        // Refueling confirmed-called (still callable in the mirror — quick-service quirk):
        // the cursor moves past it and Catering goes out. Effective concurrency, serial dispatch.
        var plan = Next(
            [Step("Refueling"), Step("Catering")],
            Services(("Refueling", GsxServiceState.Callable, true), ("Catering", GsxServiceState.Callable, true)),
            cycles: new() { ["Refueling"] = CalledOnly });

        Assert.Equal("Catering", plan.Trigger);
    }

    [Fact]
    public void AwaitingConfirmation_HoldsTheCursorAndTriggersNothing()
    {
        var plan = Next(
            [Step("Refueling"), Step("Catering")],
            Services(("Refueling", GsxServiceState.Callable, true), ("Catering", GsxServiceState.Callable, true)),
            awaitingConfirmation: "Refueling");

        Assert.Null(plan.Trigger);
        Assert.Contains(plan.Holds, h => h.ServiceId == "Refueling" && h.Reason.Contains("waiting for GSX to confirm", StringComparison.Ordinal));
    }

    [Fact]
    public void CalledPendingService_IsNeverRetriggered()
    {
        // GSX keeps quick services "callable" while they run (round-4 smoke test: Water
        // re-triggered on every pump). A confirmed call must not fire again.
        var plan = Next(
            [Step("Water")],
            Services(("Water", GsxServiceState.Callable, true)),
            cycles: new() { ["Water"] = CalledRequested });

        Assert.Null(plan.Trigger);
    }

    // ---- Activation rules ----

    [Fact]
    public void AfterRequested_WaitsUntilThePreviousServiceIsRequested()
    {
        var services = Services(("Catering", GsxServiceState.Callable, true), ("Water", GsxServiceState.Callable, true));
        var steps = new[] { Step("Catering"), Step("Water", GsxServiceActivation.AfterRequested) };

        var waiting = Next(steps, services, cycles: new() { ["Catering"] = CalledOnly });
        Assert.Null(waiting.Trigger);
        Assert.Contains(waiting.Holds, h => h.ServiceId == "Water" && h.Reason.Contains("requested", StringComparison.Ordinal));

        var satisfied = Next(steps, services, cycles: new() { ["Catering"] = CalledRequested });
        Assert.Equal("Water", satisfied.Trigger);
    }

    [Fact]
    public void AfterRequested_MirrorStateCountsAsRequested()
    {
        // The previous service shows requested in the mirror (e.g. called externally).
        var plan = Next(
            [Step("Catering"), Step("Water", GsxServiceActivation.AfterRequested)],
            Services(("Catering", GsxServiceState.Requested, false), ("Water", GsxServiceState.Callable, true)));

        Assert.Equal("Water", plan.Trigger);
    }

    [Fact]
    public void AfterActive_WaitsUntilThePreviousServiceIsActive()
    {
        var steps = new[] { Step("Refueling"), Step("Catering", GsxServiceActivation.AfterActive) };

        var waiting = Next(
            steps,
            Services(("Refueling", GsxServiceState.Requested, false), ("Catering", GsxServiceState.Callable, true)));
        Assert.Null(waiting.Trigger);

        var satisfied = Next(
            steps,
            Services(("Refueling", GsxServiceState.Active, false), ("Catering", GsxServiceState.Callable, true)));
        Assert.Equal("Catering", satisfied.Trigger);
    }

    [Fact]
    public void AfterPrevCompleted_IsAStrictChain()
    {
        var steps = new[] { Step("Refueling"), Step("Catering", GsxServiceActivation.AfterPrevCompleted) };

        var waiting = Next(
            steps,
            Services(("Refueling", GsxServiceState.Active, false), ("Catering", GsxServiceState.Callable, true)));
        Assert.Null(waiting.Trigger);
        Assert.Contains(waiting.Holds, h => h.ServiceId == "Catering" && h.Reason.Contains("complete", StringComparison.Ordinal));

        var satisfied = Next(
            steps,
            Services(("Refueling", GsxServiceState.Callable, true), ("Catering", GsxServiceState.Callable, true)),
            cycles: new() { ["Refueling"] = Done });
        Assert.Equal("Catering", satisfied.Trigger);
    }

    [Fact]
    public void AfterAllCompleted_WaitsForEveryEarlierService()
    {
        var steps = new[] { Step("Refueling"), Step("Catering"), Step("Boarding", GsxServiceActivation.AfterAllCompleted) };
        var services = Services(
            ("Refueling", GsxServiceState.Callable, true),
            ("Catering", GsxServiceState.Callable, true),
            ("Boarding", GsxServiceState.Callable, true));

        // Refueling done, Catering still running: boarding holds (and nothing else triggers).
        var waiting = Next(steps, services, cycles: new() { ["Refueling"] = Done, ["Catering"] = CalledRequested });
        Assert.Null(waiting.Trigger);
        Assert.Contains(waiting.Holds, h => h.ServiceId == "Boarding" && h.Reason.Contains("all earlier", StringComparison.Ordinal));

        var satisfied = Next(steps, services, cycles: new() { ["Refueling"] = Done, ["Catering"] = Done });
        Assert.Equal("Boarding", satisfied.Trigger);
    }

    [Fact]
    public void AfterAllCompleted_SkippedEarlierServicesCountAsSatisfied()
    {
        // Refueling not offered at this gate, Catering unavailable: both skip, boarding goes.
        var plan = Next(
            [Step("Refueling"), Step("Catering"), Step("Boarding", GsxServiceActivation.AfterAllCompleted)],
            Services(("Catering", GsxServiceState.NotAvailable, false), ("Boarding", GsxServiceState.Callable, true)));

        Assert.Equal("Boarding", plan.Trigger);
    }

    [Fact]
    public void FirstStep_HasNoPreviousService_AndAlwaysActivates()
    {
        var plan = Next(
            [Step("Water", GsxServiceActivation.AfterPrevCompleted)],
            Services(("Water", GsxServiceState.Callable, true)));

        Assert.Equal("Water", plan.Trigger);
    }

    // ---- Manual + force-next (INT/RAD) ----

    [Fact]
    public void Manual_ParksTheCursor_AndBlocksLaterSteps()
    {
        var plan = Next(
            [Step("Refueling", GsxServiceActivation.Manual), Step("Catering")],
            Services(("Refueling", GsxServiceState.Callable, true), ("Catering", GsxServiceState.Callable, true)));

        Assert.Null(plan.Trigger);
        Assert.Contains(plan.Holds, h => h.ServiceId == "Refueling" && h.Reason.Contains("manual", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Manual_CalledExternally_UnblocksTheSequence()
    {
        // The user requested refueling from the GSX menu: mirror shows it running, the cursor
        // moves on and Catering (AfterRequested) activates.
        var plan = Next(
            [Step("Refueling", GsxServiceActivation.Manual), Step("Catering", GsxServiceActivation.AfterRequested)],
            Services(("Refueling", GsxServiceState.Active, false), ("Catering", GsxServiceState.Callable, true)));

        Assert.Equal("Catering", plan.Trigger);
    }

    [Fact]
    public void ForceNext_CallsAManualStep()
    {
        var plan = Next(
            [Step("Refueling", GsxServiceActivation.Manual)],
            Services(("Refueling", GsxServiceState.Callable, true)),
            force: true);

        Assert.Equal("Refueling", plan.Trigger);
        Assert.Contains("forced", plan.TriggerReason, StringComparison.Ordinal);
    }

    [Fact]
    public void ForceNext_BypassesTheBarrier_NotThePlanGate()
    {
        // Boarding early while refueling runs — the classic INT/RAD move.
        var steps = new[] { Step("Refueling"), Step("Boarding", GsxServiceActivation.AfterAllCompleted) };
        var services = Services(("Refueling", GsxServiceState.Active, false), ("Boarding", GsxServiceState.Callable, true));

        var forced = Next(steps, services, force: true);
        Assert.Equal("Boarding", forced.Trigger);

        // But no force ever calls a service before a flight plan exists.
        var noPlan = Next(steps, services, plan: false, force: true);
        Assert.Null(noPlan.Trigger);
        Assert.Contains(noPlan.Holds, h => h.ServiceId == "Boarding" && h.Reason.Contains("flight plan", StringComparison.Ordinal));
    }

    // ---- Constraints ----

    [Fact]
    public void TurnAroundConstraint_SkipsOnTheFirstLeg_RunsOnTurnarounds()
    {
        var steps = new[] { Step("Cleaning", constraint: GsxServiceConstraint.TurnAround), Step("Refueling") };
        var services = Services(("Cleaning", GsxServiceState.Callable, true), ("Refueling", GsxServiceState.Callable, true));

        var firstLeg = Next(steps, services, turnaround: false);
        Assert.Equal("Refueling", firstLeg.Trigger);
        Assert.Contains(firstLeg.Skipped, s => s.ServiceId == "Cleaning" && s.Reason.Contains("turnaround only", StringComparison.Ordinal));

        var turnaround = Next(steps, services, turnaround: true);
        Assert.Equal("Cleaning", turnaround.Trigger);
    }

    [Fact]
    public void FirstLegConstraint_SkipsOnTurnarounds()
    {
        var plan = Next(
            [Step("Water", constraint: GsxServiceConstraint.FirstLeg)],
            Services(("Water", GsxServiceState.Callable, true)),
            turnaround: true);

        Assert.Null(plan.Trigger);
        Assert.Contains(plan.Skipped, s => s.ServiceId == "Water" && s.Reason.Contains("first-leg only", StringComparison.Ordinal));
    }

    // ---- Gates and skips ----

    [Fact]
    public void PlanGate_HoldsTheCursorUntilAFlightPlanExists()
    {
        var plan = Next(
            [Step("Refueling"), Step("Catering")],
            Services(("Refueling", GsxServiceState.Callable, true), ("Catering", GsxServiceState.Callable, true)),
            plan: false);

        Assert.Null(plan.Trigger);
        Assert.Contains(plan.Holds, h => h.ServiceId == "Refueling" && h.Reason.Contains("flight plan", StringComparison.Ordinal));
    }

    [Fact]
    public void PlanGate_NotAppliedWhenOfpNotRequired()
    {
        var plan = Next(
            [Step("Catering")],
            Services(("Catering", GsxServiceState.Callable, true)),
            plan: false,
            requireOfp: false);

        Assert.Equal("Catering", plan.Trigger);
    }

    [Fact]
    public void SkipActivation_MissingAndUnavailableServices_AreSkippedWithReasons()
    {
        var plan = Next(
            [Step("Cleaning", GsxServiceActivation.Skip), Step("Refueling"), Step("Lavatory"), Step("Water")],
            Services(("Cleaning", GsxServiceState.Callable, true), ("Refueling", GsxServiceState.NotAvailable, false), ("Water", GsxServiceState.Callable, true)));

        Assert.Contains(plan.Skipped, s => s.ServiceId == "Cleaning" && s.Reason.Contains("Skip", StringComparison.Ordinal));
        Assert.Contains(plan.Skipped, s => s.ServiceId == "Refueling" && s.Reason == "unavailable");
        Assert.Contains(plan.Skipped, s => s.ServiceId == "Lavatory" && s.Reason.Contains("not offered", StringComparison.Ordinal));
        Assert.Equal("Water", plan.Trigger); // skipped steps are transparent — Water is first callable
    }

    [Fact]
    public void CallableButNotTriggerable_Holds()
    {
        var plan = Next(
            [Step("Catering")],
            Services(("Catering", GsxServiceState.Callable, false)));

        Assert.Null(plan.Trigger);
        Assert.Contains(plan.Holds, h => h.ServiceId == "Catering" && h.Reason.Contains("not triggerable", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicatedSteps_EvaluateOnce()
    {
        var plan = Next(
            [Step("Catering"), Step("Catering")],
            Services(("Catering", GsxServiceState.Callable, true)),
            cycles: new() { ["Catering"] = Done });

        Assert.True(plan.AllDone);
    }

    // ---- Completion ----

    [Fact]
    public void EverythingCompletedOrSkipped_IsAllDone()
    {
        var plan = Next(
            [Step("Refueling"), Step("Catering"), Step("Boarding", GsxServiceActivation.AfterAllCompleted)],
            Services(("Catering", GsxServiceState.Bypassed, false), ("Boarding", GsxServiceState.Callable, true), ("Refueling", GsxServiceState.Callable, true)),
            cycles: new() { ["Refueling"] = Done, ["Boarding"] = Done });

        Assert.True(plan.AllDone);
    }

    [Fact]
    public void RunningService_IsNotAllDone()
    {
        var plan = Next(
            [Step("Boarding")],
            Services(("Boarding", GsxServiceState.Active, false)));

        Assert.False(plan.AllDone);
    }
}
