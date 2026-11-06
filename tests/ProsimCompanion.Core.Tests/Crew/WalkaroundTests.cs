using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Crew;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.TechLog;
using ProsimCompanion.Core.Tests.Speech;
using ProsimCompanion.Core.Tests.TechLog;
using ProsimCompanion.Speech.Crew;
using ProsimCompanion.Speech.TechLog;
using Xunit;

namespace ProsimCompanion.Core.Tests.Crew;

/// <summary>The FO walkaround (issue #196): the state machine with the clock, the finding
/// draw, the outcome routing, the availability store and the pre-filled raise path.</summary>
public sealed class WalkaroundTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);

    private static WalkaroundOptions Options(bool enabled = true, int min = 6, int max = 10)
        => new() { Enabled = enabled, MinMinutes = min, MaxMinutes = max };

    private static WalkaroundSample Sample(
        DateTimeOffset? now = null, FlightPhase phase = FlightPhase.Preflight, bool live = true, bool parked = true,
        bool enginesOff = true, bool prepComplete = true, GsxServiceStage? refuel = GsxServiceStage.Waiting)
        => new(phase, live, parked, enginesOff, prepComplete, refuel, now ?? T0);

    /// <summary>A core that saw "not ready" once (the normal cold start), so the next ready
    /// sample is an edge.</summary>
    private static WalkaroundCore Baselined()
    {
        var core = new WalkaroundCore();
        core.Evaluate(Sample(prepComplete: false), Options(), () => 0);
        return core;
    }

    [Fact]
    public void FirstObservation_AlreadyReady_OnlyBaselines_NeverAnnounces()
    {
        var core = new WalkaroundCore();
        Assert.Equal(WalkaroundAction.None, core.Evaluate(Sample(), Options(), () => 0)); // a mid-turnaround restart
        Assert.Equal(WalkaroundAction.None, core.Evaluate(Sample(now: T0.AddMinutes(1)), Options(), () => 0));
        Assert.Equal(WalkaroundState.Idle, core.State);
    }

    [Fact]
    public void ReadyEdge_Announces_ThenReportsAfterTheAwayWindow_OncePerCycle()
    {
        var core = Baselined();
        Assert.Equal(WalkaroundAction.Announce, core.Evaluate(Sample(), Options(min: 6, max: 10), () => 0.5));
        Assert.Equal(WalkaroundState.Departed, core.State);
        Assert.Equal(T0.AddMinutes(8), core.ReportDueUtc); // 6 + 0.5 × 4

        Assert.Equal(WalkaroundAction.None, core.Evaluate(Sample(now: T0.AddMinutes(5)), Options(), () => 0));
        Assert.Equal(WalkaroundAction.Report, core.Evaluate(Sample(now: T0.AddMinutes(8)), Options(), () => 0));
        Assert.Equal(WalkaroundState.Reported, core.State);
        Assert.Equal(TimeSpan.FromMinutes(8), core.Duration(T0.AddMinutes(8)));

        // Done for the cycle: a ready edge again (prep reset and complete) is NOT a second walk.
        Assert.Equal(WalkaroundAction.None, core.Evaluate(Sample(now: T0.AddMinutes(9)), Options(), () => 0));
        Assert.Equal(WalkaroundState.Done, core.State);
        core.Evaluate(Sample(now: T0.AddMinutes(10), prepComplete: false), Options(), () => 0);
        Assert.Equal(WalkaroundAction.None, core.Evaluate(Sample(now: T0.AddMinutes(11)), Options(), () => 0));
    }

    [Fact]
    public void RefuelCalled_WhileWalking_Abandons()
    {
        var core = Baselined();
        core.Evaluate(Sample(), Options(), () => 0);
        Assert.Equal(WalkaroundAction.Abandon, core.Evaluate(Sample(now: T0.AddMinutes(2), refuel: GsxServiceStage.Called), Options(), () => 0));
        Assert.Equal(WalkaroundState.Abandoned, core.State);
        Assert.Equal(WalkaroundAbandonReason.RefuelCalled, core.LastAbandonReason);
        Assert.Equal(WalkaroundAction.None, core.Evaluate(Sample(now: T0.AddMinutes(9)), Options(), () => 0));
        Assert.Equal(WalkaroundState.Done, core.State);
    }

    [Fact]
    public void EngineStart_WhileWalking_Abandons()
    {
        var core = Baselined();
        core.Evaluate(Sample(), Options(), () => 0);
        Assert.Equal(WalkaroundAction.Abandon, core.Evaluate(Sample(now: T0.AddMinutes(3), enginesOff: false), Options(), () => 0));
        Assert.Equal(WalkaroundAbandonReason.EngineRunning, core.LastAbandonReason);
    }

    // Review 2026-11-05: the abandon names what happened — "the refuel's been called" was the
    // only line, untrue for boarding, an engine start, a prep flip or a live blip.
    [Fact]
    public void AbandonReason_NamesTheFirstFailingCondition_AndTheLineMatches()
    {
        Assert.Equal(WalkaroundAbandonReason.RefuelCalled, WalkaroundCore.AbandonReason(Sample(refuel: GsxServiceStage.Requested, enginesOff: false)));
        Assert.Equal(WalkaroundAbandonReason.BoardingStarted, WalkaroundCore.AbandonReason(Sample() with { BoardingObserved = true }));
        Assert.Equal(WalkaroundAbandonReason.EngineRunning, WalkaroundCore.AbandonReason(Sample(enginesOff: false)));
        Assert.Equal(WalkaroundAbandonReason.PrepNotComplete, WalkaroundCore.AbandonReason(Sample(prepComplete: false)));
        Assert.Equal(WalkaroundAbandonReason.FlightNotLive, WalkaroundCore.AbandonReason(Sample() with { Live = false }));
        Assert.Equal(WalkaroundAbandonReason.LeftTheGate, WalkaroundCore.AbandonReason(Sample()));

        Assert.Equal(WalkaroundService.AbandonLine, WalkaroundService.AbandonLineFor(WalkaroundAbandonReason.RefuelCalled));
        Assert.Contains("boarding", WalkaroundService.AbandonLineFor(WalkaroundAbandonReason.BoardingStarted), StringComparison.Ordinal);
        Assert.Contains("engine", WalkaroundService.AbandonLineFor(WalkaroundAbandonReason.EngineRunning), StringComparison.Ordinal);
        Assert.DoesNotContain("refuel", WalkaroundService.AbandonLineFor(WalkaroundAbandonReason.PrepNotComplete), StringComparison.Ordinal);
    }

    [Fact]
    public void FlightCycleReset_ArmsTheNextCycle_OnItsOwnReadyEdge()
    {
        var core = Baselined();
        core.Evaluate(Sample(), Options(), () => 0);
        core.Evaluate(Sample(now: T0.AddMinutes(10)), Options(), () => 0); // report
        core.OnFlightCycleReset();
        Assert.Equal(WalkaroundState.Idle, core.State);

        // The turnaround: prep runs again — its completion is the edge.
        Assert.Equal(WalkaroundAction.Announce, core.Evaluate(Sample(now: T0.AddHours(3)), Options(), () => 0));
    }

    [Fact]
    public void Disabled_NeverAnnounces_AndTheGatesHold()
    {
        Assert.Equal(WalkaroundAction.None, Baselined().Evaluate(Sample(), Options(enabled: false), () => 0));
        Assert.False(WalkaroundCore.IsReady(Sample(live: false)));
        Assert.False(WalkaroundCore.IsReady(Sample(phase: FlightPhase.TaxiOut)));
        Assert.False(WalkaroundCore.IsReady(Sample(parked: false)));
        Assert.False(WalkaroundCore.IsReady(Sample(enginesOff: false)));
        Assert.False(WalkaroundCore.IsReady(Sample(prepComplete: false)));
        Assert.True(WalkaroundCore.IsReady(Sample()));
        Assert.Equal(TimeSpan.FromMinutes(6), WalkaroundCore.AwayWindow(Options(min: 6, max: 4), 1)); // max below min reads as min
    }

    // ---- the draw ----

    private static WalkaroundFinding Finding(string id, WalkaroundSeverity severity = WalkaroundSeverity.Deferrable, WalkaroundConditions? conditions = null, double weight = 1, bool deice = false)
        => new(id, id + " spoken", severity, severity == WalkaroundSeverity.Cosmetic ? null : id + " title", MelCategory.C, conditions ?? WalkaroundConditions.None, weight, deice);

    [Fact]
    public void Draw_HonoursTheChance_TheConditions_AndTheNoGoExclusion()
    {
        var bank = new[]
        {
            Finding("plain"),
            Finding("icing", WalkaroundSeverity.Cosmetic, new WalkaroundConditions(true, false, false, false), deice: true),
            Finding("birds", conditions: new WalkaroundConditions(false, false, false, true)),
            Finding("nogo", WalkaroundSeverity.NoGo),
        };
        var dry = WalkaroundConditions.None;

        // Chance: the first roll is the dice; 0.5 against 0.15 = nothing.
        var rolls = new Queue<double>([0.5]);
        Assert.Null(WalkaroundFindingDraw.Draw(bank, dry, 0.15, false, rolls.Dequeue));
        Assert.Null(WalkaroundFindingDraw.Draw(bank, dry, 0, false, () => 0));

        // Dry day, noGo excluded: only "plain" qualifies.
        rolls = new Queue<double>([0.1, 0.9]);
        Assert.Equal("plain", WalkaroundFindingDraw.Draw(bank, dry, 0.15, false, rolls.Dequeue)!.Id);

        // Icing day after a landing: icing and birds join; noGo allowed joins too.
        var icy = new WalkaroundConditions(true, true, false, true);
        var ids = new HashSet<string>();
        for (var i = 0; i < 40; i++)
        {
            var dice = new Random(i);
            var picked = WalkaroundFindingDraw.Draw(bank, icy, 1, true, () => dice.NextDouble());
            ids.Add(picked!.Id);
        }

        Assert.Superset(new HashSet<string> { "plain", "icing", "birds", "nogo" }, ids);
        Assert.Contains("icing", ids);
        Assert.DoesNotContain("nogo", Enumerable.Range(0, 40).Select(i => { var d = new Random(i); return WalkaroundFindingDraw.Draw(bank, icy, 1, false, () => d.NextDouble())!.Id; }));
    }

    [Fact]
    public void FindingsFile_Parses_AndNormalises()
    {
        var result = WalkaroundFindingsFile.Parse("""
            { "findings": [
              { "id": "a", "spoken": "A", "severity": "NoGo", "techLogTitle": "T", "techLogCategory": "b", "conditions": [ "Night", "afterLanding" ], "weight": 2.5 },
              { "id": "b", "spoken": "B", "deice": true, "conditions": [ "icing" ] },
              { "id": "a", "spoken": "dup" },
              { "spoken": "no id" }
            ] }
            """);

        Assert.Equal(2, result.Findings.Count);
        Assert.Equal(2, result.Problems.Count);
        var a = result.Findings[0];
        Assert.Equal((WalkaroundSeverity.NoGo, "T", MelCategory.B, 2.5), (a.Severity, a.TechLogTitle, a.TechLogCategory, a.Weight));
        Assert.True(a.Conditions.Night && a.Conditions.AfterLanding && !a.Conditions.Icing);
        Assert.True(result.Findings[1].Deice);
        Assert.Equal(WalkaroundSeverity.Cosmetic, result.Findings[1].Severity);
    }

    // ---- the outcome routing ----

    [Fact]
    public void Outcome_IcingGoesToDeice_DeferrableAsks_NoGoRaisesOnlyWhenAllowed_CosmeticEndsThere()
    {
        Assert.Equal(WalkaroundOutcome.None, WalkaroundService.Outcome(null, false));
        Assert.Equal(WalkaroundOutcome.Deice, WalkaroundService.Outcome(Finding("ice", WalkaroundSeverity.Cosmetic, deice: true), false));
        Assert.Equal(WalkaroundOutcome.Ask, WalkaroundService.Outcome(Finding("tyre"), false));
        Assert.Equal(WalkaroundOutcome.Cosmetic, WalkaroundService.Outcome(Finding("pitot", WalkaroundSeverity.Cosmetic), false));
        Assert.Equal(WalkaroundOutcome.Raise, WalkaroundService.Outcome(Finding("leak", WalkaroundSeverity.NoGo), true));
        Assert.Equal(WalkaroundOutcome.Cosmetic, WalkaroundService.Outcome(Finding("leak", WalkaroundSeverity.NoGo), false)); // never drawn without allowNoGo; routed harmlessly if it were
        Assert.Equal(WalkaroundService.ReportTemplate + " tyre spoken", WalkaroundService.ReportText(Finding("tyre")));
    }

    // ---- availability ----

    [Fact]
    public void AvailabilityStore_AwayAndBack()
    {
        var store = new FoAvailabilityStore();
        Assert.False(store.IsAway);
        store.SetAway(FoAvailabilityStore.WalkaroundReason, T0);
        Assert.True(store.IsAway);
        Assert.Equal((FoAvailabilityStore.WalkaroundReason, T0), (store.Snapshot().Reason, store.Snapshot().SinceUtc));
        store.Clear();
        Assert.False(store.IsAway);
    }

    // ---- the pre-filled raise keeps its confirmation ----

    [Fact]
    public async Task PrefilledRaise_ReadsBack_AndRaisesOnlyOnAffirm()
    {
        var dir = Directory.CreateTempSubdirectory("pc-walkaround-raise-").FullName;
        try
        {
            var options = new TechLogOptions { Path = Path.Combine(dir, "techlog.json") };
            var eventLog = new JsonlEventLog(Path.Combine(dir, "sessions"), NullLogger<JsonlEventLog>.Instance);
            using var techLog = new TechLogService(OptionsSupport.Monitor(options), eventLog, new FakePhaseSource(), NullLogger<TechLogService>.Instance, new TestTimeProvider());
            techLog.Start();
            var io = new FakeDialogueIo();
            var core = new TechLogDialogueCore(techLog, io, eventLog);

            io.Replies.Enqueue("negative");
            Assert.False(await core.RunPrefilledRaiseAsync("Static discharger missing, right elevator", MelCategory.D, "walkaround:static-wick-missing", CancellationToken.None));
            Assert.Empty(techLog.OpenDefects);
            Assert.Contains(io.Spoken, s => s.StartsWith("Logging Static discharger missing", StringComparison.Ordinal) && s.EndsWith("Confirm?", StringComparison.Ordinal));
            Assert.Single(io.Listens); // no title listen, no category question — only the confirm

            io.Replies.Enqueue("affirm");
            Assert.True(await core.RunPrefilledRaiseAsync("Static discharger missing, right elevator", MelCategory.D, "walkaround:static-wick-missing", CancellationToken.None));
            var defect = Assert.Single(techLog.OpenDefects);
            Assert.Equal(("walkaround:static-wick-missing", MelCategory.D), (defect.Source, defect.Category));
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    // ---- restart and boarding (ADR-0019 §9, issue #220: a restart is invisible; boarding started blocks the walk) ----

    [Fact]
    public void BoardingStarted_IsNeverReady()
    {
        Assert.False(WalkaroundCore.IsReady(Sample() with { BoardingObserved = true }));
        var core = Baselined();
        Assert.Equal(WalkaroundAction.None, core.Evaluate(Sample() with { BoardingObserved = true }, Options(), () => 0));
        Assert.Equal(WalkaroundState.Idle, core.State);
    }

    [Theory]
    [InlineData(GsxServiceStage.Called, true)]
    [InlineData(GsxServiceStage.Requested, true)]
    [InlineData(GsxServiceStage.Active, true)]
    [InlineData(GsxServiceStage.Completed, true)]
    [InlineData(GsxServiceStage.Waiting, false)]
    [InlineData(GsxServiceStage.Held, false)]
    [InlineData(GsxServiceStage.Skipped, false)]
    [InlineData(null, false)]
    public void BoardingStarted_IsAnyStageFromTheTriggerOn(GsxServiceStage? stage, bool started)
    {
        Assert.Equal(started, WalkaroundCore.BoardingStarted(stage));
    }

    [Fact]
    public void StaggeredStartup_WithoutSnapshot_StillArmsOnTheEdge()
    {
        // The 2026-11-01 signature: the first ticks read "not ready" (phase/prep not in yet),
        // the resync seeds prep Complete seconds later — an edge the baseline cannot tell apart.
        var core = new WalkaroundCore();
        Assert.Equal(WalkaroundAction.None, core.Evaluate(Sample(prepComplete: false), Options(), () => 0));
        Assert.Equal(WalkaroundAction.Announce, core.Evaluate(Sample(), Options(), () => 0));
    }

    [Fact]
    public void RestoredIdleCycle_StaysArmed_AndWalksOnTheReadyState_UnlessBoardingStarted()
    {
        // ADR-0019 §9: a restart is invisible — an idle cycle restored from the snapshot walks
        // on the ready state it finds (the 2026-11-01 staggered startup counts as its edge)...
        var core = new WalkaroundCore();
        Assert.Equal(WalkaroundRestartOutcome.StillPossible, core.AdoptRestart(new WalkaroundSnapshotState(WalkaroundState.Idle, null, null)));
        Assert.Equal(WalkaroundState.Idle, core.State);
        Assert.Equal(WalkaroundAction.None, core.Evaluate(Sample(prepComplete: false), Options(), () => 0));
        Assert.Equal(WalkaroundAction.Announce, core.Evaluate(Sample(), Options(), () => 0));

        // ...but never once boarding has started (#196's rule that stays: the seeded Completed
        // of the resync reads as started too).
        var blocked = new WalkaroundCore();
        blocked.AdoptRestart(null);
        Assert.Equal(WalkaroundAction.None, blocked.Evaluate(Sample(prepComplete: false), Options(), () => 0));
        Assert.Equal(WalkaroundAction.None, blocked.Evaluate(Sample() with { BoardingObserved = true }, Options(), () => 0));
        Assert.Equal(WalkaroundState.Idle, blocked.State);
    }

    [Theory]
    [InlineData(WalkaroundState.Departed, WalkaroundRestartOutcome.WalkEnded)]
    [InlineData(WalkaroundState.Reported, WalkaroundRestartOutcome.AlreadyDone)]
    [InlineData(WalkaroundState.Abandoned, WalkaroundRestartOutcome.AlreadyDone)]
    [InlineData(WalkaroundState.Done, WalkaroundRestartOutcome.AlreadyDone)]
    public void RestoredWalkInProgressOrDone_EndsTheWalk_NeverRepeatsIt(WalkaroundState saved, WalkaroundRestartOutcome expected)
    {
        var core = new WalkaroundCore();
        var outcome = core.AdoptRestart(new WalkaroundSnapshotState(saved, T0, T0.AddMinutes(7)));

        Assert.Equal(expected, outcome);
        Assert.Equal(WalkaroundState.Done, core.State);
        Assert.Null(core.DepartedAtUtc);
        Assert.Null(core.ReportDueUtc);

        // No edge is acted on this cycle...
        Assert.Equal(WalkaroundAction.None, core.Evaluate(Sample(prepComplete: false), Options(), () => 0));
        Assert.Equal(WalkaroundAction.None, core.Evaluate(Sample(), Options(), () => 0));
        Assert.Equal(WalkaroundAction.None, core.Evaluate(Sample(now: T0.AddMinutes(11)), Options(), () => 0));
        Assert.Equal(WalkaroundState.Done, core.State);

        // ...and the next departure cycle arms again on its own edge.
        core.OnFlightCycleReset();
        Assert.Equal(WalkaroundAction.Announce, core.Evaluate(Sample(), Options(), () => 0));
    }

    [Fact]
    public void SnapshotState_RoundTripsThroughJson()
    {
        var state = new WalkaroundSnapshotState(WalkaroundState.Departed, T0, T0.AddMinutes(7));
        var node = JsonSerializer.SerializeToNode(state, FlightStateSnapshotStore.JsonOptions);
        var back = node!.Deserialize<WalkaroundSnapshotState>(FlightStateSnapshotStore.JsonOptions);
        Assert.Equal(state, back);
    }
}
