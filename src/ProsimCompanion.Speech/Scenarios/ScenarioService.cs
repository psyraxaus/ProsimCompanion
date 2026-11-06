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
/// and the one serialised failure-write path (<see cref="IScenarioControl"/>). Once per leg it
/// rolls the dice — at the Preflight edge, at the first live tick in Preflight when that edge
/// came before the flight was live, or at the leg-began edge (a different OFP) when nothing
/// rolled yet — and plans at most one scenario; a one-second tick asks the scheduler whether
/// the plan may arm; an arm is announce (session event) → a 3 s cancel window → the injector's
/// write → a verify read, all under one lock whichever surface asked, and a refused arm spends
/// the plan (<see cref="ScenarioPlanState.Failed"/>) instead of re-queuing every second. The
/// failure monitor detects the result exactly as an IOS failure; on the dialogue's end the FO's
/// decision turn is raised as a pick-one captain's question. Every armed failure is cleared at
/// the arrival flight-cycle reset and at app exit — a new-plan reset keeps a Planned plan, the
/// leg is the same aircraft at the same stand — and "clear all failures" runs unconditionally
/// from voice, the STATUS page, the Stream Deck and the API. The startup clear-all of a plan
/// left Armed by a crash waits for the ProSim connected edge: at <see cref="Start"/> there is
/// no SDK session yet (review 2026-11-05).
/// </summary>
public sealed class ScenarioService : Core.Hosting.IStartupModule, IHostedService, IScenarioControl, IVoiceFeature, IDisposable
{
    public const string ArmingEvent = "scenario.arming";
    public const string ArmedEvent = "scenario.armed";
    public const string FiredEvent = "scenario.fired";
    public const string ClearedEvent = "scenario.cleared";

    /// <summary>One per leg: the dice were rolled (<c>planned</c> says whether a scenario came of it).</summary>
    public const string RolledEvent = "scenario.rolled";

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
    private readonly ILegBoundaryEvents _legBoundary;
    private readonly ConnectionStatusStore _connections;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<ScenarioService> _logger;
    private readonly OfpStore? _ofp;
    private readonly DiversionStore? _diversion;
    private readonly PersonaService? _persona;
    private readonly OpenAiChatClient? _llm;
    private readonly LlmHealthStore? _llmHealth;
    private readonly FoFactSource? _facts;

    // Never disposed on purpose: the startup-module host disposes this service BEFORE the
    // hosted StopAsync runs the exit clear (hosted services stop in reverse registration
    // order), and a SemaphoreSlim without a wait handle holds no OS resource. Disposing it
    // here made the exit clear throw ObjectDisposedException inside its own lock (review
    // 2026-11-05) — and the startup clear that was the safety net could not reach ProSim.
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();

    private Timer? _timer;
    private int _ticking;
    private int _arming;
    private ScenarioPlan? _plan;
    private DateTimeOffset? _phaseSinceUtc;
    private FlightPhase _lastPhase = FlightPhase.Unknown;
    private string? _lastHold;
    private CancellationTokenSource? _armCancel;
    private Timer? _transientTimer;
    private bool _decisionAsked;
    private bool _rolledThisLeg;
    private bool _planPending;
    private bool _startupClearPending;
    private bool _prosimConnected;
    private string? _lastClear;
    private IReadOnlyList<string> _liveFailures = [];
    private bool _started;
    private bool _disposed;

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
        ILegBoundaryEvents legBoundary,
        ConnectionStatusStore connections,
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
        ArgumentNullException.ThrowIfNull(legBoundary);
        ArgumentNullException.ThrowIfNull(connections);
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
        _legBoundary = legBoundary;
        _connections = connections;
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

    /// <summary>The announce-to-write window the pilot can cancel in (ADR-0018 §3); tests shorten it.</summary>
    internal TimeSpan CancelWindow { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>The last background job this service started (an arm, a clear, the startup
    /// clear-all) — for tests, which await it instead of sleeping.</summary>
    internal Task? LastBackgroundWork { get; private set; }

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
        _legBoundary.LegBegan += OnLegBegan;
        _connections.Changed += OnConnectionChanged;
        _decision.Answered += OnDecisionAnswered;

        var load = _stateFile.Load();
        if (load.LeftArmed)
        {
            // No SDK session exists yet at module start: the clear runs on the connected edge
            // (OnConnectionChanged), or now if ProSim is somehow already up.
            _startupClearPending = true;
            _logger.LogWarning("Scenario state shows a failure armed at the last exit — clearing all failures once ProSim is connected");
        }
        else if (load.Plan is { } plan && Now() - plan.PlannedAtUtc < PlanMaxAge)
        {
            lock (_gate)
            {
                _plan = plan; // Planned only — re-judged against the safety floor before it fires
                _rolledThisLeg = true;
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
        _prosimConnected = ProsimConnected();
        if (_prosimConnected)
        {
            OnProsimConnected();
        }

        UpdateStore();
    }

    Task IHostedService.StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>App exit: every failure we armed is cleared and the clear verified. Runs AFTER
    /// the module host's <see cref="Dispose"/> (reverse registration order), so it uses the
    /// host's token, not <c>_shutdown</c>, and the write lock is still alive.</summary>
    async Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        ScenarioPlan? plan;
        lock (_gate)
        {
            plan = _plan;
        }

        if (plan is { State: ScenarioPlanState.Armed or ScenarioPlanState.Fired })
        {
            await ClearPlanAsync(plan, "exit", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Goes quiet: events off, timers off, in-flight arms cancelled. Idempotent — the
    /// module host and the container both call it.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _flight.PhaseChanged -= OnPhaseChanged;
        _monitor.FailureDetected -= OnFailureDetected;
        _monitor.AbnormalEnded -= OnAbnormalEnded;
        _signals.FlightCycleReset -= OnFlightCycleReset;
        _legBoundary.LegBegan -= OnLegBegan;
        _connections.Changed -= OnConnectionChanged;
        _decision.Answered -= OnDecisionAnswered;
        _timer?.Dispose();
        _transientTimer?.Dispose();
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    private bool ProsimConnected()
        => _connections.Snapshot().Any(pair => pair.Key == Subsystems.Prosim && pair.Value == ConnectionState.Connected);

    private void OnConnectionChanged(object? sender, EventArgs e)
    {
        var connected = ProsimConnected();
        if (connected == _prosimConnected)
        {
            return;
        }

        _prosimConnected = connected;
        if (connected)
        {
            OnProsimConnected();
        }
    }

    /// <summary>The ProSim session is up: the crash-recovery clear-all runs now if one is
    /// owed, and the live failure list is read for the STATUS page and the catalogue check.</summary>
    private void OnProsimConnected()
    {
        if (_startupClearPending)
        {
            _startupClearPending = false;
            Forget(Task.Run(async () =>
            {
                var result = await ClearAllAsync("startup", _shutdown.Token).ConfigureAwait(false);
                if (result.Outcome != CommandOutcome.Success)
                {
                    _startupClearPending = true; // the next connected edge tries again
                }
            }), "startup clear-all");
            return;
        }

        Forget(Task.Run(RefreshLiveFailuresAsync), "failure list refresh");
    }

    /// <summary>Observes a background task's fault: a fire-and-forget whose exception nobody
    /// reads is a silent failure (review 2026-11-05 — the three clear paths and the arm).</summary>
    private void Forget(Task task, string what)
    {
        LastBackgroundWork = task;
        task.ContinueWith(
            t => _logger.LogWarning(t.Exception, "Scenario {What} faulted", what),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    // ---- planning ----

    private void OnPhaseChanged(object? sender, FlightPhaseChangedEventArgs e)
    {
        var now = Now();
        lock (_gate)
        {
            _phaseSinceUtc = now;
            _lastPhase = e.Current;
            // A Preflight edge that comes before the flight is live (a restart at the gate)
            // leaves the roll pending for the first live tick; leaving Preflight drops it.
            _planPending = e.Current == FlightPhase.Preflight;
        }

        if (e.Current == FlightPhase.Preflight && e.Previous != FlightPhase.Preflight)
        {
            TryPlan(now);
        }
    }

    /// <summary>A different OFP landed (ADR-0019 §1 — the one source of "new leg"): in a
    /// turnaround the Preflight edge comes 30 s after deboarding and the new plan later; if
    /// nothing rolled for this leg yet, roll now.</summary>
    private void OnLegBegan(object? sender, LegBeganEventArgs e) => TryPlan(Now());

    /// <summary>Rolls the leg's dice and plans its scenario — once per leg, only with a live
    /// flight and scenario mode on. Exposed for tests.</summary>
    public ScenarioPlan? TryPlan(DateTimeOffset nowUtc)
    {
        var options = _options.CurrentValue;
        lock (_gate)
        {
            if (_plan is not null || _rolledThisLeg || !options.Enabled || !_flight.IsLive)
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
            _rolledThisLeg = true;
            _planPending = false;
            _decisionAsked = false;
            _stateFile.Save(plan);
            _eventLog.Record(RolledEvent, new { planned = plan is not null, phase = _flight.CurrentPhase.ToString() });
            if (plan is not null)
            {
                _eventLog.Record("scenario.planned", new { scenario = plan.ScenarioId, title = plan.Title, phase = plan.Phase.ToString(), afterSeconds = plan.FireAfterSeconds, shown = options.ShowPlanned });
                _logger.LogInformation("Scenario planned: {Scenario} in {Phase} after {Seconds} s", plan.Title, plan.Phase, plan.FireAfterSeconds);
            }
            else
            {
                _logger.LogInformation("Scenario roll: a quiet leg");
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
            _logger.LogWarning(ex, "Scenario tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    /// <summary>One evaluation with the clock supplied: rolls a pending plan on the first live
    /// Preflight tick, arms the plan when the scheduler says so (one arm in flight at a time).
    /// Returns the scheduler's decision, for tests.</summary>
    public ScenarioDecision ProcessTick(DateTimeOffset nowUtc)
    {
        ScenarioPlan? plan;
        bool pending;
        lock (_gate)
        {
            plan = _plan;
            pending = _planPending && _plan is null && !_rolledThisLeg;
        }

        if (pending && _flight.CurrentPhase == FlightPhase.Preflight && _flight.IsLive)
        {
            plan = TryPlan(nowUtc);
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

        if (decision.Arm && Volatile.Read(ref _arming) == 0)
        {
            Forget(Task.Run(() => ArmPlanAsync(plan, "scheduler", _shutdown.Token)), "arm");
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
            view.Phase, FlightGate.IsLiveFor(view), nowUtc, since,
            data is { IsValid: true } ? data.AltitudeAglFt : null,
            _monitor.ActiveAbnormalTitles.Count > 0,
            _monitor.DialogueActive,
            noGo,
            SterileCockpitRule.IsSterile(_speech.CurrentValue, context));
    }

    // ---- the write path ----

    /// <summary>Announce → cancel window → write → verify, under the one write lock and one at
    /// a time. False when cancelled (the plan stays Planned), refused or unverified (the plan
    /// is spent: <see cref="ScenarioPlanState.Failed"/>, no second attempt this leg).</summary>
    private async Task<bool> ArmPlanAsync(ScenarioPlan plan, string source, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _arming, 1, 0) == 1)
        {
            return false;
        }

        try
        {
            await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Exchange(ref _arming, 0);
            return false;
        }

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
                ? new FailureArmCondition(plan.AfterEvent ?? FailureAfterEvent.Takeoff, 0)
                : null;
            var handle = await _injector.ArmAsync(plan.FailureName, condition, cancel).ConfigureAwait(false);
            if (handle is null)
            {
                MarkFailed(plan, "refused-or-unreachable", "ProSim refused the failure or is unreachable");
                return false;
            }

            // Verify: the armed list must show it within the window.
            var verified = false;
            var deadline = Now() + VerifyWindow;
            for (var attempt = 0; attempt < 10 && Now() < deadline && !verified; attempt++)
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
                await _injector.ClearAsync(handle, CancellationToken.None).ConfigureAwait(false);
                MarkFailed(plan, "not-verified", $"the armed failure did not verify within {(int)VerifyWindow.TotalSeconds} s and was cleared");
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
                _transientTimer = new Timer(_ => Forget(ClearOursAsync("transient", _shutdown.Token), "transient clear"), null, TimeSpan.FromSeconds(seconds), Timeout.InfiniteTimeSpan);
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
            MarkFailed(plan, "error", ex.Message);
            return false;
        }
        finally
        {
            _writes.Release();
            Interlocked.Exchange(ref _arming, 0);
        }
    }

    /// <summary>A refused or unverified arm spends the plan for the leg: one event, one
    /// warning, no second attempt (the tick re-queued the arm every second before).</summary>
    private void MarkFailed(ScenarioPlan plan, string reason, string detail)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_plan, plan) && plan.State == ScenarioPlanState.Planned)
            {
                plan.State = ScenarioPlanState.Failed;
                _stateFile.Save(null);
            }
        }

        _eventLog.Record("scenario.armFailed", new { scenario = plan.ScenarioId, failure = plan.FailureName, reason });
        _logger.LogWarning("Scenario {Scenario}: {Detail} — no further attempt this leg", plan.Title, detail);
        UpdateStore();
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

            // A session reset ends the dialogue without a decision; the latch stays open so
            // the turn can still be raised when the abnormal ends for real (review 2026-11-05).
            if (reason == "session-ended")
            {
                return;
            }

            _decisionAsked = true;
            plan = p;
        }

        Forget(Task.Run(() => RaiseDecisionAsync(plan)), "decision turn");
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

    /// <summary>The flight-cycle reset. Arrival: the leg is over — clear what we armed, drop
    /// the plan, re-arm the dice for the next leg. New plan (ADR-0019 §6): the same aircraft
    /// at the same stand got a different OFP — a Planned plan is kept (one roll per leg), an
    /// Armed or Fired one is cleared like at the arrival. Before this the new-plan reset
    /// dropped the plan without reading the kind and nothing re-planned: every leg after the
    /// first flew scenario-free (review 2026-11-05).</summary>
    private void OnFlightCycleReset(FlightCycleResetKind kind)
    {
        ScenarioPlan? toClear;
        lock (_gate)
        {
            _lastHold = null;
            if (kind == FlightCycleResetKind.NewPlan && _plan is { State: ScenarioPlanState.Planned or ScenarioPlanState.Failed })
            {
                _logger.LogDebug("Scenario plan kept across the new-plan reset ({Scenario}, {State})", _plan.Title, _plan.State);
                return;
            }

            toClear = _plan is { State: ScenarioPlanState.Armed or ScenarioPlanState.Fired } ? _plan : null;
            _plan = null;
            _decisionAsked = false;
            _rolledThisLeg = kind == FlightCycleResetKind.NewPlan && _rolledThisLeg;
            _stateFile.Save(null);
        }

        UpdateStore();
        if (toClear is not null)
        {
            Forget(ClearPlanAsync(toClear, "flight-cycle-reset", _shutdown.Token), "flight-cycle-reset clear");
        }
    }

    /// <summary>Clears the current plan's failure (when it is armed or fired).</summary>
    private Task<bool> ClearOursAsync(string source, CancellationToken cancellationToken)
    {
        ScenarioPlan? plan;
        lock (_gate)
        {
            plan = _plan;
        }

        return plan is { State: ScenarioPlanState.Armed or ScenarioPlanState.Fired }
            ? ClearPlanAsync(plan, source, cancellationToken)
            : Task.FromResult(true);
    }

    /// <summary>Clears <paramref name="plan"/>'s failure (by handle; clear-all when the handle
    /// is gone) and verifies by re-reading the armed list.</summary>
    private async Task<bool> ClearPlanAsync(ScenarioPlan plan, string source, CancellationToken cancellationToken)
    {
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
                if (ReferenceEquals(_plan, plan))
                {
                    _stateFile.Save(null);
                }

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

        // ADR-0018 §5: the catalogue says where a failure may fire, the manual arm included —
        // "scenario.arm brakes-hot" (TaxiIn only) used to arm in the cruise.
        var phase = _flight.CurrentPhase;
        if (!ScenarioScheduler.AllowsPhase(entry, phase))
        {
            var allowed = entry.Takeoff ? "before the takeoff roll (pushback, taxi out)" : string.Join(", ", entry.Phases);
            return CommandResult.PreconditionFailed($"Scenario '{id}' is not allowed in {phase} — its catalogue phases are {allowed}.");
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
                Phase = phase,
                FireAfterSeconds = 0,
                Takeoff = entry.Takeoff,
                AfterEvent = entry.AfterEvent,
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
                if (ReferenceEquals(_plan, plan))
                {
                    _plan = null;
                }
            }

            UpdateStore();
            return CommandResult.PreconditionFailed("ProSim refused the failure, it did not verify, another arm is in progress, or the arm was cancelled.");
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
        catch (OperationCanceledException)
        {
            // Shutdown.
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
            Forget(Task.Run(async () =>
            {
                var result = await ClearAllAsync("voice", _shutdown.Token).ConfigureAwait(false);
                await _arbiter.SpeakAsync(result.Outcome == CommandOutcome.Success ? "All failures cleared, captain." : "I couldn't clear the failures, captain — check the IOS.", SpeechPriority.High).ConfigureAwait(false);
            }), "voice clear-all");
            return true;
        }

        if (PlannedPhrases.Select(CommandMatcher.Normalize).Contains(text))
        {
            var plan = Plan;
            var line = _options.CurrentValue.ShowPlanned && plan is { State: not (ScenarioPlanState.Failed or ScenarioPlanState.Cleared) }
                ? $"{plan.Title} is planned for the {PhaseWord(plan.Phase)}, captain."
                : "Nothing I can tell you about, captain.";
            Forget(_arbiter.SpeakAsync(line, SpeechPriority.Normal), "planned answer");
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
        var shown = options.ShowPlanned || plan is { State: ScenarioPlanState.Fired or ScenarioPlanState.Cleared or ScenarioPlanState.Failed };
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
