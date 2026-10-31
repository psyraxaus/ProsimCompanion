using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Commands;
using ProsimCompanion.Core.Commands.Handlers;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Scenarios;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.TechLog;
using ProsimCompanion.Core.VoiceReference;
using ProsimCompanion.Speech.Abnormals;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Llm;
using ProsimCompanion.Speech.Persona;
using ProsimCompanion.Speech.Questions;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Scenarios;

/// <summary>
/// Scenario mode (issue #197, ADR-0018): the shell around the pure <see cref="ScenarioScheduler"/>
/// and the one serialised failure-write path (<see cref="IScenarioControl"/>). At the first
/// live Preflight of a cycle it plans at most one scenario; a one-second tick asks the
/// scheduler whether the plan may arm; an arm is announce (session event) → a 3 s cancel
/// window → the injector's write → a verify read, all under one lock whichever surface asked.
/// The failure monitor detects the result exactly as an IOS failure; on the dialogue's end the
/// FO's decision turn is raised as a pick-one captain's question. Every armed failure is
/// cleared at the flight-cycle reset and at app exit, and "clear all failures" runs
/// unconditionally from voice, the STATUS page, the Stream Deck and the API.
/// </summary>
public sealed class ScenarioService : Core.Hosting.IStartupModule, IHostedService, IScenarioControl, IVoiceFeature, IDisposable
{
    public const string ArmingEvent = "scenario.arming";
    public const string ArmedEvent = "scenario.armed";
    public const string FiredEvent = "scenario.fired";
    public const string ClearedEvent = "scenario.cleared";

    private static readonly TimeSpan CancelWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan VerifyWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PlanMaxAge = TimeSpan.FromHours(6);

    private static readonly string[] ClearPhrases = ["clear all failures", "clear the failures", "reset all failures"];
    private static readonly string[] PlannedPhrases = ["any failures planned", "anything planned today", "any scenarios planned"];

    private readonly IOptionsMonitor<ScenarioOptions> _options;
    private readonly IOptionsMonitor<AbnormalsOptions> _abnormals;
    private readonly IOptionsMonitor<SpeechOptions> _speech;
    private readonly IOptionsMonitor<BriefingOptions> _briefing;
    private readonly IFlightPhaseSource _flight;
    private readonly FailureMonitor _monitor;
    private readonly IFailureInjector _injector;
    private readonly ScenarioCatalogueBank _catalogue;
    private readonly ScenarioStateFile _stateFile;
    private readonly ScenarioStore _store;
    private readonly ScenarioDecisionStore _decision;
    private readonly ITechLogService _techLog;
    private readonly GroundOpsSignals _signals;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<ScenarioService> _logger;
    private readonly OfpStore? _ofp;
    private readonly DiversionStore? _diversion;
    private readonly PersonaService? _persona;
    private readonly OpenAiChatClient? _llm;
    private readonly LlmHealthStore? _llmHealth;
    private readonly FoFactSource? _facts;
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();

    private Timer? _timer;
    private int _ticking;
    private ScenarioPlan? _plan;
    private DateTimeOffset? _phaseSinceUtc;
    private FlightPhase _lastPhase = FlightPhase.Unknown;
    private string? _lastHold;
    private CancellationTokenSource? _armCancel;
    private Timer? _transientTimer;
    private bool _decisionAsked;
    private string? _lastClear;
    private IReadOnlyList<string> _liveFailures = [];
    private bool _started;

    public ScenarioService(
        IOptionsMonitor<ScenarioOptions> options,
        IOptionsMonitor<AbnormalsOptions> abnormals,
        IOptionsMonitor<SpeechOptions> speech,
        IOptionsMonitor<BriefingOptions> briefing,
        IFlightPhaseSource flight,
        FailureMonitor monitor,
        IFailureInjector injector,
        ScenarioCatalogueBank catalogue,
        ScenarioStateFile stateFile,
        ScenarioStore store,
        ScenarioDecisionStore decision,
        ITechLogService techLog,
        GroundOpsSignals signals,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        ILogger<ScenarioService> logger,
        OfpStore? ofp = null,
        DiversionStore? diversion = null,
        PersonaService? persona = null,
        OpenAiChatClient? llm = null,
        LlmHealthStore? llmHealth = null,
        FoFactSource? facts = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(abnormals);
        ArgumentNullException.ThrowIfNull(speech);
        ArgumentNullException.ThrowIfNull(briefing);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(injector);
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(stateFile);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(techLog);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _abnormals = abnormals;
        _speech = speech;
        _briefing = briefing;
        _flight = flight;
        _monitor = monitor;
        _injector = injector;
        _catalogue = catalogue;
        _stateFile = stateFile;
        _store = store;
        _decision = decision;
        _techLog = techLog;
        _signals = signals;
        _arbiter = arbiter;
        _eventLog = eventLog;
        _logger = logger;
        _ofp = ofp;
        _diversion = diversion;
        _persona = persona;
        _llm = llm;
        _llmHealth = llmHealth;
        _facts = facts;
    }

    /// <summary>Roster declaration: "clear all failures" always works, whoever is in the
    /// cockpit (issue #196) and whatever scenario.enabled says.</summary>
    public static VoiceFeatureDeclaration Declared { get; } = new()
    {
        Precedence = 310,
        Gate = VoiceFeatureGate.AlwaysOn,
        AvailableWhileFoAway = true,
        Description = new DescribedVoiceReference("scenario", "voiceReference.group.scenario.title", VoiceReferenceTab.FirstOfficer, VoiceSpeaker.FirstOfficer,
        [
            new(["clear all failures", "clear the failures", "reset all failures"], "voiceReference.group.scenario.clearAll", "All failures cleared, captain."),
            new(["any failures planned", "anything planned today"], "voiceReference.group.scenario.planned", "Nothing I can tell you about, captain."),
        ]),
    };

    VoiceFeatureDeclaration IVoiceFeature.Declaration => Declared;

    public bool Enabled => true;

    public IEnumerable<string> Phrases => ClearPhrases.Concat(PlannedPhrases);

    public bool ValueParse => false;

    /// <summary>The dice, injectable for deterministic tests.</summary>
    internal Func<double> Roll { get; init; } = () => Random.Shared.NextDouble();

    /// <summary>The clock, replaceable by tests.</summary>
    internal Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>The current plan, for tests and the API.</summary>
    public ScenarioPlan? Plan
    {
        get
        {
            lock (_gate)
            {
                return _plan;
            }
        }
    }

    // ---- lifecycle ----

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _flight.PhaseChanged += OnPhaseChanged;
        _monitor.FailureDetected += OnFailureDetected;
        _monitor.AbnormalEnded += OnAbnormalEnded;
        _signals.FlightCycleReset += OnFlightCycleReset;
        _decision.Answered += OnDecisionAnswered;

        var load = _stateFile.Load();
        if (load.LeftArmed)
        {
            _logger.LogWarning("Scenario state shows a failure armed at the last exit — clearing all failures once");
            _ = Task.Run(() => ClearAllAsync("startup", _shutdown.Token));
        }
        else if (load.Plan is { } plan && Now() - plan.PlannedAtUtc < PlanMaxAge)
        {
            lock (_gate)
            {
                _plan = plan; // Planned only — re-judged against the safety floor before it fires
            }

            _logger.LogInformation("Scenario plan resumed: {Scenario} in {Phase}", plan.Title, plan.Phase);
        }
        else
        {
            _stateFile.Save(null);
        }

        if (!_options.CurrentValue.Enabled)
        {
            _logger.LogInformation("Scenario mode disabled (scenario.enabled) — 'clear all failures' still works");
        }

        _timer = new Timer(_ => Tick(), null, 1000, 1000);
        _ = Task.Run(RefreshLiveFailuresAsync);
        UpdateStore();
    }

    Task IHostedService.StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>App exit: every failure we armed is cleared and the clear verified.</summary>
    async Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        ScenarioPlan? plan;
        lock (_gate)
        {
            plan = _plan;
        }

        if (plan is { State: ScenarioPlanState.Armed or ScenarioPlanState.Fired })
        {
            await ClearOursAsync("exit", cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _flight.PhaseChanged -= OnPhaseChanged;
        _monitor.FailureDetected -= OnFailureDetected;
        _monitor.AbnormalEnded -= OnAbnormalEnded;
        _signals.FlightCycleReset -= OnFlightCycleReset;
        _decision.Answered -= OnDecisionAnswered;
        _timer?.Dispose();
        _transientTimer?.Dispose();
        _shutdown.Cancel();
        _shutdown.Dispose();
        _writes.Dispose();
    }

    // ---- planning ----

    private void OnPhaseChanged(object? sender, FlightPhaseChangedEventArgs e)
    {
        var now = Now();
        lock (_gate)
        {
            _phaseSinceUtc = now;
            _lastPhase = e.Current;
        }

        if (e.Current == FlightPhase.Preflight && e.Previous != FlightPhase.Preflight)
        {
            TryPlan(now);
        }
    }

    /// <summary>Plans the flight's scenario at Preflight — once per cycle, only with a live
    /// flight. Exposed for tests.</summary>
    public ScenarioPlan? TryPlan(DateTimeOffset nowUtc)
    {
        var options = _options.CurrentValue;
        lock (_gate)
        {
            if (_plan is not null || !options.Enabled || !_flight.IsLive)
            {
                return _plan;
            }

            var catalogue = _catalogue.Entries.Where(e => Known(e)).ToList();
            var inputs = new ScenarioPlanInputs(
                _abnormals.CurrentValue.RtoEnabled,
                _abnormals.CurrentValue.EfatoEnabled,
                OpenTitles(),
                _ofp?.Current?.EstimatedEnroute?.TotalMinutes);
            var plan = ScenarioScheduler.Plan(catalogue, options, inputs, nowUtc, Path.GetFileNameWithoutExtension(_eventLog.Path), Roll);
            _plan = plan;
            _decisionAsked = false;
            _stateFile.Save(plan);
            if (plan is not null)
            {
                _eventLog.Record("scenario.planned", new { scenario = plan.ScenarioId, title = plan.Title, phase = plan.Phase.ToString(), afterSeconds = plan.FireAfterSeconds, shown = options.ShowPlanned });
                _logger.LogInformation("Scenario planned: {Scenario} in {Phase} after {Seconds} s", plan.Title, plan.Phase, plan.FireAfterSeconds);
            }
        }

        UpdateStore();
        return Plan;
    }

    private bool Known(ScenarioEntry entry)
    {
        if (_monitor.HasAbnormal(entry.AbnormalId))
        {
            return true;
        }

        _logger.LogWarning("Scenario {Id} names abnormal {Abnormal}, which is not loaded — refused", entry.Id, entry.AbnormalId);
        return false;
    }

    private IReadOnlyCollection<string> OpenTitles()
    {
        try
        {
            return _techLog.OpenDefects.Select(d => d.Title).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Tech log read failed");
            return [];
        }
    }

    // ---- the tick ----

    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            ProcessTick(Now());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Scenario tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    /// <summary>One evaluation with the clock supplied: arms the plan when the scheduler says so.
    /// Returns the scheduler's decision, for tests.</summary>
    public ScenarioDecision ProcessTick(DateTimeOffset nowUtc)
    {
        ScenarioPlan? plan;
        lock (_gate)
        {
            plan = _plan;
        }

        if (plan is not { State: ScenarioPlanState.Planned })
        {
            return ScenarioDecision.Wait;
        }

        var decision = ScenarioScheduler.Evaluate(plan, BuildTick(nowUtc), _options.CurrentValue);
        if (decision.HoldReason is { } hold && hold != _lastHold)
        {
            _lastHold = hold;
            _logger.LogInformation("Scenario {Scenario} held: {Reason}", plan.Title, hold);
        }

        if (decision.Arm)
        {
            _ = Task.Run(() => ArmPlanAsync(plan, "scheduler", _shutdown.Token));
        }

        return decision;
    }

    /// <summary>The tick as the scheduler sees it — exposed for the tests' tables.</summary>
    public ScenarioTick BuildTick(DateTimeOffset nowUtc)
    {
        var view = _flight.Snapshot();
        var data = view.Data;
        var context = new SpeechContext(view.Phase, data?.AltitudeFt ?? 0, data?.IsValid ?? false);
        DateTimeOffset? since;
        lock (_gate)
        {
            since = _lastPhase == view.Phase ? _phaseSinceUtc : null;
        }

        var noGo = false;
        try
        {
            noGo = _techLog.OpenDefects.Any(d => d.Category == MelCategory.A);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Tech log read failed");
        }

        return new ScenarioTick(
            view.Phase, _flight.IsLive, nowUtc, since,
            data is { IsValid: true } ? data.AltitudeAglFt : null,
            _monitor.ActiveAbnormalTitles.Count > 0,
            _monitor.DialogueActive,
            noGo,
            SterileCockpitRule.IsSterile(_speech.CurrentValue, context));
    }

    // ---- the write path ----

    /// <summary>Announce → cancel window → write → verify, under the one write lock. False when
    /// cancelled, refused or unverified (the plan goes back to Planned).</summary>
    private async Task<bool> ArmPlanAsync(ScenarioPlan plan, string source, CancellationToken cancellationToken)
    {
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_plan, plan) || plan.State != ScenarioPlanState.Planned)
                {
                    return false;
                }

                _armCancel?.Dispose();
                _armCancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            }

            var cancel = _armCancel!.Token;
            _eventLog.Record(ArmingEvent, new { scenario = plan.ScenarioId, title = plan.Title, failure = plan.FailureName, source, cancelWindowSeconds = (int)CancelWindow.TotalSeconds });
            try
            {
                await Task.Delay(CancelWindow, cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _eventLog.Record("scenario.armCancelled", new { scenario = plan.ScenarioId, source });
                _logger.LogInformation("Scenario {Scenario} arm cancelled in the window", plan.Title);
                return false;
            }

            var condition = plan.Takeoff
                ? new FailureArmCondition(plan.AbnormalId.Contains("after-v1", StringComparison.OrdinalIgnoreCase) ? FailureAfterEvent.V1 : FailureAfterEvent.Takeoff, 0)
                : null;
            var handle = await _injector.ArmAsync(plan.FailureName, condition, cancel).ConfigureAwait(false);
            if (handle is null)
            {
                _eventLog.Record("scenario.armFailed", new { scenario = plan.ScenarioId, failure = plan.FailureName, reason = "refused-or-unreachable" });
                _logger.LogWarning("Scenario {Scenario}: ProSim refused or is unreachable — stays planned", plan.Title);
                return false;
            }

            // Verify: the armed list must show it within the window.
            var verified = false;
            var deadline = Now() + VerifyWindow;
            while (Now() < deadline && !verified)
            {
                var armed = await _injector.ArmedAsync(cancel).ConfigureAwait(false);
                verified = armed.Any(a => string.Equals(a.Handle, handle, StringComparison.Ordinal));
                if (!verified)
                {
                    await Task.Delay(500, cancel).ConfigureAwait(false);
                }
            }

            if (!verified)
            {
                _eventLog.Record("scenario.armFailed", new { scenario = plan.ScenarioId, failure = plan.FailureName, reason = "not-verified" });
                _logger.LogWarning("Scenario {Scenario}: the armed failure did not verify within {Seconds} s — clearing it", plan.Title, (int)VerifyWindow.TotalSeconds);
                await _injector.ClearAsync(handle, CancellationToken.None).ConfigureAwait(false);
                return false;
            }

            lock (_gate)
            {
                plan.State = ScenarioPlanState.Armed;
                plan.Handle = handle;
                plan.ArmedAtUtc = Now();
                _stateFile.Save(plan);
            }

            _eventLog.Record(ArmedEvent, new { scenario = plan.ScenarioId, title = plan.Title, failure = plan.FailureName, source, takeoff = plan.Takeoff });
            _logger.LogInformation("Scenario armed: {Scenario} ({Failure})", plan.Title, plan.FailureName);
            if (plan.ClearAfterSeconds is { } seconds)
            {
                _transientTimer?.Dispose();
                _transientTimer = new Timer(_ => _ = ClearOursAsync("transient", _shutdown.Token), null, TimeSpan.FromSeconds(seconds), Timeout.InfiniteTimeSpan);
            }

            UpdateStore();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Scenario arm failed");
            return false;
        }
        finally
        {
            _writes.Release();
        }
    }

    private void OnFailureDetected(string id, string title)
    {
        ScenarioPlan? fired = null;
        lock (_gate)
        {
            if (_plan is { State: ScenarioPlanState.Armed } plan && string.Equals(plan.AbnormalId, id, StringComparison.OrdinalIgnoreCase))
            {
                plan.State = ScenarioPlanState.Fired;
                plan.FiredAtUtc = Now();
                _stateFile.Save(plan);
                fired = plan;
            }
        }

        if (fired is null)
        {
            return;
        }

        _eventLog.Record(FiredEvent, new { scenario = fired.ScenarioId, title = fired.Title, failure = fired.FailureName, abnormalId = id, abnormalTitle = title });
        _logger.LogInformation("Scenario fired: {Scenario} — the FO has it ({Abnormal})", fired.Title, title);
        UpdateStore();
    }

    private void OnAbnormalEnded(string id, string reason)
    {
        ScenarioPlan? plan;
        lock (_gate)
        {
            if (_plan is not { State: ScenarioPlanState.Fired } p || !string.Equals(p.AbnormalId, id, StringComparison.OrdinalIgnoreCase) || _decisionAsked)
            {
                return;
            }

            _decisionAsked = true;
            plan = p;
        }

        if (reason == "session-ended")
        {
            return;
        }

        _ = Task.Run(() => RaiseDecisionAsync(plan));
    }

    /// <summary>The FO's decision turn: the model's line when it is on (every number checked
    /// against the fact sheet), the entry's template as the floor, as a pick-one captain's question.</summary>
    private async Task RaiseDecisionAsync(ScenarioPlan plan)
    {
        var ofp = _ofp?.Current;
        var floor = ScenarioScheduler.DecisionLine(plan.Decision, ofp?.AlternateName is { Length: > 0 } an ? an : ofp?.AlternateIcao, ofp?.DestinationName is { Length: > 0 } dn ? dn : ofp?.DestinationIcao);
        var line = floor;
        var source = "template";
        try
        {
            var llmUp = _llm is { IsConfigured: true } && _briefing.CurrentValue.LlmEnabled && _llmHealth?.Snapshot().IsUnhealthy != true;
            if (llmUp && _facts is not null)
            {
                var sheet = _facts.Build(Now());
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                budget.CancelAfter(TimeSpan.FromSeconds(15));
                var reply = await _llm!.CompleteAsync(
                    (_persona?.SystemPromptFragment(PersonaStyleCategory.Advisory) ?? "")
                    + "You are the First Officer of an Airbus A320. The ECAM actions for a failure are complete and you now give the Captain "
                    + "your recommendation in ONE or TWO short spoken sentences ending with 'your call, captain' — continue, return or divert, "
                    + "with the one reason that matters. Use only the FACTS and the SHAPE; never add a figure of your own. Plain English for text-to-speech.",
                    "FAILURE: " + plan.Title + "\nSHAPE (keep the meaning): " + floor + "\nFACTS:\n" + sheet.Text + "\n\nSay it now.",
                    budget.Token).ConfigureAwait(false);
                var candidate = (reply ?? "").Trim().Trim('"', '\'');
                if (!string.IsNullOrWhiteSpace(candidate) && NumberVerifier.Check(SpokenNumberText.ToDigits(candidate), sheet.AllowedNumbers).Ok)
                {
                    line = candidate;
                    source = "llm";
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Scenario decision: model turn failed — template");
        }

        _eventLog.Record("scenario.decision", new { scenario = plan.ScenarioId, source, line });
        if (!_decision.Ask(plan.ScenarioId, plan.Title, line))
        {
            _logger.LogInformation("Scenario decision not raised — a question already stands");
        }
    }

    private void OnDecisionAnswered(object? sender, (bool Accepted, string Source) answer)
    {
        var outcome = _decision.Outcome;
        ScenarioPlan? plan;
        lock (_gate)
        {
            plan = _plan;
        }

        _eventLog.Record("scenario.decided", new { scenario = plan?.ScenarioId, outcome, source = answer.Source });
        if (plan is null || _diversion is null || outcome == ScenarioDecisionStore.Continue)
        {
            return;
        }

        try
        {
            var ofp = _ofp?.Current;
            var (icao, name) = outcome == ScenarioDecisionStore.Divert
                ? (ofp?.AlternateIcao, ofp?.AlternateName)
                : (ofp?.OriginIcao, ofp?.OriginName);
            if (string.IsNullOrWhiteSpace(icao))
            {
                _logger.LogInformation("Scenario decision {Outcome}: no airport in the plan — the diversion is the captain's own", outcome);
                return;
            }

            if (_diversion.Declare(icao, name, DiversionReason.Technical, Now()))
            {
                _logger.LogInformation("Scenario decision {Outcome}: diversion declared to {Icao}", outcome, icao);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Scenario decision: the diversion could not be declared");
        }
    }

    // ---- clearing ----

    private void OnFlightCycleReset()
    {
        _lastHold = null;
        _ = Task.Run(async () =>
        {
            await ClearOursAsync("flight-cycle-reset", _shutdown.Token).ConfigureAwait(false);
            lock (_gate)
            {
                _plan = null;
                _decisionAsked = false;
                _stateFile.Save(null);
            }

            UpdateStore();
        });
    }

    /// <summary>Clears the plan's failure (by handle; clear-all when the handle is gone) and
    /// verifies by re-reading the armed list.</summary>
    private async Task<bool> ClearOursAsync(string source, CancellationToken cancellationToken)
    {
        ScenarioPlan? plan;
        lock (_gate)
        {
            plan = _plan;
        }

        if (plan is not { State: ScenarioPlanState.Armed or ScenarioPlanState.Fired })
        {
            return true;
        }

        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var ok = plan.Handle is { } handle
                ? await _injector.ClearAsync(handle, cancellationToken).ConfigureAwait(false)
                : await _injector.ClearAllAsync(cancellationToken).ConfigureAwait(false) >= 0;
            var armed = await _injector.ArmedAsync(cancellationToken).ConfigureAwait(false);
            var stillThere = plan.Handle is { } h && armed.Any(a => a.Handle == h);
            var verified = ok && !stillThere;
            lock (_gate)
            {
                plan.State = ScenarioPlanState.Cleared;
                plan.ClearedAtUtc = Now();
                _stateFile.Save(null);
                _lastClear = verified ? $"cleared ({source})" : "could not clear — check the IOS";
            }

            _eventLog.Record(verified ? ClearedEvent : "scenario.clearFailed", new { scenario = plan.ScenarioId, failure = plan.FailureName, source });
            if (!verified)
            {
                _logger.LogWarning("Scenario {Scenario}: the failure could not be cleared ({Source}) — check the ProSim IOS", plan.Title, source);
            }

            UpdateStore();
            return verified;
        }
        finally
        {
            _writes.Release();
        }
    }

    // ---- IScenarioControl ----

    public async Task<CommandResult> ArmAsync(string? id, string source, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return CommandResult.PreconditionFailed("A scenario id is required.");
        }

        var options = _options.CurrentValue;
        var entry = _catalogue.Entries.FirstOrDefault(e => string.Equals(e.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return CommandResult.PreconditionFailed($"No scenario '{id}' in the catalogue.");
        }

        if (!Known(entry))
        {
            return CommandResult.PreconditionFailed($"Scenario '{id}' names an abnormal that is not loaded.");
        }

        var inputs = new ScenarioPlanInputs(_abnormals.CurrentValue.RtoEnabled, _abnormals.CurrentValue.EfatoEnabled, [], null);
        if (!options.Enabled || !ScenarioScheduler.Allowed([entry], options, inputs).Any())
        {
            return CommandResult.PreconditionFailed($"Scenario '{id}' is not allowed by the settings (enabled, verified, allowMajor, takeoffScenarios).");
        }

        ScenarioPlan plan;
        lock (_gate)
        {
            if (_plan is { State: ScenarioPlanState.Armed or ScenarioPlanState.Fired })
            {
                return CommandResult.PreconditionFailed("A scenario is already armed — clear it first.");
            }

            plan = new ScenarioPlan
            {
                ScenarioId = entry.Id,
                Title = entry.Title,
                AbnormalId = entry.AbnormalId,
                FailureName = entry.FailureName,
                Phase = _flight.CurrentPhase,
                FireAfterSeconds = 0,
                Takeoff = entry.Takeoff,
                ClearAfterSeconds = entry.ClearAfterSeconds,
                TechLogAfter = entry.TechLogAfter,
                Decision = entry.Decision,
                PlannedAtUtc = Now(),
                State = ScenarioPlanState.Planned,
                SessionId = Path.GetFileNameWithoutExtension(_eventLog.Path),
            };
            _plan = plan;
            _decisionAsked = false;
        }

        var tick = BuildTick(Now());
        var decision = ScenarioScheduler.Evaluate(plan, tick with { PhaseSinceUtc = Now().AddHours(-1) }, options);
        if (!decision.Arm)
        {
            lock (_gate)
            {
                _plan = null;
            }

            UpdateStore();
            return CommandResult.PreconditionFailed(decision.HoldReason ?? "the flight is not live or not in the right phase");
        }

        var armed = await ArmPlanAsync(plan, source, cancellationToken).ConfigureAwait(false);
        if (!armed)
        {
            lock (_gate)
            {
                _plan = null;
            }

            UpdateStore();
            return CommandResult.PreconditionFailed("ProSim refused the failure, it did not verify, or the arm was cancelled.");
        }

        return CommandResult.Ok($"{entry.Title} armed.");
    }

    public async Task<CommandResult> ClearAsync(string? id, string source, CancellationToken cancellationToken = default)
    {
        ScenarioPlan? plan;
        lock (_gate)
        {
            plan = _plan;
        }

        if (plan is null || (!string.IsNullOrWhiteSpace(id) && !string.Equals(plan.ScenarioId, id.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return CommandResult.AlreadySatisfied("No such scenario is armed.");
        }

        var ok = await ClearOursAsync(source, cancellationToken).ConfigureAwait(false);
        return ok ? CommandResult.Ok($"{plan.Title} cleared.") : CommandResult.PreconditionFailed("The failure could not be cleared — check the ProSim IOS.");
    }

    /// <summary>Unconditional: cancels an arm in its window, resets every armed failure ProSim
    /// reports (the IOS's included), drops the plan.</summary>
    public async Task<CommandResult> ClearAllAsync(string source, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _armCancel?.Cancel();
        }

        _eventLog.Record("scenario.clearAllRequested", new { source });
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        int count;
        try
        {
            count = await _injector.ClearAllAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (_plan is { } plan)
                {
                    plan.State = ScenarioPlanState.Cleared;
                    plan.ClearedAtUtc = Now();
                }

                _plan = null;
                _decisionAsked = false;
                _stateFile.Save(null);
                _lastClear = count >= 0 ? $"cleared {count} ({source})" : "could not clear — check the IOS";
            }
        }
        finally
        {
            _writes.Release();
        }

        _eventLog.Record(count >= 0 ? ClearedEvent : "scenario.clearFailed", new { scenario = (string?)null, count, source });
        if (count < 0)
        {
            _logger.LogWarning("Clear all failures ({Source}): ProSim unreachable — check the IOS", source);
        }
        else
        {
            _logger.LogInformation("Clear all failures ({Source}): {Count} reset", source, count);
        }

        UpdateStore();
        return count >= 0 ? CommandResult.Ok($"{count} failure(s) cleared.") : CommandResult.PreconditionFailed("ProSim is unreachable — check the IOS.");
    }

    public async Task<CommandResult> ListFailuresAsync(CancellationToken cancellationToken = default)
    {
        await RefreshLiveFailuresAsync().ConfigureAwait(false);
        var list = _liveFailures;
        return list.Count == 0
            ? CommandResult.PreconditionFailed("ProSim reported no failures (not connected, or the SDK has no failure API).")
            : CommandResult.Ok(string.Join("; ", list));
    }

    private async Task RefreshLiveFailuresAsync()
    {
        try
        {
            var list = await _injector.ListAsync(_shutdown.Token).ConfigureAwait(false);
            _liveFailures = list;
            if (list.Count > 0)
            {
                var catalogue = _catalogue.Entries;
                var unknown = catalogue.Where(e => !list.Contains(e.FailureName, StringComparer.Ordinal)).Select(e => $"{e.Id} → \"{e.FailureName}\"").ToList();
                _logger.LogInformation("ProSim failure API: {Count} failures listed; {Unknown} catalogue name(s) not in the list{Names}",
                    list.Count, unknown.Count, unknown.Count == 0 ? "" : ": " + string.Join(", ", unknown));
                _eventLog.Record("scenario.failuresListed", new { count = list.Count, unknown });
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failure list refresh failed");
        }

        UpdateStore();
    }

    // ---- voice ----

    public bool TryHandle(string utterance)
    {
        if (string.IsNullOrWhiteSpace(utterance))
        {
            return false;
        }

        var text = CommandMatcher.Normalize(utterance);
        if (ClearPhrases.Select(CommandMatcher.Normalize).Contains(text))
        {
            _ = Task.Run(async () =>
            {
                var result = await ClearAllAsync("voice", _shutdown.Token).ConfigureAwait(false);
                await _arbiter.SpeakAsync(result.Outcome == CommandOutcome.Success ? "All failures cleared, captain." : "I couldn't clear the failures, captain — check the IOS.", SpeechPriority.High).ConfigureAwait(false);
            });
            return true;
        }

        if (PlannedPhrases.Select(CommandMatcher.Normalize).Contains(text))
        {
            var plan = Plan;
            var line = _options.CurrentValue.ShowPlanned && plan is not null
                ? $"{plan.Title} is planned for the {PhaseWord(plan.Phase)}, captain."
                : "Nothing I can tell you about, captain.";
            _ = _arbiter.SpeakAsync(line, SpeechPriority.Normal);
            return true;
        }

        return false;
    }

    private static string PhaseWord(FlightPhase phase) => phase switch
    {
        FlightPhase.TakeoffRoll => "takeoff roll",
        FlightPhase.InitialClimb => "initial climb",
        FlightPhase.TaxiIn => "taxi in",
        FlightPhase.TaxiOut => "taxi out",
        _ => phase.ToString().ToLowerInvariant(),
    };

    private void UpdateStore()
    {
        ScenarioPlan? plan;
        string? lastClear;
        lock (_gate)
        {
            plan = _plan;
            lastClear = _lastClear;
        }

        var options = _options.CurrentValue;
        var shown = options.ShowPlanned || plan is { State: ScenarioPlanState.Fired or ScenarioPlanState.Cleared };
        var state = !options.Enabled && plan is null ? "off"
            : plan is null ? "idle"
            : plan.State.ToString().ToLowerInvariant();
        _store.Update(_ => new ScenarioSnapshot(
            state,
            shown ? plan?.Title : null,
            shown && plan is { State: ScenarioPlanState.Armed or ScenarioPlanState.Fired } ? plan.FailureName : null,
            lastClear,
            _liveFailures,
            _injector.IsAvailable));
    }
}
