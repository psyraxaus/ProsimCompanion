using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Gsx.Automation;
using ProsimCompanion.Gsx.Mirror;
using ProsimCompanion.Gsx.Protocol;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>
/// Table tests for the departure pump's decision function (campaign #78). The gate ORDER is
/// the contract: enabled/ready → resync → started/phase → ground prep → plan gate →
/// sequencing. The pillar's worst historical bugs were ordering bugs (#30: reposition before
/// the resync seed; round-5: auto-import satisfying the plan gate) — these tests pin it.
/// </summary>
public sealed class DepartureAutomationCoreTests
{
    private static DepartureServiceStep Step(string id)
        => new(id, GsxServiceActivation.AfterCalled, GsxServiceConstraint.Always);

    private static Dictionary<string, GsxServiceInfo> Services(
        params (string Id, GsxServiceState State, bool CanTrigger)[] entries)
        => entries.ToDictionary(
            e => e.Id,
            e => new GsxServiceInfo(e.Id, e.Id, null, e.State, e.CanTrigger, false, null, null),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>Baseline: everything open — a callable Refueling should be triggered.</summary>
    private static DepartureAutomationCore.PumpInputs Ready(
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
            FmsOrigin: "YSSY",
            FmsDestination: "YMML",
            Forced: false,
            InFlightServiceId: null,
            IsTurnaround: false,
            IsCompanyHub: false,
            EstimatedEnroute: null,
            Steps: [Step("Refueling")],
            MirrorServices: Services(("Refueling", GsxServiceState.Callable, true)),
            Cycle: id => cycles?.GetValueOrDefault(id) ?? default);

    [Fact]
    public void VoiceActivationMode_NeverAutoStarts()
    {
        // 2026-08-29 ESSA turnaround: the auto-start marked the cycle Started and silently
        // released the prep chain's "commence ground services" hold.
        var inputs = Ready() with { CycleStarted = false, AutoStartOption = true, VoiceActivationMode = true };

        var outcome = DepartureAutomationCore.Evaluate(inputs);

        Assert.False(outcome.AutoStarted);
        Assert.Null(outcome.Trigger);
    }

    [Fact]
    public void AutoMode_StillAutoStarts()
    {
        var outcome = DepartureAutomationCore.Evaluate(Ready() with { CycleStarted = false, AutoStartOption = true });

        Assert.True(outcome.AutoStarted);
        Assert.Equal(DepartureAutomationCore.AutoStartSource, outcome.AutoStartSource);
    }

    // ---- voiceThenAuto turnaround start (issue #169, 2026-10-11 EGCC turnaround) ----

    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 23, 25, 21, TimeSpan.Zero);

    private static DepartureAutomationCore.TurnaroundStartInputs Turnaround(
        DateTimeOffset? readySince = null,
        DateTimeOffset? now = null,
        bool nextLegOfpLoaded = true,
        bool askFirst = false,
        bool questionAsked = false,
        bool declined = false)
        => new(
            Enabled: true,
            NextLegOfpLoaded: nextLegOfpLoaded,
            ReadySinceUtc: readySince,
            Now: now ?? T0,
            Delay: TimeSpan.FromSeconds(30),
            AskFirst: askFirst,
            QuestionAsked: questionAsked,
            Declined: declined);

    /// <summary>The voice gate held for the turnaround on the flight: engines off at the
    /// stand, OFP loaded, autoStart on — nothing started until the web button.</summary>
    private static DepartureAutomationCore.PumpInputs TurnaroundReady()
        => Ready() with { CycleStarted = false, AutoStartOption = true, VoiceActivationMode = true, IsTurnaround = true };

    [Fact]
    public void VoiceThenAuto_FirstLeg_StaysOnTheVoiceGate()
    {
        var inputs = TurnaroundReady() with { IsTurnaround = false, Turnaround = Turnaround() };

        var outcome = DepartureAutomationCore.Evaluate(inputs);

        Assert.False(outcome.AutoStarted);
        Assert.Null(outcome.TurnaroundReadySinceUtc);
        Assert.Equal("departure sequence not started", outcome.WaitingBoard);
    }

    [Fact]
    public void VoiceThenAuto_TurnaroundWithTheNextPlan_StampsReadyButWaitsOutTheDelay()
    {
        var outcome = DepartureAutomationCore.Evaluate(TurnaroundReady() with { Turnaround = Turnaround() });

        Assert.False(outcome.AutoStarted);
        Assert.Equal(T0, outcome.TurnaroundReadySinceUtc);
        Assert.False(outcome.AskTurnaroundStart);
    }

    [Fact]
    public void VoiceThenAuto_TurnaroundSettled_AutoStartsAsTurnaround()
    {
        var inputs = TurnaroundReady() with { Turnaround = Turnaround(readySince: T0, now: T0.AddSeconds(30)) };

        var outcome = DepartureAutomationCore.Evaluate(inputs);

        Assert.True(outcome.AutoStarted);
        Assert.Equal(DepartureAutomationCore.TurnaroundAutoStartSource, outcome.AutoStartSource);
        Assert.Equal("Refueling", outcome.Trigger); // the sequence runs on in the same evaluation
    }

    [Fact]
    public void VoiceThenAuto_WithoutTheNextLegsPlan_NeverStarts()
    {
        var inputs = TurnaroundReady() with { Turnaround = Turnaround(readySince: T0, now: T0.AddMinutes(10), nextLegOfpLoaded: false) };

        var outcome = DepartureAutomationCore.Evaluate(inputs);

        Assert.False(outcome.AutoStarted);
        Assert.Null(outcome.TurnaroundReadySinceUtc); // the stamp lapses with the conditions
    }

    [Fact]
    public void VoiceThenAuto_AskFirst_RaisesTheQuestionOnceInsteadOfStarting()
    {
        var settled = Turnaround(readySince: T0, now: T0.AddSeconds(45), askFirst: true);

        var first = DepartureAutomationCore.Evaluate(TurnaroundReady() with { Turnaround = settled });
        var again = DepartureAutomationCore.Evaluate(TurnaroundReady() with { Turnaround = settled with { QuestionAsked = true } });

        Assert.False(first.AutoStarted);
        Assert.True(first.AskTurnaroundStart);
        Assert.False(again.AskTurnaroundStart);
        Assert.False(again.AutoStarted);
    }

    [Fact]
    public void VoiceThenAuto_Declined_StaysOnTheVoiceGate()
    {
        var inputs = TurnaroundReady() with { Turnaround = Turnaround(readySince: T0, now: T0.AddMinutes(5), declined: true) };

        var outcome = DepartureAutomationCore.Evaluate(inputs);

        Assert.False(outcome.AutoStarted);
        Assert.False(outcome.AskTurnaroundStart);
        Assert.Null(outcome.TurnaroundReadySinceUtc);
    }

    [Fact]
    public void PlainVoiceMode_TurnaroundNeverAutoStarts()
    {
        // gsx.groundPrepActivation = voice: the shell passes no turnaround inputs.
        var outcome = DepartureAutomationCore.Evaluate(TurnaroundReady());

        Assert.False(outcome.AutoStarted);
        Assert.Null(outcome.AutoStartSource);
    }

    [Fact]
    public void VoiceThenAuto_NotInPreparation_NeverStarts()
    {
        var inputs = TurnaroundReady() with
        {
            Phase = GsxAutomationPhase.Arrival,
            Turnaround = Turnaround(readySince: T0, now: T0.AddMinutes(5)),
        };

        Assert.False(DepartureAutomationCore.Evaluate(inputs).AutoStarted);
    }

    [Fact]
    public void EverythingOpen_TriggersTheNextService()
    {
        var outcome = DepartureAutomationCore.Evaluate(Ready());

        Assert.Equal("Refueling", outcome.Trigger);
        Assert.True(outcome.ArmPaxTarget);
        Assert.Null(outcome.WaitingBoard);
        Assert.False(outcome.AllDone);
    }

    [Fact]
    public void Disabled_OrNotReady_DoesNothing_NotEvenABoard()
    {
        Assert.Equal(
            DepartureAutomationCore.PumpOutcome.Idle,
            DepartureAutomationCore.Evaluate(Ready() with { AutomationEnabled = false }));
        Assert.Equal(
            DepartureAutomationCore.PumpOutcome.Idle,
            DepartureAutomationCore.Evaluate(Ready() with { GsxReady = false }));
    }

    [Fact]
    public void ResyncGate_HoldsBeforeEverything_EvenAutoStart()
    {
        // #30: never sequence — and never even auto-start — before the startup resync has
        // assessed prior progress.
        var outcome = DepartureAutomationCore.Evaluate(Ready() with
        {
            ResyncAssessed = false,
            CycleStarted = false,
            AutoStartOption = true,
        });

        Assert.Equal("waiting for the startup state resync", outcome.WaitingBoard);
        Assert.False(outcome.AutoStarted);
        Assert.Null(outcome.Plan);
        Assert.Null(outcome.Trigger);
    }

    [Fact]
    public void AutoStart_FiresOnlyInPreparation()
    {
        var preparation = DepartureAutomationCore.Evaluate(Ready() with
        {
            CycleStarted = false,
            AutoStartOption = true,
        });
        Assert.True(preparation.AutoStarted);
        Assert.Equal("Refueling", preparation.Trigger); // start counts within the same evaluation

        var sessionStart = DepartureAutomationCore.Evaluate(Ready() with
        {
            CycleStarted = false,
            AutoStartOption = true,
            Phase = GsxAutomationPhase.SessionStart,
        });
        Assert.False(sessionStart.AutoStarted);
        Assert.Equal("departure sequence not started", sessionStart.WaitingBoard);
    }

    [Fact]
    public void NotStarted_ShowsTheWaitingBoard_AndNeverSequences()
    {
        var outcome = DepartureAutomationCore.Evaluate(Ready() with { CycleStarted = false });

        Assert.Equal("departure sequence not started", outcome.WaitingBoard);
        Assert.Null(outcome.Plan);
    }

    [Fact]
    public void CompleteCycle_StaysQuiet_NoBoardRepaint()
    {
        var outcome = DepartureAutomationCore.Evaluate(Ready() with { CycleComplete = true });

        Assert.Null(outcome.WaitingBoard);
        Assert.Null(outcome.Plan);
        Assert.Null(outcome.Trigger);
    }

    [Fact]
    public void OffTheGroundPhases_NeverSequence()
    {
        foreach (var phase in new[]
        {
            GsxAutomationPhase.PushBack,
            GsxAutomationPhase.Flight,
            GsxAutomationPhase.Arrival,
        })
        {
            var outcome = DepartureAutomationCore.Evaluate(Ready() with { Phase = phase });
            Assert.Null(outcome.Plan);
            Assert.Null(outcome.Trigger);
        }
    }

    [Fact]
    public void GroundPrepGate_HoldsServices_WithTheHoldDecision()
    {
        // Owner-specified order: reposition → GPU/chocks → jetway/stairs before any service.
        var outcome = DepartureAutomationCore.Evaluate(Ready() with { PrepComplete = false });

        Assert.Equal("ground preparation running", outcome.WaitingBoard);
        Assert.NotNull(outcome.HoldReason);
        Assert.Null(outcome.Plan);
    }

    [Fact]
    public void McduPlanWithoutOfp_StartsTheImport_AndNothingElseDoes()
    {
        // Round-5 smoke test: the importer must only run AFTER the pilot's MCDU plan shows up.
        var mcduPlan = DepartureAutomationCore.Evaluate(Ready() with
        {
            OfpImported = false,
            FmsPlanPresent = true,
            FlightPlanAvailable = true,
        });
        Assert.True(mcduPlan.StartSimbriefImport);

        var noMcduPlan = DepartureAutomationCore.Evaluate(Ready() with
        {
            OfpImported = false,
            FmsPlanPresent = false,
            FlightPlanAvailable = false,
        });
        Assert.False(noMcduPlan.StartSimbriefImport);
        Assert.NotNull(noMcduPlan.PlanDiagnostic);

        var ofpNotRequired = DepartureAutomationCore.Evaluate(Ready() with
        {
            OfpImported = false,
            FmsPlanPresent = true,
            RequireOfp = false,
        });
        Assert.False(ofpNotRequired.StartSimbriefImport);
        Assert.Null(ofpNotRequired.PlanDiagnostic);
    }

    [Fact]
    public void NoFlightPlan_StillSequences_ButNeverArmsPax()
    {
        // The plan HOLD lives in the sequencer (per-service reasons on the board); the core
        // only decides the diagnostic and the pax arming.
        var outcome = DepartureAutomationCore.Evaluate(Ready() with { FlightPlanAvailable = false });

        Assert.NotNull(outcome.Plan);
        Assert.False(outcome.ArmPaxTarget);
        Assert.Null(outcome.Trigger); // sequencer holds every service behind the plan gate
        Assert.NotNull(outcome.PlanDiagnostic);
    }

    [Fact]
    public void InFlightTrigger_SuppressesANewDispatch_ButStillPlans()
    {
        var outcome = DepartureAutomationCore.Evaluate(Ready() with { InFlightServiceId = "Refueling" });

        Assert.NotNull(outcome.Plan);
        Assert.Null(outcome.Trigger);
    }

    [Fact]
    public void AllServicesDone_ReportsAllDone()
    {
        var cycles = new Dictionary<string, DepartureCycleView>
        {
            ["Refueling"] = new(true, true, true, true),
        };
        var outcome = DepartureAutomationCore.Evaluate(Ready(cycles));

        Assert.True(outcome.AllDone);
        Assert.Null(outcome.Trigger);
    }

    [Fact]
    public void Force_IsConsumed_EvenWhenNothingIsEligible()
    {
        var outcome = DepartureAutomationCore.Evaluate(Ready() with
        {
            Forced = true,
            MirrorServices = Services(("Refueling", GsxServiceState.NotAvailable, false)),
        });

        Assert.True(outcome.ConsumedForce);
        Assert.Null(outcome.Trigger);
    }
}
