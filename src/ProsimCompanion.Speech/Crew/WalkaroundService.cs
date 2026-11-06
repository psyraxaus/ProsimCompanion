using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Crew;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.TechLog;
using ProsimCompanion.Core.Weather;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Persona;
using ProsimCompanion.Speech.TechLog;

namespace ProsimCompanion.Speech.Crew;

/// <summary>
/// The FO's exterior walkaround (issue #196): the shell around <see cref="WalkaroundCore"/>.
/// A one-second tick samples the flight engine's ground facts and the GSX prep stage; on the
/// ready edge the FO announces the walk in the cockpit (Low priority, FO voice), is marked
/// away on the <see cref="FoAvailabilityStore"/> (the router answers the captain's queries
/// with the away line, the departure pump holds the refuel call), and after the random away
/// window reports back on the interphone exactly as the ground upcalls arrive — MECH press,
/// INT latch or grace, then the FO's own voice through the ground intercom filter. A finding
/// may ride on the report: cosmetic ends there, icing leads into the de-ice question,
/// deferrable asks "shall I raise it?" and hands the pre-filled item to the tech-log raise path
/// (confirmation kept), no-go is raised regardless. A refuel called meanwhile abandons the
/// walk. Read-and-speak only beyond the upcalls' allow-listed MECH press.
/// </summary>
public sealed class WalkaroundService : Core.Hosting.IStartupModule, IDisposable, IFlightStatePart
{
    public const string ReportTag = "walkaround.report";
    public const string AnnounceLine = "I'll do the walkaround, captain.";
    public const string AbandonLine = "Abandoning the walkaround, captain — the refuel's been called.";
    public const string ReportTemplate = "Walkaround complete, captain. Aircraft looks good — tyres fine, no leaks, pitot covers off, chocks in.";
    public const string DeicePrompt = "We'll need de-icing, captain — request it?";

    private readonly IOptionsMonitor<GroundCrewOptions> _options;
    private readonly IFlightPhaseSource _flight;
    private readonly IGroundFactsSource _ground;
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly BoardingEvidenceStore _boarding;
    private readonly DepartureCycleState _departure;
    private readonly GroundOpsSignals _signals;
    private readonly FoAvailabilityStore _availability;
    private readonly WalkaroundFindingBank _bank;
    private readonly WalkaroundRaiseStore _raise;
    private readonly ITechLogService _techLog;
    private readonly ISpeechArbiter _arbiter;
    private readonly StyledSpeechService _styled;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<WalkaroundService> _logger;
    private readonly IAcpChannel? _acp;
    private readonly IProsimDataRefs? _dataRefs;
    private readonly TechLogDialogueService? _techLogDialogue;
    private readonly DeiceRequestStore? _deice;
    private readonly HeroWeatherStore? _weather;
    private readonly OfpStore? _ofp;
    private readonly ISimLocalTime? _clock;
    private readonly WalkaroundCore _core = new();
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();

    private Timer? _timer;
    private int _ticking;
    private Task? _inFlight;
    private WalkaroundFinding? _pendingFinding;
    private bool _disabledLogged;

    public WalkaroundService(
        IOptionsMonitor<GroundCrewOptions> options,
        IFlightPhaseSource flight,
        IGroundFactsSource ground,
        GsxDiagnosticsStore diagnostics,
        BoardingEvidenceStore boarding,
        DepartureCycleState departure,
        GroundOpsSignals signals,
        FoAvailabilityStore availability,
        WalkaroundFindingBank bank,
        WalkaroundRaiseStore raise,
        ITechLogService techLog,
        ISpeechArbiter arbiter,
        StyledSpeechService styled,
        JsonlEventLog eventLog,
        ILogger<WalkaroundService> logger,
        IAcpChannel? acp = null,
        IProsimDataRefs? dataRefs = null,
        TechLogDialogueService? techLogDialogue = null,
        DeiceRequestStore? deice = null,
        HeroWeatherStore? weather = null,
        OfpStore? ofp = null,
        ISimLocalTime? clock = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(ground);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(boarding);
        ArgumentNullException.ThrowIfNull(departure);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(availability);
        ArgumentNullException.ThrowIfNull(bank);
        ArgumentNullException.ThrowIfNull(raise);
        ArgumentNullException.ThrowIfNull(techLog);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(styled);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _flight = flight;
        _ground = ground;
        _diagnostics = diagnostics;
        _boarding = boarding;
        _departure = departure;
        _signals = signals;
        _availability = availability;
        _bank = bank;
        _raise = raise;
        _techLog = techLog;
        _arbiter = arbiter;
        _styled = styled;
        _eventLog = eventLog;
        _logger = logger;
        _acp = acp;
        _dataRefs = dataRefs;
        _techLogDialogue = techLogDialogue;
        _deice = deice;
        _weather = weather;
        _ofp = ofp;
        _clock = clock;
    }

    private WalkaroundOptions Options => _options.CurrentValue.Walkaround;

    /// <summary>The announce / report / abandon in progress, for tests to await.</summary>
    internal Task? InFlight => _inFlight;

    /// <summary>The dice, injectable for deterministic tests.</summary>
    internal Func<double> Roll { get; init; } = () => Random.Shared.NextDouble();

    /// <summary>The core's state, for tests and the diagnostics.</summary>
    public WalkaroundState State
    {
        get
        {
            lock (_gate)
            {
                return _core.State;
            }
        }
    }

    public void Start()
    {
        _signals.FlightCycleReset += OnFlightCycleReset;
        _raise.Answered += OnRaiseAnswered;
        if (!Options.Enabled)
        {
            _logger.LogInformation("FO walkaround disabled (groundCrew.walkaround.enabled)");
            _disabledLogged = true;
        }

        _timer = new Timer(_ => Tick(), null, 1000, 1000);
    }

    public void Dispose()
    {
        _signals.FlightCycleReset -= OnFlightCycleReset;
        _raise.Answered -= OnRaiseAnswered;
        _timer?.Dispose();
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    private void OnFlightCycleReset(FlightCycleResetKind _)
    {
        // Arrival and new-plan resets alike (ADR-0019 §6): per-leg state, it starts over either way.
        lock (_gate)
        {
            _core.OnFlightCycleReset();
            _pendingFinding = null;
        }

        if (_availability.Snapshot() is { IsAway: true, Reason: FoAvailabilityStore.WalkaroundReason })
        {
            _availability.Clear(); // a reset mid-walk: the FO is back, nothing is reported
        }
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            ProcessTick(DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Walkaround tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    /// <summary>One evaluation with the clock supplied (tests step it). Returns the action
    /// started, for tests.</summary>
    public WalkaroundAction ProcessTick(DateTimeOffset nowUtc)
    {
        var options = Options;
        if (!options.Enabled)
        {
            if (!_disabledLogged)
            {
                _logger.LogInformation("FO walkaround disabled (groundCrew.walkaround.enabled)");
                _disabledLogged = true;
            }
        }
        else
        {
            _disabledLogged = false;
        }

        var sample = BuildSample(nowUtc);
        WalkaroundAction action;
        TimeSpan? duration = null;
        lock (_gate)
        {
            if (_inFlight is { IsCompleted: false })
            {
                return WalkaroundAction.None;
            }

            var before = _core.State;
            action = _core.Evaluate(sample, options, Roll);
            duration = _core.Duration(nowUtc);
            switch (action)
            {
                case WalkaroundAction.Announce:
                    _inFlight = Task.Run(() => AnnounceAsync(nowUtc), CancellationToken.None);
                    break;
                case WalkaroundAction.Report:
                    _inFlight = Task.Run(() => ReportAsync(sample, duration), CancellationToken.None);
                    break;
                case WalkaroundAction.Abandon:
                    var reason = _core.LastAbandonReason ?? WalkaroundAbandonReason.LeftTheGate;
                    _inFlight = Task.Run(() => AbandonAsync(duration, reason), CancellationToken.None);
                    break;
            }

            if (_core.State != before)
            {
                Changed?.Invoke();
            }
        }

        return action;
    }

    // ---- the flight-state snapshot part (ADR-0019 §9: a restart is invisible) -----------------

    /// <inheritdoc />
    public string Key => "walkaround";

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public JsonNode? Capture()
    {
        WalkaroundSnapshotState state;
        lock (_gate)
        {
            state = _core.Capture();
        }

        // Always a node, even Idle: the restart then knows the cycle's walk had not happened
        // yet (still possible) rather than guessing.
        return JsonSerializer.SerializeToNode(state, FlightStateSnapshotStore.JsonOptions);
    }

    /// <inheritdoc />
    public void Restore(JsonNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var saved = node.Deserialize<WalkaroundSnapshotState>(FlightStateSnapshotStore.JsonOptions);
        WalkaroundRestartOutcome outcome;
        lock (_gate)
        {
            outcome = _core.AdoptRestart(saved);
            _pendingFinding = null;
        }

        if (outcome != WalkaroundRestartOutcome.StillPossible
            && _availability.Snapshot() is { IsAway: true, Reason: FoAvailabilityStore.WalkaroundReason })
        {
            _availability.Clear();
        }

        var savedState = saved?.State.ToString() ?? WalkaroundState.Idle.ToString();
        switch (outcome)
        {
            case WalkaroundRestartOutcome.StillPossible:
                _logger.LogInformation("FO walkaround still possible this departure after the restart — it runs on the ready edge unless boarding has started");
                break;
            case WalkaroundRestartOutcome.WalkEnded:
                _logger.LogInformation("FO walkaround was in progress when the app went down — the FO is back, no report");
                break;
            default:
                _logger.LogInformation("FO walkaround already done this departure (snapshot state {Saved}) — not repeated", savedState);
                break;
        }

        _eventLog.Record("walkaround.restored", new { snapshotState = savedState, outcome = outcome.ToString(), state = State.ToString() });
    }

    /// <summary>The sample as the core sees it — exposed for the tests' tables.</summary>
    public WalkaroundSample BuildSample(DateTimeOffset nowUtc)
    {
        var facts = _ground.Facts;
        GsxServiceStage? refuel = null;
        try
        {
            // The refuel stage is still read off the service board (it is what the board is
            // for); readiness and boarding come from their owners (review 2026-11-05) — the
            // departure cycle's PrepComplete flag, not a diagnostics string, and the lifecycle's
            // boarding evidence, the same "started" the gate monitor reads.
            refuel = _diagnostics.Snapshot().ServiceBoard
                .FirstOrDefault(row => string.Equals(row.ServiceId, GsxServiceIds.Refueling, StringComparison.OrdinalIgnoreCase))?.Stage;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GSX diagnostics read failed");
        }

        var view = _flight.Snapshot();
        return new WalkaroundSample(view.Phase, FlightGate.IsLiveFor(view), facts.Parked, facts.EnginesOff, _departure.PrepComplete, refuel, nowUtc, _boarding.Snapshot().Started);
    }

    private async Task AnnounceAsync(DateTimeOffset nowUtc)
    {
        try
        {
            TimeSpan? window;
            lock (_gate)
            {
                window = _core.ReportDueUtc is { } due ? due - nowUtc : null;
            }

            _availability.SetAway(FoAvailabilityStore.WalkaroundReason, nowUtc);
            _eventLog.Record("walkaround.started", new { plannedMinutes = window is { } w ? Math.Round(w.TotalMinutes, 1) : (double?)null, phase = _flight.CurrentPhase.ToString() });
            _logger.LogInformation("FO walkaround started — back in about {Minutes} min", window is { } m ? Math.Round(m.TotalMinutes) : 0);
            var text = await _styled.StyleAsync(AnnounceLine, PersonaStyleCategory.Advisory, "walkaround:announce", _shutdown.Token).ConfigureAwait(false);
            await _arbiter.EnqueueAsync(new SpeechRequest(
                string.IsNullOrWhiteSpace(text) ? AnnounceLine : text, SpeechPriority.Low, Ttl: TimeSpan.FromMinutes(2), Tag: "walkaround.announce"),
                _shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Walkaround announce failed");
        }
    }

    /// <summary>The abandon line for a reason — it names what actually happened (review
    /// 2026-11-05: every abandon said "the refuel's been called"). Pure, for tests.</summary>
    public static string AbandonLineFor(WalkaroundAbandonReason reason) => reason switch
    {
        WalkaroundAbandonReason.RefuelCalled => AbandonLine,
        WalkaroundAbandonReason.BoardingStarted => "Abandoning the walkaround, captain — boarding's been called.",
        WalkaroundAbandonReason.EngineRunning => "Abandoning the walkaround, captain — we've got an engine running.",
        WalkaroundAbandonReason.AircraftMoving => "Abandoning the walkaround, captain — we're moving.",
        WalkaroundAbandonReason.PrepNotComplete => "Abandoning the walkaround, captain — the ground crew's still setting up.",
        _ => "Abandoning the walkaround, captain — the aircraft's not ready for it.",
    };

    private async Task AbandonAsync(TimeSpan? duration, WalkaroundAbandonReason reason)
    {
        try
        {
            _availability.Clear();
            _eventLog.Record("walkaround.abandoned", new { reason = reason.ToString(), durationMinutes = duration is { } d ? Math.Round(d.TotalMinutes, 1) : (double?)null });
            _logger.LogInformation("FO walkaround abandoned ({Reason})", reason);
            await _arbiter.EnqueueAsync(new SpeechRequest(AbandonLineFor(reason), SpeechPriority.Low, Ttl: TimeSpan.FromMinutes(1), Tag: "walkaround.abandon"), _shutdown.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Walkaround abandon failed");
        }
    }

    private async Task ReportAsync(WalkaroundSample sample, TimeSpan? duration)
    {
        try
        {
            var options = Options;
            var conditions = CurrentConditions(sample);
            var finding = WalkaroundFindingDraw.Draw(_bank.Findings, conditions, options.FindingChance, options.AllowNoGo, Roll);
            var report = ReportText(finding);
            var styled = await _styled.StyleAsync(report, PersonaStyleCategory.Advisory, "walkaround:report:" + (finding?.Id ?? "clean"), _shutdown.Token)
                .ConfigureAwait(false);
            var text = string.IsNullOrWhiteSpace(styled) ? report : styled;

            if (options.OnInterphone)
            {
                await RingInterphoneAsync().ConfigureAwait(false);
            }

            await _arbiter.EnqueueAsync(new SpeechRequest(
                text, SpeechPriority.Normal, Ttl: TimeSpan.FromMinutes(3), Tag: ReportTag,
                Role: options.OnInterphone ? SpeechRole.FirstOfficerOnInterphone : SpeechRole.FirstOfficer),
                _shutdown.Token).ConfigureAwait(false);

            // Back in the cockpit before anything that needs the FO to answer.
            _availability.Clear();
            _eventLog.Record("walkaround.reported", new
            {
                durationMinutes = duration is { } d ? Math.Round(d.TotalMinutes, 1) : (double?)null,
                finding = finding?.Id,
                severity = finding?.Severity.ToString().ToLowerInvariant(),
                onInterphone = options.OnInterphone,
                text,
            });
            _logger.LogInformation("FO walkaround reported ({Finding})", finding?.Id ?? "no finding");

            await HandleFindingAsync(finding, sample).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Walkaround report failed");
            _availability.Clear();
        }
    }

    /// <summary>The report line — the template plus the finding's words. Pure, for tests.</summary>
    public static string ReportText(WalkaroundFinding? finding)
        => finding is null ? ReportTemplate : ReportTemplate + " " + finding.Spoken;

    /// <summary>What a finding leads to after the report. Pure for the routing decision; the
    /// side effects follow in <see cref="HandleFindingAsync"/>.</summary>
    public static WalkaroundOutcome Outcome(WalkaroundFinding? finding, bool allowNoGo)
        => finding switch
        {
            null => WalkaroundOutcome.None,
            { Deice: true } => WalkaroundOutcome.Deice,
            { Severity: WalkaroundSeverity.NoGo } when allowNoGo && finding.TechLogTitle is not null => WalkaroundOutcome.Raise,
            { Severity: WalkaroundSeverity.Deferrable } when finding.TechLogTitle is not null => WalkaroundOutcome.Ask,
            _ => WalkaroundOutcome.Cosmetic,
        };

    private async Task HandleFindingAsync(WalkaroundFinding? finding, WalkaroundSample sample)
    {
        var outcome = Outcome(finding, Options.AllowNoGo);
        switch (outcome)
        {
            case WalkaroundOutcome.Deice:
                RaiseDeiceQuestion();
                break;

            case WalkaroundOutcome.Ask:
                // The finding becomes the pending one only when the question is actually
                // raised: a refused Ask (one already stands) used to overwrite the standing
                // question's finding, so the captain's "yes" logged the wrong defect (review
                // 2026-11-05).
                WalkaroundFinding? standing;
                lock (_gate)
                {
                    standing = _pendingFinding;
                    _pendingFinding = finding;
                }

                if (!_raise.Ask(finding!.Id, finding.TechLogTitle!, finding.TechLogCategory))
                {
                    lock (_gate)
                    {
                        _pendingFinding = standing;
                    }

                    _logger.LogInformation("Walkaround: the raise question already stands — finding {Id} stays noted", finding.Id);
                }

                break;

            case WalkaroundOutcome.Raise:
                var defect = _techLog.RaiseDefect(_techLog.NewDraft("walkaround:" + finding!.Id, finding.TechLogTitle!, finding.TechLogCategory));
                _eventLog.Record("walkaround.techLogRaised", new { finding = finding.Id, title = finding.TechLogTitle, category = finding.TechLogCategory.ToString(), defectId = defect.Id, confirmed = false });
                await _arbiter.EnqueueAsync(new SpeechRequest(
                    $"I've raised it in the tech log, captain — {finding.TechLogTitle}. Your call from here.", SpeechPriority.Normal, Tag: "walkaround.nogo"),
                    _shutdown.Token).ConfigureAwait(false);
                break;
        }

        _eventLog.Record("walkaround.finding", new { finding = finding?.Id, outcome = outcome.ToString().ToLowerInvariant() });
    }

    private void RaiseDeiceQuestion()
    {
        if (_deice is null)
        {
            return;
        }

        try
        {
            var facts = _weather?.Snapshot().Local.Facts;
            var icao = _ofp?.Current?.OriginIcao;
            _deice.Publish(DeicePolicyVerdict.Ask, "walkaround: ice on the wing upper surface", facts?.TemperatureC, facts?.Precip.ToString().ToLowerInvariant() ?? "unknown", icao, DeicePrompt);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Walkaround: de-ice question could not be raised");
        }
    }

    private void OnRaiseAnswered(object? sender, (bool Accepted, string Source) answer)
    {
        WalkaroundFinding? finding;
        lock (_gate)
        {
            finding = _pendingFinding;
            _pendingFinding = null;
        }

        if (finding?.TechLogTitle is null)
        {
            return;
        }

        if (!answer.Accepted)
        {
            _eventLog.Record("walkaround.noted", new { finding = finding.Id, title = finding.TechLogTitle, source = answer.Source });
            return;
        }

        if (_techLogDialogue is null)
        {
            _logger.LogInformation("Walkaround: no tech-log dialogue registered — finding {Id} stays noted", finding.Id);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var raised = await _techLogDialogue.RaisePrefilledAsync(finding.TechLogTitle, finding.TechLogCategory, "walkaround:" + finding.Id, _shutdown.Token).ConfigureAwait(false);
                _eventLog.Record("walkaround.techLogRaised", new { finding = finding.Id, title = finding.TechLogTitle, category = finding.TechLogCategory.ToString(), confirmed = raised });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Walkaround: the pre-filled raise failed");
            }
        });
    }

    private WalkaroundConditions CurrentConditions(WalkaroundSample sample)
    {
        var facts = _weather?.Snapshot().Local.Facts ?? WxFacts.None;
        var icing = false;
        try
        {
            icing = MetarConditions.GroundIcingLikely(facts);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Walkaround: icing predicate failed");
        }

        var night = _clock?.LocalTimeOfDay is { } t && (t.TotalHours < 6 || t.TotalHours >= 20);
        var afterLanding = _flight.Snapshot().HasBeenAirborneThisSession;
        return new WalkaroundConditions(icing, facts.Precip != PrecipKind.None, night, afterLanding);
    }

    /// <summary>The upcalls' delivery: the MECH call press (the ACP lamp cue), then the INT
    /// latch or the grace — a report is never lost to an unmonitored panel.</summary>
    private Task RingInterphoneAsync()
    {
        var options = _options.CurrentValue;
        return InterphoneRing.RingAsync(_dataRefs, _acp, options.MechCall, options.RequireIntChannel, options.IntChannelGraceSeconds, _logger, "walkaround report", _shutdown.Token);
    }
}
