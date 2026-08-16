using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Day;
using ProsimCompanion.Core.Debrief;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Logbook;
using ProsimCompanion.Core.Sessions;
using ProsimCompanion.Core.TechLog;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Company;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Day;

/// <summary>
/// Company day mode (Prosim2FO semantics): tracks a multi-leg rotation as one continuous duty —
/// turnaround detection at Shutdown, per-leg session rotation at the next Preflight, cumulative
/// block/duty via the ONE <see cref="DayMath"/> formula set, and an end-of-day summary + logbook
/// fold. Convenience tracking, never FTL compliance. The state machine itself is the pure
/// <see cref="CompanyDayEngine"/>; this service adds the clock, persistence, speech and events.
///
/// <para>Leg facts are filled by this service acting as the Order-40 session-finalization step —
/// after the debrief (10), logbook (20) and tech log (30) have read the same session file — which
/// replaces the predecessor's fragile 1700 ms "hopefully after the debrief" delay with an
/// explicit ordering guarantee. The event-log session rotates ONLY at next-leg start, so the
/// finalization steps always read the completed leg's file before it is retired.</para>
///
/// <para>Deviation watch is post-hoc by design: the plan-vs-reality comparison runs at leg
/// completion against the extracted destination, because this codebase emits
/// <c>flight.route</c> only when a briefing resolves — there is no continuous in-flight
/// destination event to hang a live advisory on (the predecessor's mid-flight query needed
/// one). When such an event exists, an in-flight variant can layer on top.</para>
/// </summary>
public sealed class CompanyDayService : IVoiceFeature, ISessionFinalizationStep, IDayControl, IDisposable
{
    private static readonly string[] StartPhrases = ["start duty day", "begin duty day", "start the duty day"];
    private static readonly string[] EndPhrases = ["end duty day", "close duty day", "end the duty day"];

    private readonly IFlightPhaseSource _flight;
    private readonly JsonlEventLog _eventLog;
    private readonly IDebriefFactExtractor _extractor;
    private readonly ITechLogService _techLog;
    private readonly ILogbookService _logbook;
    private readonly IDaySummaryComposer _composer;
    private readonly DayStateFile _stateFile;
    private readonly DayStatusStore _store;
    private readonly ICompanyChannel _company;
    private readonly ISpeechArbiter _arbiter;
    private readonly IOptionsMonitor<DayOptions> _options;
    private readonly ILogger<CompanyDayService> _logger;

    // Optional: with no name source, airports stay spelled ("E G L L") — issue #70.
    private readonly Core.Airports.IAirportNames? _airportNames;

    private readonly object _gate = new();
    private readonly CompanyDayEngine _engine = new();
    private DateTimeOffset _lastActivityUtc = DateTimeOffset.UtcNow;
    private Timer? _timer;
    private bool _started;

    public CompanyDayService(
        IFlightPhaseSource flight,
        JsonlEventLog eventLog,
        IDebriefFactExtractor extractor,
        ITechLogService techLog,
        ILogbookService logbook,
        IDaySummaryComposer composer,
        DayStateFile stateFile,
        DayStatusStore store,
        ICompanyChannel company,
        ISpeechArbiter arbiter,
        IOptionsMonitor<DayOptions> options,
        ILogger<CompanyDayService> logger,
        Core.Airports.IAirportNames? airportNames = null)
    {
        _airportNames = airportNames;
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(extractor);
        ArgumentNullException.ThrowIfNull(techLog);
        ArgumentNullException.ThrowIfNull(logbook);
        ArgumentNullException.ThrowIfNull(composer);
        ArgumentNullException.ThrowIfNull(stateFile);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(company);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _flight = flight;
        _eventLog = eventLog;
        _extractor = extractor;
        _techLog = techLog;
        _logbook = logbook;
        _composer = composer;
        _stateFile = stateFile;
        _store = store;
        _company = company;
        _arbiter = arbiter;
        _options = options;
        _logger = logger;
    }

    string ISessionFinalizationStep.Name => "day";

    /// <summary>After debrief (10), logbook (20) and tech log (30) — the leg fill reads the
    /// same session file and must never race the stores it summarizes.</summary>
    int ISessionFinalizationStep.Order => 40;

    IEnumerable<string> IVoiceFeature.Phrases => StartPhrases.Concat(EndPhrases);

    public bool ValueParse => false;

    /// <summary>"Leg {n} of {m} complete." (planned multi-leg) / "Leg {n} complete."; null
    /// when no day is open. The debrief integrator appends it to the per-sector debrief.</summary>
    public string? DebriefContextLine
    {
        get
        {
            lock (_gate)
            {
                return _engine.DebriefContextLine;
            }
        }
    }

    /// <summary>Loads persisted state (an open day resumes), hooks the phase engine and starts
    /// the 60 s tick that keeps the duty figure fresh and drives idle auto-close.</summary>
    public void Start()
    {
        DaySnapshot snapshot;
        string? resumedDayId = null;
        int legIndex = 0, legCount = 0;
        lock (_gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
            _engine.Resume(_stateFile.Load());
            _flight.PhaseChanged += OnPhaseChanged;
            _timer = new Timer(_ => TimerTick(), null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));

            if (_engine.Day is { IsOpen: true } day)
            {
                resumedDayId = day.DayId;
                _lastActivityUtc = DateTimeOffset.UtcNow;
                legIndex = day.CurrentLegIndex;
                legCount = day.Legs.Count;
            }

            snapshot = _engine.BuildSnapshot(DateTimeOffset.UtcNow);
        }

        _store.Update(snapshot);
        if (resumedDayId is not null)
        {
            _eventLog.Record("day.resumed", new { dayId = resumedDayId, leg = legIndex });
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(3000).ConfigureAwait(false); // let startup speech settle
                    Speak($"Continuing the duty day — leg {legIndex} of {legCount}. Say end duty day to finish.",
                        SpeechPriority.Low);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Day resume announcement failed");
                }
            });
        }

        _logger.LogInformation("Company day service started ({State})",
            _engine.Day is { } d ? d.State.ToString() : "no day");
    }

    public void Dispose()
    {
        _flight.PhaseChanged -= OnPhaseChanged;
        _timer?.Dispose();
    }

    // ---- phase-driven state machine (never modifies the flight phases) ----

    private void OnPhaseChanged(object? sender, FlightPhaseChangedEventArgs e)
    {
        try
        {
            HandlePhase(e.Current, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Day phase handling failed");
        }
    }

    /// <summary>One phase edge — exposed for tests; the event handler passes the wall clock.</summary>
    public void HandlePhase(FlightPhase current, DateTimeOffset nowUtc)
    {
        var options = _options.CurrentValue;
        DaySnapshot? snapshot = null;
        string? speak = null;
        string? companyMessage = null;
        var autoStart = false;

        lock (_gate)
        {
            _lastActivityUtc = nowUtc;
            var day = _engine.Day;
            if (day is null || !day.IsOpen)
            {
                autoStart = current == FlightPhase.Preflight && options.Enabled && options.AutoStart;
            }
            else
            {
                switch (current)
                {
                    case FlightPhase.PushbackAndStart or FlightPhase.TaxiOut:
                        if (_engine.StampOffBlocks(nowUtc))
                        {
                            _stateFile.Save(day);
                            snapshot = _engine.BuildSnapshot(nowUtc);
                        }

                        break;

                    case FlightPhase.Shutdown when day.State == DayPhase.OnLeg:
                        var sessionId = Path.GetFileNameWithoutExtension(_eventLog.Path);
                        var completed = _engine.CompleteLeg(sessionId, nowUtc);
                        if (completed is { } legIndex)
                        {
                            _stateFile.Save(day);
                            _eventLog.Record("day.leg.completed", new { leg = legIndex, sessionId });
                            snapshot = _engine.BuildSnapshot(nowUtc);
                        }

                        break;

                    case FlightPhase.Preflight when day.State == DayPhase.Turnaround:
                        // Rotate FIRST so the new leg's events land in a fresh file. The completed
                        // leg's file was read by the finalization steps back at Shutdown.
                        _eventLog.StartNewSession();
                        var leg = _engine.StartNextLeg();
                        if (leg is not null)
                        {
                            _stateFile.Save(day);
                            _eventLog.Record("day.leg.started", new { leg = leg.Index });
                            snapshot = _engine.BuildSnapshot(nowUtc);
                            speak = $"Leg {leg.Index}. New sector.";
                            if (day.Mode == DayMode.Planned)
                            {
                                companyMessage = NextSectorMessage(leg);
                            }
                        }

                        break;
                }
            }
        }

        if (autoStart)
        {
            TryStartDay("auto", nowUtc);
        }

        if (snapshot is not null)
        {
            _store.Update(snapshot);
        }

        if (speak is not null)
        {
            Speak(speak, SpeechPriority.Low);
        }

        if (companyMessage is not null)
        {
            _company.DeliverMessage(companyMessage);
        }
    }

    /// <summary>"Next sector, {From} to {To}, flight {FlightNo}, scheduled off-blocks {HH:mm}
    /// zulu." — only the parts the plan actually has; null when it has none of them. Airports
    /// speak by name when known, spelled otherwise (issue #70 — the raw "EGLL" the TTS used
    /// to get came out as a garbled word).</summary>
    private string? NextSectorMessage(DayLeg leg)
    {
        if (string.IsNullOrEmpty(leg.To) && string.IsNullOrEmpty(leg.FlightNo))
        {
            return null;
        }

        var parts = new List<string> { "Next sector" };
        if (!string.IsNullOrEmpty(leg.From) && !string.IsNullOrEmpty(leg.To))
        {
            parts.Add($"{SpokenAirport(leg.From)} to {SpokenAirport(leg.To)}");
        }

        if (!string.IsNullOrEmpty(leg.FlightNo))
        {
            parts.Add($"flight {leg.FlightNo}");
        }

        if (DayMath.ParseUtc(leg.ScheduledOffUtc) is { } off)
        {
            parts.Add($"scheduled off-blocks {off:HH\\:mm} zulu");
        }

        return string.Join(", ", parts) + ".";
    }

    // ---- session finalization: fill the completed leg's facts (Order 40) ----

    Task ISessionFinalizationStep.RunAsync(SessionFinalizationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        FillLegFacts(context, DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }

    /// <summary>Fills the leg keyed by the finalized session's id, then speaks the turnaround
    /// summary (and the deviation note when the plan disagreed). Exposed for tests.</summary>
    public void FillLegFacts(SessionFinalizationContext context, DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            // Cheap pre-check before the file read: some session must belong to this day.
            if (_engine.Day is not { IsOpen: true } day
                || day.Legs.All(l => !string.Equals(l.SessionId, context.SessionId, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }
        }

        var facts = _extractor.Extract(context.SessionPath); // file read outside the lock

        LegFactsOutcome? outcome;
        DaySnapshot snapshot;
        int? delay;
        lock (_gate)
        {
            outcome = _engine.ApplyLegFacts(context.SessionId, facts);
            if (outcome is null || _engine.Day is not { } dayNow)
            {
                return;
            }

            _stateFile.Save(dayNow);
            delay = DayMath.DelayMinutes(dayNow);
            snapshot = _engine.BuildSnapshot(nowUtc);
            if (outcome.DeviationPlanned is not null)
            {
                _eventLog.Record("day.deviation", new
                {
                    leg = outcome.Leg.Index,
                    planned = outcome.DeviationPlanned,
                    actual = outcome.DeviationActual,
                });
            }
        }

        _store.Update(snapshot);

        if (outcome.DeviationPlanned is not null && outcome.DeviationActual is not null)
        {
            // Post-hoc by design — see the class remarks on the deviation watch.
            Speak($"Note — the plan showed {SpokenAirport(outcome.DeviationPlanned)}, "
                + $"but we flew to {SpokenAirport(outcome.DeviationActual)}.", SpeechPriority.Normal);
        }

        if (_options.CurrentValue.TurnaroundSummary && outcome.Leg.BlockMinutes is { } block)
        {
            var open = OpenDefectCountSafe(); // tech log read outside any lock
            Speak(DaySummaryComposer.TurnaroundLine(outcome.Leg.Index, block, delay, open), SpeechPriority.Low);
        }
    }

    // ---- start / end / tick ----

    /// <summary>Starts a day (loading any planned rotation); false when one is already open.</summary>
    public bool TryStartDay(string source) => TryStartDay(source, DateTimeOffset.UtcNow);

    public bool TryStartDay(string source, DateTimeOffset nowUtc)
    {
        var options = _options.CurrentValue;
        DaySnapshot snapshot;
        string speak;
        lock (_gate)
        {
            var rotation = RotationLoader.TryLoad(options.RotationsFolder);
            if (!_engine.StartDay(rotation, options.PostFlightAllowanceMinutes, nowUtc))
            {
                return false;
            }

            var day = _engine.Day!;
            _lastActivityUtc = nowUtc;
            _stateFile.Save(day);
            _eventLog.Record("day.started", new
            {
                dayId = day.DayId,
                mode = day.Mode.ToString().ToLowerInvariant(),
                source,
                legs = day.Legs.Count,
            });
            snapshot = _engine.BuildSnapshot(nowUtc);
            speak = day.Mode == DayMode.Planned
                ? $"Duty day started — {day.Legs.Count} sectors planned."
                : "Duty day started.";
        }

        _store.Update(snapshot);
        Speak(speak, SpeechPriority.Normal);
        _logger.LogInformation("Duty day started ({Source})", source);
        return true;
    }

    /// <summary>Ends the open day: summary spoken and persisted beside the session logs, the
    /// logbook day record folded (idempotent by DayId). False when no day is open.</summary>
    public bool TryEndDay(string source) => TryEndDay(source, DateTimeOffset.UtcNow);

    public bool TryEndDay(string source, DateTimeOffset nowUtc)
    {
        DayState? ended;
        DaySnapshot snapshot;
        lock (_gate)
        {
            ended = _engine.EndDay();
            if (ended is null)
            {
                return false;
            }

            _stateFile.Save(ended);
            _eventLog.Record("day.ended", new { dayId = ended.DayId, legs = ended.LegsCompleted, source });
            snapshot = _engine.BuildSnapshot(nowUtc);
        }

        _store.Update(snapshot);

        try
        {
            _logbook.RecordDay(_composer.BuildRecord(ended, nowUtc));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Day logbook record failed");
        }

        try
        {
            var text = _composer.Compose(ended, nowUtc);
            PersistSummary(ended, text);
            Speak(text, SpeechPriority.Low); // outranked by any late High/Critical speech
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "End-of-day summary failed");
        }

        _logger.LogInformation("Duty day {DayId} ended ({Source})", ended.DayId, source);
        return true;
    }

    private void PersistSummary(DayState day, string text)
    {
        try
        {
            var directory = Path.GetDirectoryName(_eventLog.Path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            File.WriteAllText(Path.Combine(directory, $"{day.DayId}.summary.txt"), text);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Day summary persist failed");
        }
    }

    private void TimerTick()
    {
        try
        {
            ProcessTick(DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Day tick failed");
        }
    }

    /// <summary>One 60 s tick: idle auto-close from a turnaround, else a view refresh so the
    /// duty figure stays live on the web page. Exposed for tests.</summary>
    public void ProcessTick(DateTimeOffset nowUtc)
    {
        var options = _options.CurrentValue;
        var idleClose = false;
        DaySnapshot? snapshot = null;
        lock (_gate)
        {
            if (_engine.Day is not { IsOpen: true } day)
            {
                return;
            }

            if (day.State == DayPhase.Turnaround
                && options.AutoCloseIdleMinutes > 0
                && (nowUtc - _lastActivityUtc).TotalMinutes >= options.AutoCloseIdleMinutes)
            {
                idleClose = true;
            }
            else
            {
                snapshot = _engine.BuildSnapshot(nowUtc);
            }
        }

        if (idleClose)
        {
            TryEndDay("idle", nowUtc);
        }
        else if (snapshot is not null)
        {
            _store.Update(snapshot);
        }
    }

    // ---- voice + web control ----

    public bool TryHandle(string utterance)
    {
        if (string.IsNullOrWhiteSpace(utterance))
        {
            return false;
        }

        var text = utterance.Trim();
        if (StartPhrases.Any(p => string.Equals(p, text, StringComparison.OrdinalIgnoreCase)))
        {
            if (!TryStartDay("voice"))
            {
                Speak("Duty day is already running.", SpeechPriority.Normal);
            }

            return true;
        }

        if (EndPhrases.Any(p => string.Equals(p, text, StringComparison.OrdinalIgnoreCase)))
        {
            if (!TryEndDay("voice"))
            {
                Speak("No duty day is running.", SpeechPriority.Normal);
            }

            return true;
        }

        return false;
    }

    void IDayControl.StartDay() => TryStartDay("web");

    void IDayControl.EndDay() => TryEndDay("web");

    // ---- helpers ----

    private int OpenDefectCountSafe()
    {
        try
        {
            return _techLog.OpenDefects.Count;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Tech log open-defect count unavailable");
            return 0;
        }
    }

    private void Speak(string text, SpeechPriority priority)
        => _ = _arbiter.EnqueueAsync(new SpeechRequest(text, priority, Tag: "day"));

    /// <summary>Name when known ("Heathrow"), spelled ICAO otherwise ("E G C C").</summary>
    private string SpokenAirport(string icao)
        => _airportNames?.SpokenName(icao) ?? Spell(icao);

    /// <summary>Spelled ICAO for TTS ("EGCC" → "E G C C").</summary>
    private static string Spell(string icao)
        => string.Join(" ", icao.Trim().ToUpperInvariant().ToCharArray());
}
