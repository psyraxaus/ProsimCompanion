using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProsimCompanion.Core.Commands;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Debrief;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Scenarios;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.TechLog;
using ProsimCompanion.Core.Tests.Speech;
using ProsimCompanion.Core.Tests.TechLog;
using ProsimCompanion.Prosim.DataRefs;
using ProsimCompanion.Prosim.Failures;
using ProsimCompanion.Speech.Abnormals;
using ProsimCompanion.Speech.Scenarios;
using Xunit;

namespace ProsimCompanion.Core.Tests.Scenarios;

/// <summary>Scenario mode (issue #197, ADR-0018): the gate, the scheduler tables, the state
/// file, the catalogue, the decision question, the replay safety and the service's
/// unconditional clear-all.</summary>
[Collection("ProsimFailureGate")]
public sealed class ScenarioTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Directory.CreateTempSubdirectory("pc-scenario-").FullName;

    public void Dispose()
    {
        ProsimFailureGate.InstallCatalogue([]);
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static ScenarioEntry Entry(string id, string abnormal = "elec-gen-1-fault", string failure = "Generator 1 fault", bool verified = true,
        ScenarioSeverity severity = ScenarioSeverity.Minor, bool takeoff = false, double weight = 1, string? wear = null, params FlightPhase[] phases)
        => new(id, id, abnormal, failure, verified, phases.Length == 0 ? [FlightPhase.Cruise] : phases, weight, severity, takeoff, null, null, "{destination} / {alternate}", wear);

    private static ScenarioOptions Options(bool enabled = true, double chance = 1, bool major = false, bool takeoff = false, int floor = 1500)
        => new() { Enabled = enabled, PerFlightChance = chance, AllowMajor = major, TakeoffScenarios = takeoff, MinAltitudeFt = floor };

    private static ScenarioPlanInputs Inputs(bool rto = false, bool efato = false, params string[] wear) => new(rto, efato, wear, 90);

    // ---- the gate ----

    [Fact]
    public void FailureGate_PassesOnlyCatalogueNamesThatProSimAlsoLists_AndHasNoDatarefPath()
    {
        ProsimFailureGate.InstallCatalogue(["Generator 1 fault", " Pack 1 fault "]);
        var live = new HashSet<string>(StringComparer.Ordinal) { "Generator 1 fault", "Engine 1 fire", "system.config.Config.autoFailure" };

        Assert.True(ProsimFailureGate.IsAllowed("Generator 1 fault", live));
        Assert.False(ProsimFailureGate.IsAllowed("Pack 1 fault", live)); // in the catalogue, not live
        Assert.False(ProsimFailureGate.IsAllowed("Engine 1 fire", live)); // live, not in the catalogue
        Assert.False(ProsimFailureGate.IsAllowed("system.config.Config.autoFailure", live)); // never: not a failure, not in the catalogue
        Assert.False(ProsimFailureGate.IsAllowed("", live));
        Assert.Throws<InvalidOperationException>(() => ProsimFailureGate.EnsureAllowed("Engine 1 fire", live));

        // No dataref path through the type: every parameter is a failure name or the live list.
        var parameters = typeof(ProsimFailureGate).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .SelectMany(m => m.GetParameters()).Select(p => p.Name ?? "").ToList();
        Assert.All(parameters, name => Assert.DoesNotContain("dataref", name, StringComparison.OrdinalIgnoreCase));
        Assert.All(parameters, name => Assert.True(name.StartsWith("failure", StringComparison.Ordinal) || name.StartsWith("live", StringComparison.Ordinal) || name.StartsWith("verified", StringComparison.Ordinal), name));

        // And the dataref gate keeps ProSim's random-failure generator closed.
        Assert.False(ProsimWriteGate.IsAllowed("system.config.Config.autoFailure"));
        Assert.False(ProsimWriteGate.IsAllowed("system.config.Config.failureRate"));
        Assert.False(ProsimWriteGate.IsAllowed("system.config.Config.engineAutoRestart"));
    }

    // ---- the scheduler ----

    [Fact]
    public void Allowed_FiltersUnverified_Major_AndTakeoff_AndDoublesAnOpenWearItem()
    {
        var catalogue = new[]
        {
            Entry("ok"),
            Entry("unverified", verified: false),
            Entry("major", severity: ScenarioSeverity.Major),
            Entry("efato", abnormal: "drill-engine-failure-after-v1", takeoff: true, phases: FlightPhase.TakeoffRoll),
            Entry("rto-cause", abnormal: "eng-1-fire", takeoff: true, phases: FlightPhase.TakeoffRoll),
            Entry("aged", wear: "Generator 1 fault"),
        };

        var plain = ScenarioScheduler.Allowed(catalogue, Options(), Inputs());
        Assert.Equal(["ok", "aged"], plain.Select(a => a.Entry.Id));
        Assert.Equal(1, plain.Single(a => a.Entry.Id == "aged").Weight);

        var withMajor = ScenarioScheduler.Allowed(catalogue, Options(major: true), Inputs());
        Assert.Contains(withMajor, a => a.Entry.Id == "major");

        var takeoffOnly = ScenarioScheduler.Allowed(catalogue, Options(takeoff: true), Inputs(rto: false, efato: true));
        Assert.Contains(takeoffOnly, a => a.Entry.Id == "efato");
        Assert.DoesNotContain(takeoffOnly, a => a.Entry.Id == "rto-cause"); // RTO off

        var aged = ScenarioScheduler.Allowed(catalogue, Options(), Inputs(wear: "Generator 1 fault"));
        Assert.Equal(2, aged.Single(a => a.Entry.Id == "aged").Weight);
    }

    [Fact]
    public void Plan_RollsTheChance_PicksByWeight_AndPlacesTheMomentInsideThePhase()
    {
        var catalogue = new[] { Entry("a", weight: 1), Entry("b", weight: 3, phases: FlightPhase.Climb) };

        Assert.Null(ScenarioScheduler.Plan(catalogue, Options(chance: 0.1), Inputs(), T0, "s", () => 0.5)); // chance missed
        Assert.Null(ScenarioScheduler.Plan(catalogue, Options(enabled: false), Inputs(), T0, "s", () => 0));

        var rolls = new Queue<double>([0.0, 0.9, 0.0, 0.5]); // chance hit, weight point in b's share, phase 0, moment mid-window
        var plan = ScenarioScheduler.Plan(catalogue, Options(), Inputs(), T0, "session-1", rolls.Dequeue)!;
        Assert.Equal("b", plan.ScenarioId);
        Assert.Equal(FlightPhase.Climb, plan.Phase);
        Assert.Equal(ScenarioPlanState.Planned, plan.State);
        Assert.InRange(plan.FireAfterSeconds, 0.2 * 12 * 60, 0.8 * 12 * 60);
        Assert.Equal("session-1", plan.SessionId);
    }

    private static ScenarioPlan CruisePlan(int after = 60) => new()
    {
        ScenarioId = "gen-1-fault", Title = "Generator 1 fault", AbnormalId = "elec-gen-1-fault", FailureName = "Generator 1 fault",
        Phase = FlightPhase.Cruise, FireAfterSeconds = after, PlannedAtUtc = T0, State = ScenarioPlanState.Planned,
    };

    private static ScenarioTick Tick(FlightPhase phase = FlightPhase.Cruise, bool live = true, DateTimeOffset? since = null, double? agl = 35_000,
        bool abnormal = false, bool dialogue = false, bool noGo = false, bool sterile = false)
        => new(phase, live, T0.AddMinutes(5), since ?? T0, agl, abnormal, dialogue, noGo, sterile);

    [Fact]
    public void Evaluate_ArmsInThePhaseAfterTheMoment_AndEveryGateAloneHolds()
    {
        var options = Options();
        Assert.True(ScenarioScheduler.Evaluate(CruisePlan(), Tick(), options).Arm);

        Assert.False(ScenarioScheduler.Evaluate(CruisePlan(), Tick(phase: FlightPhase.Climb), options).Arm);
        Assert.False(ScenarioScheduler.Evaluate(CruisePlan(after: 600), Tick(), options).Arm); // the moment has not come
        Assert.False(ScenarioScheduler.Evaluate(CruisePlan(), Tick(live: false), options).Arm);
        Assert.Equal("scenario mode is off", ScenarioScheduler.Evaluate(CruisePlan(), Tick(), Options(enabled: false)).HoldReason);
        Assert.Contains("floor", ScenarioScheduler.Evaluate(CruisePlan(), Tick(agl: 1000), options).HoldReason);
        Assert.Contains("floor", ScenarioScheduler.Evaluate(CruisePlan(), Tick(agl: null), options).HoldReason);
        Assert.Equal("an abnormal is open", ScenarioScheduler.Evaluate(CruisePlan(), Tick(abnormal: true), options).HoldReason);
        Assert.Contains("dialogue", ScenarioScheduler.Evaluate(CruisePlan(), Tick(dialogue: true), options).HoldReason);
        Assert.Contains("no-go", ScenarioScheduler.Evaluate(CruisePlan(), Tick(noGo: true), options).HoldReason);
        Assert.Equal("sterile cockpit", ScenarioScheduler.Evaluate(CruisePlan(), Tick(sterile: true), options).HoldReason);

        // No double arm: an armed or fired plan never arms again.
        var armed = CruisePlan();
        armed.State = ScenarioPlanState.Armed;
        Assert.False(ScenarioScheduler.Evaluate(armed, Tick(), options).Arm);
    }

    [Fact]
    public void Evaluate_TakeoffScenario_ArmsOnTheGroundBeforeTheRoll_WithoutTheAltitudeFloor()
    {
        var plan = CruisePlan();
        plan.Takeoff = true;
        plan.Phase = FlightPhase.TakeoffRoll;
        Assert.True(ScenarioScheduler.Evaluate(plan, Tick(phase: FlightPhase.TaxiOut, agl: 0), Options()).Arm);
        Assert.False(ScenarioScheduler.Evaluate(plan, Tick(phase: FlightPhase.Preflight, agl: 0), Options()).Arm);
        Assert.False(ScenarioScheduler.Evaluate(plan, Tick(phase: FlightPhase.TaxiOut, agl: 0, abnormal: true), Options()).Arm);
    }

    // ---- the state file ----

    [Fact]
    public void StateFile_OnlyPlannedSurvives_ArmedOrFiredReportsACrash()
    {
        var file = new ScenarioStateFile(NullLogger<ScenarioStateFile>.Instance, Path.Combine(_dir, "state.json"));

        file.Save(CruisePlan());
        var planned = file.Load();
        Assert.NotNull(planned.Plan);
        Assert.False(planned.LeftArmed);

        var armed = CruisePlan();
        armed.State = ScenarioPlanState.Armed;
        armed.Handle = "h";
        file.Save(armed);
        var afterCrash = file.Load();
        Assert.Null(afterCrash.Plan);
        Assert.True(afterCrash.LeftArmed);

        var fired = CruisePlan();
        fired.State = ScenarioPlanState.Fired;
        file.Save(fired);
        Assert.True(file.Load().LeftArmed);

        file.Save(null);
        Assert.False(File.Exists(file.FilePath));
        File.WriteAllText(file.FilePath, "{ nope");
        Assert.Null(file.Load().Plan);
    }

    // ---- the catalogue ----

    [Fact]
    public void Catalogue_Parses_Normalises_AndNamesProblems()
    {
        var result = ScenarioCatalogueFile.Parse("""
            { "scenarios": [
              { "id": "a", "title": "A", "abnormalId": "pack-1-fault", "failureName": "Pack 1 fault", "verified": true, "phases": [ "Cruise", "Nowhere" ], "weight": 2, "severity": "Major", "clearAfterSeconds": 60, "techLogAfter": "T", "decision": "d", "wearTitle": "W" },
              { "id": "b", "abnormalId": "x", "failureName": "Y", "takeoff": true, "phases": [ "TakeoffRoll" ] },
              { "id": "c", "failureName": "no abnormal" }
            ] }
            """);

        Assert.Equal(2, result.Scenarios.Count);
        var a = result.Scenarios[0];
        Assert.Equal((ScenarioSeverity.Major, 60, "T", "W", true), (a.Severity, a.ClearAfterSeconds, a.TechLogAfter, a.WearTitle, a.Verified));
        Assert.Equal([FlightPhase.Cruise], a.Phases);
        Assert.True(result.Scenarios[1].Takeoff);
        Assert.False(result.Scenarios[1].Verified);
        Assert.Equal(2, result.Problems.Count); // the unknown phase and the entry without an abnormal
    }

    [Fact]
    public void ShippedCatalogue_NamesOnlyShippedAbnormals_AndShipsUnverified()
    {
        var root = FindRepoRoot();
        var json = File.ReadAllText(Path.Combine(root, "src", "ProsimCompanion.App", "config", "scenarios.json"));
        var abnormals = Directory.GetFiles(Path.Combine(root, "src", "ProsimCompanion.App", "config", "abnormals"), "*.json")
            .Select(f => JsonDocument.Parse(File.ReadAllText(f)).RootElement.GetProperty("id").GetString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = ScenarioCatalogueFile.Parse(json);
        Assert.Empty(result.Problems);
        Assert.True(result.Scenarios.Count >= 15);
        Assert.All(result.Scenarios, s => Assert.Contains(s.AbnormalId, abnormals));
        Assert.All(result.Scenarios, s => Assert.False(s.Verified)); // verified only on the rig (ADR-0018)
        Assert.DoesNotContain(result.Scenarios, s => s.Id.Contains("ap-", StringComparison.OrdinalIgnoreCase)); // no AP disconnect (owner, 2026-11-01)
    }

    // ---- the decision ----

    [Fact]
    public void DecisionStore_ThreeOutcomes_AndThePhrasesInterpretThem()
    {
        using var store = new ScenarioDecisionStore(new GroundOpsSignals());
        Assert.True(store.Ask("gen-1-fault", "Generator 1 fault", "We've lost GEN 1 — I'd suggest we continue; your call, captain."));
        Assert.True(store.Asking.QuestionStands);
        Assert.Equal(ScenarioDecisionStore.Continue, store.Outcome);

        Assert.True(store.Choose("divert", "voice"));
        Assert.Equal(ScenarioDecisionStore.Divert, store.Outcome);

        var choices = ScenarioDecisionStore.Choices;
        Assert.Equal(ScenarioDecisionStore.Divert, ScenarioDecisionPhrases.Interpret("we divert to the alternate", choices)!.Label);
        Assert.Equal(ScenarioDecisionStore.Return, ScenarioDecisionPhrases.Interpret("let's turn back", choices)!.Label);
        Assert.Equal(ScenarioDecisionStore.Continue, ScenarioDecisionPhrases.Interpret("affirm", choices)!.Label);
        Assert.Equal(ScenarioDecisionStore.Continue, ScenarioDecisionPhrases.Interpret("press on", choices)!.Label);
        Assert.Null(ScenarioDecisionPhrases.Interpret("say again", choices));

        Assert.Equal("Prague / Munich", ScenarioScheduler.DecisionLine("{destination} / {alternate}", "Munich", "Prague"));
        Assert.Contains("the alternate", ScenarioScheduler.DecisionLine("to {alternate}", null, null), StringComparison.Ordinal);
    }

    [Fact]
    public void DebriefTemplate_NamesTheScenario()
    {
        var facts = DebriefFacts.Empty with { BlockMinutes = 60, Scenario = "Generator 1 fault" };
        var text = DebriefTemplate.Build(facts, DebriefVerbosity.Full);
        Assert.Contains("Scenario today: Generator 1 fault.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Scenario", DebriefTemplate.Build(DebriefFacts.Empty with { BlockMinutes = 60 }, DebriefVerbosity.Full), StringComparison.Ordinal);
    }

    // ---- replay safety ----

    [Fact]
    public void ReplayRecordings_NeverArmAnything()
    {
        var recordings = Directory.GetFiles(Path.Combine(FindRepoRoot(), "tests", "ProsimCompanion.Core.Tests", "Flight", "Recordings"), "*.jsonl");
        Assert.NotEmpty(recordings);
        var options = Options();
        foreach (var path in recordings)
        {
            // A replay feeds the flight engine only; the scheduler is never constructed in one.
            // Driving the scheduler with the recording's own phases as a NOT-live tick (a
            // replay is never the live gate) must arm nothing, whatever the plan says.
            foreach (var line in File.ReadLines(path))
            {
                if (!line.Contains("\"phase-changed\"", StringComparison.Ordinal))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(line);
                var to = document.RootElement.GetProperty("payload").TryGetProperty("to", out var t) ? t.GetString() : null;
                if (to is null || !Enum.TryParse<FlightPhase>(to, true, out var phase))
                {
                    continue;
                }

                var plan = CruisePlan();
                plan.Phase = phase;
                plan.FireAfterSeconds = 0;
                var tick = new ScenarioTick(phase, Live: false, T0.AddHours(1), T0, 35_000, false, false, false, false);
                Assert.False(ScenarioScheduler.Evaluate(plan, tick, options).Arm, path);
            }
        }

        // And the replay type itself knows nothing of scenarios.
        Assert.DoesNotContain(typeof(FlightReplay).GetMethods().SelectMany(m => m.GetParameters()), p => p.ParameterType.Namespace?.Contains("Scenarios", StringComparison.Ordinal) == true);
    }

    // ---- the service: clear-all is unconditional, arming refuses when not live ----

    private sealed class FakeInjector : IFailureInjector
    {
        public bool IsAvailable => true;
        public List<string> Live { get; } = ["Generator 1 fault"];
        public Dictionary<string, string> Armed { get; } = [];
        public int ClearAllCalls { get; private set; }
        public Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(Live);
        public Task<string?> ArmAsync(string failureName, FailureArmCondition? condition, CancellationToken cancellationToken = default)
        {
            if (!Live.Contains(failureName))
            {
                return Task.FromResult<string?>(null);
            }

            var handle = Guid.NewGuid().ToString("N");
            Armed[handle] = failureName;
            return Task.FromResult<string?>(handle);
        }

        public Task<IReadOnlyList<ArmedFailureInfo>> ArmedAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ArmedFailureInfo>>([.. Armed.Select(a => new ArmedFailureInfo(a.Key, a.Value, true))]);
        public Task<bool> ClearAsync(string handle, CancellationToken cancellationToken = default) => Task.FromResult(Armed.Remove(handle));
        public Task<int> ClearAllAsync(CancellationToken cancellationToken = default)
        {
            ClearAllCalls++;
            var n = Armed.Count;
            Armed.Clear();
            return Task.FromResult(n);
        }
    }

    private (ScenarioService Service, FakeInjector Injector, ScenarioStore Store, FakePhaseSource Phases) CreateService(bool enabled)
    {
        File.WriteAllText(Path.Combine(_dir, ScenarioCatalogueBank.FileName), """
            { "scenarios": [ { "id": "gen-1-fault", "title": "Generator 1 fault", "abnormalId": "elec-gen-1-fault", "failureName": "Generator 1 fault", "verified": true, "phases": [ "Cruise" ], "decision": "continue" } ] }
            """);
        var phases = new FakePhaseSource();
        phases.SetLive(false);
        var arbiter = new FakeArbiter();
        var eventLog = new JsonlEventLog(Path.Combine(_dir, "sessions"), NullLogger<JsonlEventLog>.Instance);
        var monitor = new FailureMonitor(arbiter, Mock.Of<Core.Aircraft.IProsimDataRefs>(), phases, eventLog, NullLogger<FailureMonitor>.Instance);
        monitor.Load([new AbnormalDefinition { Id = "elec-gen-1-fault", Title = "Generator 1 fault" }]);
        var injector = new FakeInjector();
        var store = new ScenarioStore();
        var techLog = new Mock<ITechLogService>();
        techLog.SetupGet(t => t.OpenDefects).Returns([]);
        var service = new ScenarioService(
            OptionsSupport.Monitor(new ScenarioOptions { Enabled = enabled, PerFlightChance = 1 }),
            OptionsSupport.Monitor(new AbnormalsOptions()),
            SpeechTestSupport.SpeechMonitor(new SpeechOptions()),
            SpeechTestSupport.BriefingMonitor(new BriefingOptions()),
            phases, monitor, injector,
            new ScenarioCatalogueBank(NullLogger<ScenarioCatalogueBank>.Instance, configDirectory: _dir),
            new ScenarioStateFile(NullLogger<ScenarioStateFile>.Instance, Path.Combine(_dir, "state.json")),
            store, new ScenarioDecisionStore(new GroundOpsSignals()), techLog.Object, new GroundOpsSignals(), arbiter, eventLog,
            NullLogger<ScenarioService>.Instance)
        {
            Roll = () => 0,
            Now = () => T0,
        };
        service.Start();
        return (service, injector, store, phases);
    }

    [Fact]
    public async Task ClearAll_IsUnconditional_EvenWithScenarioModeOff()
    {
        var (service, injector, store, _) = CreateService(enabled: false);
        using (service)
        {
            injector.Armed["ios-1"] = "Engine 1 fire"; // set on the IOS, not by us
            var result = await service.ClearAllAsync("test");

            Assert.Equal(CommandOutcome.Success, result.Outcome);
            Assert.Equal(1, injector.ClearAllCalls);
            Assert.Empty(injector.Armed);
            Assert.Equal("off", store.Snapshot().State);
            Assert.StartsWith("cleared 1", store.Snapshot().LastClear, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Arm_RefusesWhenTheFlightIsNotLive_AndPlansOnlyWhenLive()
    {
        var (service, injector, _, phases) = CreateService(enabled: true);
        using (service)
        {
            var refused = await service.ArmAsync("gen-1-fault", "test");
            Assert.Equal(CommandOutcome.PreconditionFailed, refused.Outcome);
            Assert.Empty(injector.Armed);
            Assert.Null(service.TryPlan(T0)); // not live: nothing planned

            phases.SetLive(true);
            var plan = service.TryPlan(T0);
            Assert.NotNull(plan);
            Assert.Equal("gen-1-fault", plan!.ScenarioId);
            Assert.Equal(ScenarioPlanState.Planned, plan.State);
            Assert.Equal(CommandOutcome.PreconditionFailed, (await service.ArmAsync("unknown", "test")).Outcome);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProsimCompanion.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
