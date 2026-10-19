using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Automation;
using ProsimCompanion.Gsx.Mirror;
using ProsimCompanion.Gsx.Protocol;
using ProsimCompanion.Gsx.Services;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>
/// Table tests for the departure pump's effect planner (architecture review C1). The effect
/// ORDER is the contract — it used to live inline in the shell's pump, reachable only with a
/// live GSX, and the pillar's recent shell bugs were all ordering bugs: #117/#118 tankering
/// prelim raise and reversal line, #166 the de-ice store mark that must wait for the lock,
/// #169 the turnaround settle stamp, and the all-done tail (complete → event → signal).
/// Each scenario pins the sequence the shell must perform.
/// </summary>
public sealed class DeparturePumpEffectPlannerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 23, 25, 21, TimeSpan.Zero);

    private static DepartureServiceStep Step(string id, GsxServiceActivation activation = GsxServiceActivation.AfterCalled)
        => new(id, activation, GsxServiceConstraint.Always);

    private static Dictionary<string, GsxServiceInfo> Services(
        params (string Id, GsxServiceState State, bool CanTrigger)[] entries)
        => entries.ToDictionary(
            e => e.Id,
            e => new GsxServiceInfo(e.Id, e.Id, null, e.State, e.CanTrigger, false, null, null),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>Baseline: the cycle is running, prep done, plan loaded, Refueling callable.</summary>
    private static DepartureAutomationCore.PumpInputs Ready(
        IReadOnlyList<DepartureServiceStep>? steps = null,
        Dictionary<string, GsxServiceInfo>? services = null,
        Dictionary<string, DepartureCycleView>? cycles = null)
        => new(
            AutomationEnabled: true,
            GsxReady: true,
            ResyncAssessed: true,
            AutoStartOption: false,
            Phase: GsxAutomationPhase.Preparation,
            CycleStarted: true,
            CycleComplete: false,
            PrepComplete: true,
            RequireOfp: true,
            FmsPlanPresent: true,
            OfpImported: true,
            FlightPlanAvailable: true,
            FmsOrigin: "EFHK",
            FmsDestination: "EGCC",
            Forced: false,
            InFlightServiceId: null,
            IsTurnaround: false,
            IsCompanyHub: false,
            EstimatedEnroute: null,
            Steps: steps ?? [Step("Refueling")],
            MirrorServices: services ?? Services(("Refueling", GsxServiceState.Callable, true)),
            Cycle: id => cycles?.GetValueOrDefault(id) ?? default);

    private static DeparturePumpReading Reading(
        DepartureAutomationCore.PumpInputs inputs,
        bool deiceStepNext = false,
        DateTimeOffset? planAvailableSince = null,
        double fuelOnBoardKg = 4_200,
        int delaySeconds = 30,
        bool askFirst = false,
        DateTimeOffset? now = null)
        => new(
            Inputs: inputs,
            ConfiguredSteps: inputs.Steps,
            DeiceStepNext: deiceStepNext,
            PlanAvailableSinceUtc: planAvailableSince,
            FuelOnBoardKg: fuelOnBoardKg,
            TurnaroundAutoStartDelaySeconds: delaySeconds,
            TurnaroundAskFirst: askFirst,
            Now: now ?? T0);

    private static DepartureLatchSnapshot Latches(
        bool tankeringPrelimRaised = false,
        DateTimeOffset? turnaroundReadySince = null,
        bool questionAsked = false,
        DateTimeOffset? planAvailableSince = null,
        DateTimeOffset? lastImportAttempt = null,
        IReadOnlyDictionary<string, int>? attempts = null)
        => DepartureLatchSnapshot.Initial with
        {
            TankeringPrelimRaised = tankeringPrelimRaised,
            TurnaroundReadySinceUtc = turnaroundReadySince,
            TurnaroundQuestionAsked = questionAsked,
            PlanAvailableSinceUtc = planAvailableSince,
            LastImportAttemptUtc = lastImportAttempt ?? DateTimeOffset.MinValue,
            TriggerAttempts = attempts ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
        };

    /// <summary>Evaluate through the real core, then plan — the full pump minus I/O.</summary>
    private static IReadOnlyList<DeparturePumpEffect> PlanFor(
        DeparturePumpReading reading, DepartureLatchSnapshot? latches = null)
        => DeparturePumpEffectPlanner.Plan(
            reading, DepartureAutomationCore.Evaluate(reading.Inputs), latches ?? Latches());

    private static IReadOnlyList<DeparturePumpEffect> PlanFor(
        DepartureAutomationCore.PumpOutcome outcome,
        DeparturePumpReading? reading = null,
        DepartureLatchSnapshot? latches = null)
        => DeparturePumpEffectPlanner.Plan(reading ?? Reading(Ready()), outcome, latches ?? Latches());

    private static int IndexOf<T>(IReadOnlyList<DeparturePumpEffect> effects, Func<T, bool>? match = null)
        where T : DeparturePumpEffect
    {
        for (var i = 0; i < effects.Count; i++)
        {
            if (effects[i] is T typed && (match is null || match(typed)))
            {
                return i;
            }
        }

        return -1;
    }

    private static RecordDecisionEffect Decision(IReadOnlyList<DeparturePumpEffect> effects, string action)
        => Assert.Single(effects.OfType<RecordDecisionEffect>(), d => d.Action == action);

    // ---- refuel-on-fuel-confirmed hold (probe refuel-on-fuel-confirmed-hold, 2026-09-19 / #157) ----

    private static DeparturePumpReading FuelConfirmationSequence(bool confirmed)
    {
        var inputs = Ready(
            steps: [Step("Refueling"), Step("Catering"), Step("Boarding", GsxServiceActivation.AfterAllCompleted)],
            services: Services(
                ("Refueling", GsxServiceState.Callable, true),
                ("Catering", GsxServiceState.Callable, true),
                ("Boarding", GsxServiceState.Callable, true)))
            with
            {
                PreHold = id => id == "Refueling" && !confirmed ? DeparturePumpGatherer.FuelConfirmationHoldReason : null,
            };
        return Reading(inputs);
    }

    [Fact]
    public void FuelConfirmationHold_HoldsRefueling_TriggersCatering_NeverDispatchesRefueling()
    {
        var effects = PlanFor(FuelConfirmationSequence(confirmed: false));

        var hold = Decision(effects, "hold Refueling");
        Assert.True(hold.Once);
        Assert.Contains("waiting for the crew to confirm the block fuel", hold.Reason, StringComparison.Ordinal);

        var dispatch = Assert.Single(effects.OfType<DispatchTriggerEffect>());
        Assert.Equal("Catering", dispatch.ServiceId);
        Assert.Equal("sequencer — next in departure order (AfterCalled)", dispatch.Source);
        Assert.DoesNotContain(effects.OfType<DispatchTriggerEffect>(), d => d.ServiceId == "Refueling");

        // Boarding (AfterAllCompleted) is never reached this evaluation — the cursor sits on
        // Catering; it waits for the held truck once Catering has been called.
        Assert.DoesNotContain(effects.OfType<DispatchTriggerEffect>(), d => d.ServiceId == "Boarding");

        // Board → hold records → dispatch: the Status page already shows the hold when the
        // trigger goes out, and the dispatch is the last thing before the all-done tail.
        Assert.True(IndexOf<PublishBoardEffect>(effects) < IndexOf<RecordDecisionEffect>(effects, d => d.Action == "hold Refueling"));
        Assert.True(IndexOf<RecordDecisionEffect>(effects, d => d.Action == "hold Refueling") < IndexOf<DispatchTriggerEffect>(effects));
        Assert.Empty(effects.OfType<MarkCycleCompleteEffect>());
    }

    [Fact]
    public void FuelConfirmed_ReleasesTheHold_DispatchesRefuelingOnTheNextPump()
    {
        var effects = PlanFor(FuelConfirmationSequence(confirmed: true));

        var dispatch = Assert.Single(effects.OfType<DispatchTriggerEffect>());
        Assert.Equal("Refueling", dispatch.ServiceId);
        Assert.DoesNotContain(effects.OfType<RecordDecisionEffect>(), d => d.Action == "hold Refueling");
    }

    [Fact]
    public void ForceNext_WhileRefuelingHeld_CallsTheNextEligibleService_NeverRefueling()
    {
        // Probe watch line: the hold is not force-bypassable.
        var reading = FuelConfirmationSequence(confirmed: false);
        reading = reading with { Inputs = reading.Inputs with { Forced = true } };

        var effects = PlanFor(reading);

        var dispatch = Assert.Single(effects.OfType<DispatchTriggerEffect>());
        Assert.NotEqual("Refueling", dispatch.ServiceId);
        var forced = Decision(effects, "force next service");
        Assert.Equal($"calling {dispatch.ServiceId}", forced.Reason);
        // The latch is cleared before the decision line, and both come before the board.
        Assert.True(IndexOf<ClearForceNextEffect>(effects) < IndexOf<RecordDecisionEffect>(effects, d => d.Action == "force next service"));
        Assert.True(IndexOf<RecordDecisionEffect>(effects, d => d.Action == "force next service") < IndexOf<PublishBoardEffect>(effects));
    }

    // ---- de-icing policy timing (issue #166, probe gsx-deice-policy) ----

    private static DeparturePumpReading DeiceHeldAtTurn(bool deiceStepNext, bool boardingDone)
    {
        // Boarding completed (or not), the DeIce step pre-held "decided when this step's turn comes".
        var cycles = new Dictionary<string, DepartureCycleView>(StringComparer.OrdinalIgnoreCase);
        if (boardingDone)
        {
            cycles["Boarding"] = new DepartureCycleView(true, true, true, true);
        }

        var inputs = Ready(
            steps: [Step("Boarding"), Step(GsxServiceIds.DeIce)],
            services: Services(
                ("Boarding", GsxServiceState.Callable, true),
                (GsxServiceIds.DeIce, GsxServiceState.Callable, true)),
            cycles: cycles)
            with
            {
                PreHold = id => id == GsxServiceIds.DeIce ? "de-icing policy — decided when this step's turn comes" : null,
            };
        return Reading(inputs, deiceStepNext: deiceStepNext);
    }

    [Fact]
    public void DeiceStepNext_IsMarkedAfterTheLock_OnceEveryEarlierServiceIsDone()
    {
        var effects = PlanFor(DeiceHeldAtTurn(deiceStepNext: false, boardingDone: true));

        var decision = Decision(effects, "de-ice step next");
        Assert.True(decision.Once);
        Assert.Equal("every earlier service done — the de-icing policy decides now", decision.Reason);

        var mark = Assert.Single(effects.OfType<MarkDeiceStepNextEffect>());
        Assert.True(mark.RunsAfterLockRelease);
        // The decision line is written in place (before the board); the store mark is deferred.
        Assert.True(IndexOf<RecordDecisionEffect>(effects, d => d.Action == "de-ice step next") < IndexOf<PublishBoardEffect>(effects));
        Assert.Equal(IndexOf<RecordDecisionEffect>(effects, d => d.Action == "de-ice step next") + 1, IndexOf<MarkDeiceStepNextEffect>(effects));
        // No DeIce trigger while the policy has not decided.
        Assert.Empty(effects.OfType<DispatchTriggerEffect>());
    }

    [Fact]
    public void DeiceStepNext_NotMarked_WhileBoardingStillOwnsTheCursor()
    {
        // The 2026-10-10 EFHK signature: the question fired 1 ms after "departure sequence
        // started", before boarding. With Boarding unsettled the step's turn has not come.
        var effects = PlanFor(DeiceHeldAtTurn(deiceStepNext: false, boardingDone: false));

        Assert.Empty(effects.OfType<MarkDeiceStepNextEffect>());
        Assert.DoesNotContain(effects.OfType<RecordDecisionEffect>(), d => d.Action == "de-ice step next");
        Assert.Equal("Boarding", Assert.Single(effects.OfType<DispatchTriggerEffect>()).ServiceId);
    }

    [Fact]
    public void DeiceStepNext_NotMarkedTwice_OnceTheStoreKnows()
    {
        var effects = PlanFor(DeiceHeldAtTurn(deiceStepNext: true, boardingDone: true));

        Assert.Empty(effects.OfType<MarkDeiceStepNextEffect>());
    }

    [Fact]
    public void OnlyTheDeiceMark_RunsAfterTheLock()
    {
        var effects = PlanFor(DeiceHeldAtTurn(deiceStepNext: false, boardingDone: true));

        Assert.All(effects.Where(e => e is not MarkDeiceStepNextEffect), e => Assert.False(e.RunsAfterLockRelease));
    }

    // ---- tankering prelim raise (#117) and reversal (#118) ----

    private static DepartureAutomationCore.PumpOutcome SkippedForTankering()
        => new(
            AutoStarted: false,
            WaitingBoard: null,
            HoldReason: null,
            StartSimbriefImport: false,
            PlanDiagnostic: null,
            ArmPaxTarget: true,
            Plan: new DeparturePlan(
                Trigger: "Catering",
                TriggerReason: "next in departure order",
                Holds: [],
                Skipped: [("Refueling", "FOB 9576 kg at or above the plan 7100 kg (tankering) — GSX refuel not called")],
                AllDone: false),
            ConsumedForce: false,
            Trigger: "Catering",
            AllDone: false);

    [Fact]
    public void TankeringSkip_RaisesThePrelimTriggerOnce_InOrder()
    {
        var effects = PlanFor(SkippedForTankering());

        var skip = IndexOf<RecordDecisionEffect>(effects, d => d.Action == "skip Refueling");
        Assert.True(effects[skip] is RecordDecisionEffect { Once: true });
        // Skip record → latch → decision → signal, then the trigger.
        Assert.IsType<MarkTankeringPrelimRaisedEffect>(effects[skip + 1]);
        Assert.Equal(
            new RecordDecisionEffect("refuel skipped for tankering", "preliminary loadsheet trigger raised in place of the refuel-active edge"),
            effects[skip + 2]);
        Assert.IsType<RaiseRefuelServiceActiveEffect>(effects[skip + 3]);
        Assert.True(skip + 3 < IndexOf<DispatchTriggerEffect>(effects));
        Assert.True(IndexOf<PublishBoardEffect>(effects) < skip);
    }

    [Fact]
    public void TankeringSkip_AlreadyRaised_OnlyRecordsTheSkip()
    {
        var effects = PlanFor(SkippedForTankering(), latches: Latches(tankeringPrelimRaised: true));

        Assert.Empty(effects.OfType<MarkTankeringPrelimRaisedEffect>());
        Assert.Empty(effects.OfType<RaiseRefuelServiceActiveEffect>());
        Assert.DoesNotContain(effects.OfType<RecordDecisionEffect>(), d => d.Action == "refuel skipped for tankering");
        Decision(effects, "skip Refueling");
    }

    [Fact]
    public void TankeringSkipReversed_IsRecordedBeforeTheRefuelingDispatch()
    {
        // 2026-09-05: the pilot reset the FOB to ~300 kg after the skip had been published —
        // calling Refueling after all is correct, the log must say why (#118).
        var effects = PlanFor(Reading(Ready(), fuelOnBoardKg: 312), Latches(tankeringPrelimRaised: true));

        var reversed = Decision(effects, "tankering skip reversed");
        Assert.True(reversed.Once);
        Assert.Equal("FOB 312 kg no longer meets the plan — Refueling called after all", reversed.Reason);
        var reversedAt = IndexOf<RecordDecisionEffect>(effects, d => d.Action == "tankering skip reversed");
        Assert.IsType<RecordTriggerAttemptEffect>(effects[reversedAt + 1]);
        Assert.Equal(new DispatchTriggerEffect("Refueling", "sequencer — next in departure order (AfterCalled)"), effects[reversedAt + 2]);
    }

    [Fact]
    public void RefuelingDispatch_WithoutAnEarlierSkip_HasNoReversalLine()
    {
        var effects = PlanFor(Reading(Ready()));

        Assert.DoesNotContain(effects.OfType<RecordDecisionEffect>(), d => d.Action == "tankering skip reversed");
        Assert.Equal(new RecordTriggerAttemptEffect("Refueling", 1), Assert.Single(effects.OfType<RecordTriggerAttemptEffect>()));
    }

    [Fact]
    public void ReofferedTrigger_CarriesTheAttemptNumber()
    {
        var attempts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Refueling"] = 2 };

        var effects = PlanFor(Reading(Ready()), Latches(attempts: attempts));

        Assert.Equal(new RecordTriggerAttemptEffect("Refueling", 3), Assert.Single(effects.OfType<RecordTriggerAttemptEffect>()));
        Assert.Equal("sequencer — next in departure order (AfterCalled) (attempt 3)", Assert.Single(effects.OfType<DispatchTriggerEffect>()).Source);
    }

    // ---- turnaround settle stamp and ask-first (issue #169) ----

    private static DepartureAutomationCore.PumpOutcome WaitingWithStamp(DateTimeOffset? readySince, bool ask = false)
        => DepartureAutomationCore.PumpOutcome.Idle with
        {
            WaitingBoard = "departure sequence not started",
            TurnaroundReadySinceUtc = readySince,
            AskTurnaroundStart = ask,
        };

    [Fact]
    public void TurnaroundSettleStamp_RecordsOnce_ThenStoresTheStamp_BeforeTheWaitingBoard()
    {
        var effects = PlanFor(WaitingWithStamp(T0), Reading(Ready() with { CycleStarted = false }, delaySeconds: 30));

        Assert.Equal(
            [
                new RecordDecisionEffect("departure sequence", "turnaround with the next plan loaded — auto-start in 30 s"),
                new SetTurnaroundReadySinceEffect(T0),
            ],
            effects.Take(2).ToArray());
        Assert.IsType<PublishBoardEffect>(effects[2]);
        Assert.Equal(3, effects.Count);
    }

    [Fact]
    public void TurnaroundSettleStamp_AlreadyTaken_IsNotRecordedAgain()
    {
        var effects = PlanFor(
            WaitingWithStamp(T0),
            Reading(Ready() with { CycleStarted = false }),
            Latches(turnaroundReadySince: T0));

        Assert.Empty(effects.OfType<SetTurnaroundReadySinceEffect>());
        Assert.DoesNotContain(effects.OfType<RecordDecisionEffect>(), d => d.Action == "departure sequence");
    }

    [Fact]
    public void TurnaroundConditionsLapsed_ClearsTheStamp()
    {
        var effects = PlanFor(
            WaitingWithStamp(null),
            Reading(Ready() with { CycleStarted = false }),
            Latches(turnaroundReadySince: T0));

        Assert.Equal(new SetTurnaroundReadySinceEffect(null), Assert.Single(effects.OfType<SetTurnaroundReadySinceEffect>()));
    }

    [Fact]
    public void TurnaroundSettleStamp_AskFirst_SaysTheFoWillAsk()
    {
        var effects = PlanFor(WaitingWithStamp(T0), Reading(Ready() with { CycleStarted = false }, delaySeconds: 45, askFirst: true));

        Assert.Equal(
            "turnaround with the next plan loaded — auto-start in 45 s (the FO will ask first)",
            Decision(effects, "departure sequence").Reason);
    }

    [Fact]
    public void AskFirstQuestion_LatchesThenRecordsThenAsks_Once()
    {
        var effects = PlanFor(WaitingWithStamp(T0, ask: true), Reading(Ready() with { CycleStarted = false }), Latches(turnaroundReadySince: T0));

        Assert.Equal(
            [
                new MarkTurnaroundQuestionAskedEffect(),
                new RecordDecisionEffect("departure sequence", "turnaround — asking the captain to commence ground services"),
                new RecordEventEffect("gsx-turnaround-question", new TurnaroundQuestionPayload(DeparturePumpEffectPlanner.TurnaroundStartPrompt)),
                new AskTurnaroundStartEffect(DeparturePumpEffectPlanner.TurnaroundStartPrompt),
            ],
            effects.Take(4).ToArray());

        var again = PlanFor(WaitingWithStamp(T0, ask: true), Reading(Ready() with { CycleStarted = false }), Latches(turnaroundReadySince: T0, questionAsked: true));
        Assert.Empty(again.OfType<AskTurnaroundStartEffect>());
    }

    [Fact]
    public void TurnaroundAutoStart_StampsThenStartsThenRecords_ThenRunsTheSequence()
    {
        var turnaround = new DepartureAutomationCore.TurnaroundStartInputs(
            Enabled: true, NextLegOfpLoaded: true, ReadySinceUtc: T0, Now: T0.AddSeconds(30),
            Delay: TimeSpan.FromSeconds(30), AskFirst: false, QuestionAsked: false, Declined: false);
        var inputs = Ready() with { CycleStarted = false, VoiceActivationMode = true, IsTurnaround = true, Turnaround = turnaround };

        var effects = PlanFor(Reading(inputs), Latches(turnaroundReadySince: T0));

        // The core stops carrying the stamp once the cycle starts: the latch clears first.
        Assert.Equal(new SetTurnaroundReadySinceEffect(null), effects[0]);
        Assert.Equal(new StartCycleEffect(DepartureAutomationCore.TurnaroundAutoStartSource), effects[1]);
        Assert.Equal(new RecordDecisionEffect("departure sequence", "auto-started (turnaround)"), effects[2]);
        Assert.True(IndexOf<StartCycleEffect>(effects) < IndexOf<PublishBoardEffect>(effects));
        Assert.Equal("Refueling", Assert.Single(effects.OfType<DispatchTriggerEffect>()).ServiceId);
    }

    [Fact]
    public void PreparationAutoStart_RecordsThePreparationSource()
    {
        var effects = PlanFor(Reading(Ready() with { CycleStarted = false, AutoStartOption = true }));

        Assert.Equal(new StartCycleEffect(DepartureAutomationCore.AutoStartSource), effects[0]);
        Assert.Equal(new RecordDecisionEffect("departure sequence", "auto-started (Preparation phase)"), effects[1]);
    }

    // ---- all done ----

    [Fact]
    public void AllDone_EndsWithCompleteThenDecisionThenEventThenSignal()
    {
        var cycles = new Dictionary<string, DepartureCycleView>(StringComparer.OrdinalIgnoreCase)
        {
            ["Refueling"] = new(true, true, true, true),
        };

        var effects = PlanFor(Reading(Ready(cycles: cycles)));

        Assert.Equal(
            [
                new MarkCycleCompleteEffect(),
                new RecordDecisionEffect("departure sequence", "all departure services completed or skipped"),
                new RecordEventEffect("gsx-departure-complete"),
                new RaiseDepartureServicesCompletedEffect(),
            ],
            effects.TakeLast(4).ToArray());
        Assert.Empty(effects.OfType<DispatchTriggerEffect>());
        Assert.True(IndexOf<PublishBoardEffect>(effects) < IndexOf<MarkCycleCompleteEffect>(effects));
    }

    // ---- gates that stop before sequencing ----

    [Fact]
    public void WaitingBoard_RecordsTheHoldOnce_PublishesTheWaitingRows_AndStops()
    {
        var effects = PlanFor(Reading(Ready() with { PrepComplete = false }));

        Assert.Equal(new RecordDecisionEffect("hold departure services", "waiting for ground preparation to complete", Once: true), effects[0]);
        var board = Assert.IsType<PublishBoardEffect>(effects[1]);
        Assert.Equal(2, effects.Count);
        var row = Assert.Single(board.Rows);
        Assert.Equal(new GsxServiceBoardRow("Refueling", GsxServiceStage.Waiting, "ground preparation running"), row);
    }

    [Fact]
    public void Disabled_PlansNothing()
    {
        Assert.Empty(PlanFor(Reading(Ready() with { AutomationEnabled = false })));
    }

    [Fact]
    public void Complete_PlansNothing()
    {
        Assert.Empty(PlanFor(Reading(Ready() with { CycleComplete = true })));
    }

    // ---- stamps, import cooldown, pax target, diagnostic ----

    [Fact]
    public void PlanAvailabilityStamp_IsStoredOnlyWhenItMoved()
    {
        var moved = PlanFor(Reading(Ready(), planAvailableSince: T0), Latches(planAvailableSince: null));
        Assert.Equal(new SetPlanAvailableSinceEffect(T0), moved[0]);

        var steady = PlanFor(Reading(Ready(), planAvailableSince: T0), Latches(planAvailableSince: T0));
        Assert.Empty(steady.OfType<SetPlanAvailableSinceEffect>());

        var lapsed = PlanFor(Reading(Ready(), planAvailableSince: null), Latches(planAvailableSince: T0));
        Assert.Equal(new SetPlanAvailableSinceEffect(null), lapsed[0]);
    }

    [Fact]
    public void SimbriefImport_FiresOncePerCooldown_StampingFirst()
    {
        var inputs = Ready() with { OfpImported = false };

        var first = PlanFor(Reading(inputs), Latches(lastImportAttempt: DateTimeOffset.MinValue));
        var stampAt = IndexOf<SetLastImportAttemptEffect>(first);
        Assert.Equal(new SetLastImportAttemptEffect(T0), first[stampAt]);
        Assert.IsType<StartSimbriefImportEffect>(first[stampAt + 1]);
        Assert.True(stampAt < IndexOf<ArmPaxTargetEffect>(first));

        var tooSoon = PlanFor(Reading(inputs), Latches(lastImportAttempt: T0.AddSeconds(-59)));
        Assert.Empty(tooSoon.OfType<StartSimbriefImportEffect>());

        var again = PlanFor(Reading(inputs), Latches(lastImportAttempt: T0.AddSeconds(-60)));
        Assert.Single(again.OfType<StartSimbriefImportEffect>());
    }

    [Fact]
    public void NoPlan_RecordsTheDiagnosticOnce_AndNeverArmsThePaxTarget()
    {
        var inputs = Ready() with { OfpImported = false, FmsPlanPresent = false, FlightPlanAvailable = false };

        var effects = PlanFor(Reading(inputs));

        var diagnostic = Decision(effects, "flight plan detection");
        Assert.True(diagnostic.Once);
        Assert.StartsWith("none detected", diagnostic.Reason, StringComparison.Ordinal);
        Assert.Empty(effects.OfType<ArmPaxTargetEffect>());
        Assert.Empty(effects.OfType<StartSimbriefImportEffect>());
        Decision(effects, "hold Refueling");
    }

    [Fact]
    public void PlanLoaded_ArmsThePaxTarget_BeforeTheBoard()
    {
        var effects = PlanFor(Reading(Ready()));

        Assert.True(IndexOf<ArmPaxTargetEffect>(effects) < IndexOf<PublishBoardEffect>(effects));
    }

    // ---- board rows ----

    [Fact]
    public void BoardRows_ReadTheStageFromCycleMirrorPlanAndSlot()
    {
        var steps = new[] { Step("Refueling"), Step("Catering"), Step("Boarding"), Step("Water"), Step("Cleaning"), Step("Lavatory"), Step("Water") };
        var services = Services(
            ("Catering", GsxServiceState.Active, false),
            ("Boarding", GsxServiceState.Requested, false),
            ("Water", GsxServiceState.Callable, true),
            ("Cleaning", GsxServiceState.Callable, true),
            ("Lavatory", GsxServiceState.Callable, true));
        services["Catering"] = services["Catering"] with { ProgressText = "loading" };
        var cycles = new Dictionary<string, DepartureCycleView>(StringComparer.OrdinalIgnoreCase)
        {
            ["Refueling"] = new(true, true, true, true),
            ["Water"] = new(true, false, false, false), // called, awaiting GSX — pending
        };
        var plan = new DeparturePlan(
            Trigger: null, TriggerReason: null,
            Holds: [("Lavatory", "waiting for all earlier services to complete"), ("Water", "noted")],
            Skipped: [("Cleaning", "first leg only")],
            AllDone: false);

        var rows = DeparturePumpEffectPlanner.BoardRows(steps, plan, services, id => cycles.GetValueOrDefault(id), inFlight: null);

        Assert.Equal(
            [
                new GsxServiceBoardRow("Refueling", GsxServiceStage.Completed, null),
                new GsxServiceBoardRow("Catering", GsxServiceStage.Active, "loading"),
                new GsxServiceBoardRow("Boarding", GsxServiceStage.Requested, null),
                new GsxServiceBoardRow("Water", GsxServiceStage.Called, "noted"),
                new GsxServiceBoardRow("Cleaning", GsxServiceStage.Skipped, "first leg only"),
                new GsxServiceBoardRow("Lavatory", GsxServiceStage.Held, "waiting for all earlier services to complete"),
            ],
            rows);
    }

    [Fact]
    public void BoardRows_TriggerAndInFlightReadAsCalled()
    {
        var steps = new[] { Step("Refueling"), Step("Catering") };
        var services = Services(("Refueling", GsxServiceState.Callable, true), ("Catering", GsxServiceState.Callable, true));
        var plan = new DeparturePlan("Catering", "next", [], [], false);

        var rows = DeparturePumpEffectPlanner.BoardRows(steps, plan, services, _ => default, inFlight: "Refueling");

        Assert.All(rows, row => Assert.Equal(GsxServiceStage.Called, row.Stage));
    }

    [Fact]
    public void WaitingBoardRows_KeepCompletedServices_OthersCarryTheReason()
    {
        var steps = new[] { Step("Refueling"), Step("Boarding") };
        var cycles = new Dictionary<string, DepartureCycleView>(StringComparer.OrdinalIgnoreCase)
        {
            ["Refueling"] = new(true, true, true, true),
        };

        var rows = DeparturePumpEffectPlanner.WaitingBoardRows(steps, id => cycles.GetValueOrDefault(id), "departure sequence not started");

        Assert.Equal(
            [
                new GsxServiceBoardRow("Refueling", GsxServiceStage.Completed, null),
                new GsxServiceBoardRow("Boarding", GsxServiceStage.Waiting, "departure sequence not started"),
            ],
            rows);
    }
}
