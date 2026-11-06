using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Day;
using ProsimCompanion.Core.Debrief;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Logbook;
using ProsimCompanion.Core.Sessions;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.TechLog;
using ProsimCompanion.Core.VoiceReference;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Company;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Day;

/// <summary>
/// Company day mode (Prosim2FO semantics): tracks a multi-leg rotation as one continuous duty —
/// turnaround detection at Shutdown, the next leg on the next leg's OFP, cumulative block/duty
/// via the ONE <see cref="DayMath"/> formula set, and an end-of-day summary + logbook fold.
/// Convenience tracking, never FTL compliance. The state machine itself is the pure
/// <see cref="CompanyDayEngine"/>; this service adds the clock, persistence, speech and events.
///
/// <para>A leg begins when its OFP is loaded (ADR-0019 §1/§5, issue #212): the next leg starts
/// on <see cref="ILegBoundaryEvents.LegBegan"/> — a DIFFERENT plan in the store — never on the
/// arrival's Shutdown → Preflight edge. Before this the Preflight edge started "Leg 2. New
/// sector" thirty seconds after deboarding with the plan just flown as its route, and the
/// day's last landing grew a phantom leg (EGLL 2026-11-01). The Preflight edge now only
/// auto-starts a day when none is open.</para>
///
/// <para>Leg facts are filled by this service acting as the Order-40 session-finalization step —
/// after the debrief (10), logbook (20) and tech log (30) have read the same session file — which
/// replaces the predecessor's fragile 1700 ms "hopefully after the debrief" delay with an
/// explicit ordering guarantee. The event-log session rotates ONLY at next-leg start (the new
/// plan), so the finalization steps always read the completed leg's file before it is retired
/// and the turnaround's own events (deboarding, the FMS clear) stay with the arrival.</para>
///
/// <para>Deviation watch is post-hoc by design: the plan-vs-reality comparison runs at leg
/// completion against the extracted destination, because this codebase emits
/// <c>flight.route</c> only when a briefing resolves — there is no continuous in-flight
/// destination event to hang a live advisory on (the predecessor's mid-flight query needed
/// one). When such an event exists, an in-flight variant can layer on top.</para>
///
/// <para>End of duty is the pilot's act (ADR-0019 §8, issues #217/#218/#222): voice, the web
/// button, or the FO's one-time offer once the aircraft has been cold and dark with no plan for
/// <c>day.endOfDutyOfferMinutes</c>. The idle backstop counts from deboarding complete (else
/// on-blocks) — <see cref="DayIdleRules"/> — never from a phase edge or an app start. Ending
/// speaks the debrief (fuel and the tech-log entries by name, exempt from the dialogue hold)
/// and raises <see cref="DutyDaySignals.DayEnded"/> so the snapshot, the departure cycle and
/// the roster let go of the old leg.</para>
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

    // The loaded OFP seeds the open leg's route (issue #164) — the page was "---- → ----"
    // until shutdown, and forever for a pilot with speech off.
    private readonly Core.Aircraft.Ofp.OfpStore _ofp;

    // Optional: with no name source, airports stay spelled ("E G L L") — issue #70.
    private readonly Core.Speech.ISpokenText _spokenText;

    // The "a leg began" edge (ADR-0019 §1): a different OFP landed. Core-owned, so the Day
    // engine never reads the phase edge for it.
    private readonly ILegBoundaryEvents _legBoundary;

    // Deboarding complete starts the idle backstop (ADR-0019 §8); the offer store carries the
    // FO's "end of our duty day?" question; the duty-day signals tell the other pillars the
    // day ended (issue #218).
    private readonly GroundOpsSignals _signals;
    private readonly EndOfDutyOfferStore _offer;
    private readonly DutyDaySignals _dutySignals;

    private readonly object _gate = new();
    private readonly CompanyDayEngine _engine = new();
    private DateTimeOffset? _coldAndDarkSinceUtc;
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
        Core.Speech.ISpokenText spokenText,
        Core.Aircraft.Ofp.OfpStore ofp,
        ILegBoundaryEvents legBoundary,
        GroundOpsSignals signals,
        EndOfDutyOfferStore endOfDutyOffer,
        DutyDaySignals dutySignals)
    {
        ArgumentNullException.ThrowIfNull(spokenText);
        ArgumentNullException.ThrowIfNull(ofp);
        ArgumentNullException.ThrowIfNull(legBoundary);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(endOfDutyOffer);
        ArgumentNullException.ThrowIfNull(dutySignals);
        _spokenText = spokenText;
        _ofp = ofp;
        _legBoundary = legBoundary;
        _signals = signals;
        _offer = endOfDutyOffer;
        _dutySignals = dutySignals;
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

    /// <summary>Roster declaration (see <see cref="VoiceFeatureDeclaration"/>): the duty-day
    /// start/end phrases, after the company channel. Always on as a voice feature — day.enabled
    /// gates what the phrases DO, not whether they are heard.</summary>
    public static VoiceFeatureDeclaration Declared { get; } = new()
    {
        Precedence = 190,
        Gate = VoiceFeatureGate.AlwaysOn,
        Description = new DescribedVoiceReference("day", "voiceReference.group.day.title", VoiceReferenceTab.FirstOfficer, VoiceSpeaker.FirstOfficer,
        [
            new(["start duty day", "begin duty day", "start the duty day"], "voiceReference.group.day.start"),
            new(["end duty day", "close duty day", "end the duty day"], "voiceReference.group.day.end"),
        ]),
    };

    VoiceFeatureDeclaration IVoiceFeature.Declaration => Declared;

    bool IVoiceFeature.Enabled => true;

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
        int? phantomDropped = null;
        lock (_gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
            ConfigureFdp();
            _engine.Resume(_stateFile.Load());
            phantomDropped = _engine.DropPhantomLeg();
            _flight.PhaseChanged += OnPhaseChanged;
            _ofp.Changed += OnOfpChanged;
            _legBoundary.LegBegan += OnLegBegan;
            _signals.ArrivalCompleted += OnArrivalCompleted;
            _offer.Answered += OnOfferAnswered;
            // A restart in the dark: the cold-and-dark wait for the offer starts now, never
            // from a stamp this process did not see (the idle backstop has its own, persisted).
            _coldAndDarkSinceUtc = _flight.CurrentPhase == FlightPhase.ColdAndDark ? DateTimeOffset.UtcNow : null;
            _timer = new Timer(_ => TimerTick(), null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));

            if (_engine.Day is { IsOpen: true } day)
            {
                resumedDayId = day.DayId;
                legIndex = day.CurrentLegIndex;
                legCount = day.Legs.Count;
                if (phantomDropped is not null)
                {
                    _stateFile.Save(day);
                }

                SeedRouteFromOfpLocked(); // a resumed open leg takes the OFP already loaded
            }

            snapshot = _engine.BuildSnapshot(DateTimeOffset.UtcNow);
        }

        _store.Update(snapshot);
        if (phantomDropped is { } dropped)
        {
            _logger.LogWarning(
                "Day state held a never-flown leg {Leg} that duplicated the leg before it (an older build started it on the Preflight edge) — dropped; the day is back in its turnaround",
                dropped);
            _eventLog.Record("day.leg.dropped", new { dayId = resumedDayId, leg = dropped, reason = "phantom: duplicated the completed leg, never moved" });
        }

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
        _ofp.Changed -= OnOfpChanged;
        _legBoundary.LegBegan -= OnLegBegan;
        _signals.ArrivalCompleted -= OnArrivalCompleted;
        _offer.Answered -= OnOfferAnswered;
        _timer?.Dispose();
    }

    // ---- deboarding complete: the idle backstop counts from here (ADR-0019 §8, #217) ----

    private void OnArrivalCompleted()
    {
        try
        {
            HandleDeboarded(DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Day deboarding handling failed");
        }
    }

    /// <summary>GSX deboarding completed while the day is in a turnaround: the idle clock
    /// restarts from this moment (on-blocks started it). Exposed for tests.</summary>
    public void HandleDeboarded(DateTimeOffset nowUtc)
    {
        string? dayId = null;
        lock (_gate)
        {
            if (!_engine.MarkDeboarded(nowUtc) || _engine.Day is not { } day)
            {
                return;
            }

            _stateFile.Save(day);
            dayId = day.DayId;
        }

        _eventLog.Record("day.idle.started", new { dayId, from = "deboarding complete" });
    }

    // ---- a leg began: a different OFP landed (ADR-0019 §1/§5, issue #212) ----

    private void OnLegBegan(object? sender, LegBeganEventArgs e)
    {
        try
        {
            HandleLegBegan(e.PlanId, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Day leg-began handling failed");
        }
    }

    /// <summary>
    /// The next leg begins on its plan: in a turnaround the event-log session rotates, the
    /// next leg opens with the new OFP as its route and the FO announces the sector — "Leg 2.
    /// New sector, Amsterdam to Heathrow." With no day open (the first plan of the day landed
    /// before any Preflight, or a plan after an end of duty) the day auto-starts here when the
    /// automation is on, leg 1 seeded from the plan. On a leg already under way the plan only
    /// re-seeds the route (the <see cref="OnOfpChanged"/> path). Exposed for tests.
    /// </summary>
    public void HandleLegBegan(string planId, DateTimeOffset nowUtc)
    {
        var options = _options.CurrentValue;
        DaySnapshot? snapshot = null;
        string? speak = null;
        string? companyMessage = null;
        var autoStart = false;

        lock (_gate)
        {
            var day = _engine.Day;
            if (day is null || !day.IsOpen)
            {
                autoStart = options.Enabled && options.AutoStart;
            }
            else if (day.State == DayPhase.Turnaround)
            {
                // Rotate FIRST so the new leg's events land in a fresh file. The completed
                // leg's file was read by the finalization steps back at Shutdown; the
                // turnaround's own events stayed with the arrival.
                _eventLog.StartNewSession();
                var leg = _engine.StartNextLeg();
                if (leg is not null)
                {
                    SeedRouteFromOfpLocked(); // the plan that began this leg is its route
                    _stateFile.Save(day);
                    _eventLog.Record("day.leg.started", new
                    {
                        leg = leg.Index,
                        planId,
                        from = leg.From,
                        to = leg.To,
                        flightNo = leg.FlightNo,
                    });
                    snapshot = _engine.BuildSnapshot(nowUtc);
                    speak = NewSectorLine(leg);
                    if (day.Mode == DayMode.Planned)
                    {
                        companyMessage = NextSectorMessage(leg);
                    }
                }
            }
        }

        if (autoStart)
        {
            TryStartDay("ofp", nowUtc);
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

    /// <summary>"Leg 2. New sector, Amsterdam to Heathrow." — the route when the plan has
    /// both ends, "Leg 2. New sector." otherwise. Airports by name when known, spelled
    /// otherwise (issue #70).</summary>
    private string NewSectorLine(DayLeg leg)
        => !string.IsNullOrEmpty(leg.From) && !string.IsNullOrEmpty(leg.To)
            ? $"Leg {leg.Index}. New sector, {SpokenAirport(leg.From)} to {SpokenAirport(leg.To)}."
            : $"Leg {leg.Index}. New sector.";

    // ---- OFP → open leg route (issue #164) ----

    private void OnOfpChanged(object? sender, EventArgs e)
    {
        try
        {
            DaySnapshot? snapshot = null;
            lock (_gate)
            {
                if (SeedRouteFromOfpLocked() is not null)
                {
                    snapshot = _engine.BuildSnapshot(DateTimeOffset.UtcNow);
                }
            }

            if (snapshot is not null)
            {
                _store.Update(snapshot);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Day OFP route seed failed");
        }
    }

    /// <summary>Seeds the open leg from the loaded OFP; call under <c>_gate</c>. Persists and
    /// records <c>day.leg.route</c> when anything changed; callers publish the snapshot.</summary>
    private DayLeg? SeedRouteFromOfpLocked()
    {
        if (_ofp.Current is not { } ofp || _engine.Day is not { } day)
        {
            return null;
        }

        var leg = _engine.SeedRouteFromOfp(ofp.OriginIcao, ofp.DestinationIcao, ofp.FlightNumber);
        if (leg is null)
        {
            return null;
        }

        _stateFile.Save(day);
        _eventLog.Record("day.leg.route", new
        {
            leg = leg.Index,
            from = leg.From,
            to = leg.To,
            flightNo = leg.FlightNo,
            source = "ofp",
        });
        return leg;
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

    /// <summary>One phase edge — exposed for tests; the event handler passes the wall clock.
    /// Off-blocks and on-blocks stamp the leg; the Preflight edge only auto-starts a day when
    /// none is open. In a turnaround the Preflight edge does NOTHING to the legs — the next leg
    /// waits for its plan (<see cref="HandleLegBegan"/>). No edge touches the idle backstop
    /// (ADR-0019 §8): it counts from deboarding complete, whatever the phase does after.</summary>
    public void HandlePhase(FlightPhase current, DateTimeOffset nowUtc)
    {
        var options = _options.CurrentValue;
        DaySnapshot? snapshot = null;
        var autoStart = false;

        lock (_gate)
        {
            // The offer's cold-and-dark wait: from the first ColdAndDark edge, reset by any
            // other phase (power back on = the pilot is not done).
            _coldAndDarkSinceUtc = current == FlightPhase.ColdAndDark ? _coldAndDarkSinceUtc ?? nowUtc : null;
            var day = _engine.Day;
            if (day is null)
            {
                // Only a session that has never had a day auto-starts one on the Preflight edge.
                // An ENDED day is not "no day": after the end of duty the next ColdAndDark →
                // Preflight (a power cycle, the next sim morning before the OFP) used to re-open
                // a day on the flown plan — ADR-0019 §8 says the OFP opens it (HandleLegBegan).
                autoStart = current == FlightPhase.Preflight && options.Enabled && options.AutoStart;
            }
            else if (day.IsOpen)
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

        // The FDP check for the next sector (issue #189): one spoken line only when the
        // projection is past the table's maximum; the Day page banner carries the figures.
        if (snapshot.Fdp is { } fdp)
        {
            _eventLog.Record("day.fdp", new
            {
                report = fdp.ReportUtc,
                reportSource = fdp.ReportSource,
                sectorsFlown = fdp.SectorsFlown,
                sectorsWithNext = fdp.SectorsWithNext,
                maxMinutes = fdp.MaxFdpMinutes,
                soFarMinutes = fdp.FdpSoFarMinutes,
                projectedEnd = fdp.ProjectedEndUtc,
                projectedMinutes = fdp.ProjectedFdpMinutes,
                exceeds = fdp.Exceeds,
            });
            if (fdp.Exceeds && fdp.ProjectedEndUtc is { } end)
            {
                Speak(DaySummaryComposer.FdpWarningLine(end, fdp.MaxFdpMinutes, fdp.ReportUtc, fdp.SectorsWithNext), SpeechPriority.Normal);
            }
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
            _stateFile.Save(day);
            _eventLog.Record("day.started", new
            {
                dayId = day.DayId,
                mode = day.Mode.ToString().ToLowerInvariant(),
                source,
                legs = day.Legs.Count,
            });
            SeedRouteFromOfpLocked(); // leg 1 shows the OFP route from the first minute (#164)
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

        // A standing offer is answered by the act itself (the button, the voice command).
        try
        {
            _offer.Withdraw();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "End-of-duty offer withdraw failed");
        }

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
            // Fuel and the tech-log entries by name (ADR-0019 §8, #222); the debrief tag is
            // exempt from the dialogue hold — it ends the day, nothing outranks it then.
            var text = _composer.Compose(ended, nowUtc, DefectTitlesRaisedSafe(ended, nowUtc));
            PersistSummary(ended, text);
            Speak(text, SpeechPriority.Low, DebriefTag);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "End-of-day summary failed");
        }

        // The other pillars let go of the old leg (issue #218): the snapshot, the departure
        // cycle, the roster pin. Raised last, after the day's own records are final.
        try
        {
            _dutySignals.RaiseDayEnded(new DutyDayEnded(ended.DayId, source, nowUtc));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A duty-day-ended subscriber failed");
        }

        _logger.LogInformation("Duty day {DayId} ended ({Source})", ended.DayId, source);
        return true;
    }

    /// <summary>The speech tag of the end-of-duty debrief — exempt from the dialogue hold.</summary>
    public const string DebriefTag = "day.debrief";

    /// <summary>The tech-log entries raised during the day, by title: raised on one of the
    /// day's sessions, or dated within the day's calendar span (the tech log stamps UTC dates).
    /// Empty when the tech log is off or unreadable — the composer falls back to the counts.</summary>
    private IReadOnlyList<string> DefectTitlesRaisedSafe(DayState day, DateTimeOffset nowUtc)
    {
        try
        {
            var sessions = day.Legs.Select(l => l.SessionId).Where(s => !string.IsNullOrEmpty(s)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var start = DayMath.ParseUtc(day.StartedUtc)?.UtcDateTime.Date;
            var end = nowUtc.UtcDateTime.Date;
            return _techLog.Defects
                .Where(d => (d.RaisedFlight is { } flight && sessions.Contains(flight))
                    || (start is { } s && DateTime.TryParseExact(d.RaisedDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var raised)
                        && raised.Date >= s && raised.Date <= end))
                .Select(d => d.Title)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Tech log entries for the day debrief unavailable");
            return [];
        }
    }

    // ---- the FO's end-of-duty offer (ADR-0019 §8, #217) ----

    private void OnOfferAnswered(object? sender, (bool Accepted, string Source) answer)
    {
        try
        {
            HandleOfferAnswered(answer.Accepted, answer.Source, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "End-of-duty offer answer handling failed");
        }
    }

    /// <summary>Yes ends the day as "offer"; no (or the TTL's "timeout") leaves it open — the
    /// offer was marked made when it was raised, so it is never repeated. Exposed for tests.</summary>
    public void HandleOfferAnswered(bool accepted, string source, DateTimeOffset nowUtc)
    {
        if (accepted)
        {
            TryEndDay("offer", nowUtc);
            return;
        }

        _logger.LogInformation("End-of-duty offer declined ({Source}) — the day stays open until 'end duty day' or the idle backstop", source);
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

    /// <summary>The FO's offer, word for word (the probe <c>day-end-offer</c> reads it).</summary>
    public const string EndOfDutyPrompt = "Captain, is that the end of our duty day?";

    /// <summary>One 60 s tick: the idle backstop from a turnaround (<see cref="DayIdleRules"/>:
    /// from deboarding complete, else on-blocks — never a phase edge or an app start), the FO's
    /// one-time end-of-duty offer once cold and dark with no plan, else a view refresh so the
    /// duty figure stays live on the web page. Exposed for tests.</summary>
    public void ProcessTick(DateTimeOffset nowUtc)
    {
        var options = _options.CurrentValue;
        var idleClose = false;
        var offer = false;
        string? dayId = null;
        DaySnapshot? snapshot = null;
        lock (_gate)
        {
            if (_engine.Day is not { IsOpen: true } day)
            {
                return;
            }

            if (DayIdleRules.ShouldAutoClose(day, options.AutoCloseIdleMinutes, nowUtc))
            {
                idleClose = true;
            }
            else
            {
                if (DayIdleRules.ShouldOffer(day, options.EndOfDutyOfferMinutes, _coldAndDarkSinceUtc, nowUtc)
                    && _engine.MarkEndOfDutyOffered(nowUtc))
                {
                    _stateFile.Save(day);
                    offer = true;
                    dayId = day.DayId;
                }

                snapshot = _engine.BuildSnapshot(nowUtc);
            }
        }

        if (idleClose)
        {
            TryEndDay("idle", nowUtc);
            return;
        }

        if (snapshot is not null)
        {
            _store.Update(snapshot);
        }

        if (offer)
        {
            _eventLog.Record("day.end-offer.raised", new { dayId, prompt = EndOfDutyPrompt, afterMinutes = options.EndOfDutyOfferMinutes });
            if (!_offer.Ask(EndOfDutyPrompt))
            {
                _logger.LogDebug("End-of-duty offer not raised — one already stands or was answered this cycle");
            }
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

    private void Speak(string text, SpeechPriority priority, string tag = "day")
        => _ = _arbiter.EnqueueAsync(new SpeechRequest(text, priority, Tag: tag));

    /// <summary>Loads the user's ftl-easa.json (the shipped table when absent or unreadable) and
    /// hands the engine the FDP settings; called at start and whenever the day options change.</summary>
    private void ConfigureFdp()
    {
        var options = _options.CurrentValue;
        FtlTable table = FtlTable.Default;
        try
        {
            var path = Core.Configuration.UserConfigPaths.File("ftl-easa.json");
            if (File.Exists(path))
            {
                table = FtlTable.Parse(File.ReadAllText(path), path) ?? FtlTable.Default;
                if (table.Source == "shipped")
                {
                    _logger.LogWarning("ftl-easa.json has no usable rows — the shipped EASA table stands");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "ftl-easa.json could not be read — the shipped EASA table stands");
        }

        _engine.FdpConfiguration = new FdpConfiguration(table, options.ReportBeforeStdMinutes, options.FdpWarning);
    }

    /// <summary>Name when known ("Heathrow"), spelled ICAO otherwise ("E G C C").</summary>
    private string SpokenAirport(string icao)
        => _spokenText.Airport(icao);

}
