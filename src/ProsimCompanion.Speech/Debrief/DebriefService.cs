using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Debrief;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Logbook;
using ProsimCompanion.Core.Sessions;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Llm;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Debrief;

/// <summary>
/// The post-flight debrief: extracts the session's facts, composes the spoken text — optional
/// LLM styling behind the number verifier (one strict re-ask, then the deterministic template
/// on ANY failure; same pattern as the briefing), else the template directly — appends the
/// logbook comparison line, persists the text beside the session log, and speaks it at Low
/// priority so any late safety speech outranks it. Runs as the FIRST session-finalization step
/// (order 10) — it reads the session log before the logbook/tech-log folds mutate their stores
/// — plus on voice command or the web button.
/// </summary>
public sealed class DebriefService : ISessionFinalizationStep, IVoiceFeature, IDisposable
{
    private static readonly string[] Phrases =
    [
        "debrief", "debrief now", "flight debrief", "post flight debrief", "give me the debrief",
    ];

    private readonly IDebriefFactExtractor _extractor;
    private readonly ILogbookService _logbook;
    private readonly ISpeechArbiter _arbiter;
    private readonly IFlightPhaseSource _flight;
    private readonly JsonlEventLog _eventLog;
    private readonly IOptionsMonitor<DebriefOptions> _options;
    private readonly OpenAiChatClient _llm;
    private readonly Core.Day.DayStatusStore? _dayStore;
    private readonly ILogger<DebriefService> _logger;
    private readonly object _gate = new();
    private readonly Persona.PersonaService? _persona;

    // Optional: without it the fact block presents raw ICAO idents — issue #70.
    private readonly Core.Airports.IAirportNames? _airportNames;

    private bool _started;
    private bool _doneThisFlight;

    public DebriefService(
        IDebriefFactExtractor extractor,
        ILogbookService logbook,
        ISpeechArbiter arbiter,
        IFlightPhaseSource flight,
        JsonlEventLog eventLog,
        IOptionsMonitor<DebriefOptions> options,
        IOptionsMonitor<BriefingOptions> briefingOptions,
        ILogger<DebriefService> logger,
        OpenAiChatClient? llm = null,
        Core.Day.DayStatusStore? dayStore = null,
        Persona.PersonaService? persona = null,
        Core.Airports.IAirportNames? airportNames = null)
    {
        _persona = persona;
        _airportNames = airportNames;
        ArgumentNullException.ThrowIfNull(extractor);
        ArgumentNullException.ThrowIfNull(logbook);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(briefingOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _dayStore = dayStore;
        _extractor = extractor;
        _logbook = logbook;
        _arbiter = arbiter;
        _flight = flight;
        _eventLog = eventLog;
        _options = options;
        // Optional so DI needs no extra registration (the LLM endpoint settings live in the
        // briefing section); a test passes a client over a fake HTTP handler here.
        _llm = llm ?? new OpenAiChatClient(briefingOptions);
        _logger = logger;
    }

    string ISessionFinalizationStep.Name => "debrief";

    int ISessionFinalizationStep.Order => 10;

    IEnumerable<string> IVoiceFeature.Phrases => Phrases;

    public bool ValueParse => false;

    public void Start()
    {
        lock (_gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
        }

        _flight.PhaseChanged += OnPhaseChanged;
        _logger.LogInformation("Debrief service started");
    }

    public void Dispose() => _flight.PhaseChanged -= OnPhaseChanged;

    /// <summary>Compose + speak now (web button / test), bypassing the once-per-flight guard.
    /// The session log is read as-is — no flush delay, so a mid-flight trigger simply debriefs
    /// what has happened so far. Fire-and-forget: any LLM latency happens off the caller, and
    /// RunAsync never throws.</summary>
    public void TriggerNow() => _ = RunAsync(manual: true, CancellationToken.None);

    public bool TryHandle(string utterance)
    {
        if (string.IsNullOrWhiteSpace(utterance))
        {
            return false;
        }

        var text = utterance.Trim();
        if (!Phrases.Any(p => string.Equals(p, text, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        TriggerNow();
        return true;
    }

    /// <summary>Auto-debrief at shutdown, driven by the finalizer (which already applied the
    /// single flush delay and runs off-thread). Once per flight; a re-run stays silent.</summary>
    Task ISessionFinalizationStep.RunAsync(SessionFinalizationContext context, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_doneThisFlight)
            {
                return Task.CompletedTask;
            }

            _doneThisFlight = true;
        }

        return RunAsync(manual: false, cancellationToken);
    }

    private void OnPhaseChanged(object? sender, FlightPhaseChangedEventArgs e)
    {
        // A fresh flight re-arms the auto-debrief (same double edge as the finalizer).
        if (e.Current is FlightPhase.TakeoffRoll or FlightPhase.Preflight)
        {
            lock (_gate)
            {
                _doneThisFlight = false;
            }
        }
    }

    /// <summary>Extraction is a local file read and the speech enqueue is fire-and-forget —
    /// the only await inside is the optional LLM styling, so with the LLM disabled this
    /// completes synchronously (which keeps the voice/web callers instant).</summary>
    private async Task RunAsync(bool manual, CancellationToken cancellationToken)
    {
        try
        {
            var options = _options.CurrentValue;
            if (!options.Enabled)
            {
                return;
            }

            var sessionPath = _eventLog.Path;
            var facts = _extractor.Extract(sessionPath);
            if (!facts.HasData)
            {
                _logger.LogInformation("Debrief skipped — no usable data in the session log");
                return;
            }

            var verbosity = string.Equals(options.Verbosity, "brief", StringComparison.OrdinalIgnoreCase)
                ? DebriefVerbosity.Brief
                : DebriefVerbosity.Full;
            var text = DebriefTemplate.Build(facts, verbosity);

            // Optional LLM styling behind the number verifier; ANY failure keeps the template.
            var attemptLlm = options.UseLlm && _llm.IsConfigured;
            string? styled = null;
            if (attemptLlm)
            {
                styled = await StyleWithLlmAsync(facts, verbosity, cancellationToken).ConfigureAwait(false);
            }

            // llm = the styled path was attempted; verified = its output passed the number
            // check and is what gets spoken.
            _eventLog.Record("debrief.styled", new { llm = attemptLlm, verified = styled is not null });
            if (styled is not null)
            {
                text = styled;
            }

            // One notable, fact-locked logbook line. Excludes this session so the count is
            // right whether or not the logbook fold has already run. Appended AFTER styling —
            // its numbers are deterministic and must never be paraphrased.
            // Day-mode context ("Leg 2 of 4 complete.") — deterministic, appended after
            // styling like the comparison so LLM output can never rewrite it.
            var dayLine = _dayStore?.Snapshot().DebriefContextLine;
            if (!string.IsNullOrWhiteSpace(dayLine))
            {
                text = text.TrimEnd() + " " + dayLine;
            }

            var comparison = _logbook.DescribeComparison(
                facts, Path.GetFileNameWithoutExtension(sessionPath));
            if (!string.IsNullOrWhiteSpace(comparison))
            {
                text = text.TrimEnd() + " " + comparison;
            }

            Persist(sessionPath, text);

            // Low priority with a validity window: the debrief expires unspoken if a new
            // flight is already underway by the time the queue reaches it. Deliberately NOT
            // the finalizer's token — once composed, the debrief lives or dies by its own
            // TTL/validity, not by finalization ending.
            _ = _arbiter.EnqueueAsync(new SpeechRequest(
                text,
                SpeechPriority.Low,
                Ttl: TimeSpan.FromMinutes(10),
                IsStillValid: () => _flight.CurrentPhase
                    is FlightPhase.Shutdown or FlightPhase.ColdAndDark or FlightPhase.TaxiIn,
                Tag: "debrief"), CancellationToken.None);

            _eventLog.Record("debrief.spoken", new
            {
                chars = text.Length,
                verbosity = verbosity.ToString().ToLowerInvariant(),
                manual,
            });
            _logger.LogInformation("Debrief composed ({Length} chars, {Verbosity}) — enqueued at Low",
                text.Length, verbosity);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Debrief failed");
        }
    }

    /// <summary>The LLM ask → verify → ONE strict re-ask → verify chain. Returns the verified
    /// styled text, or null on any miss (blank reply, unverified numbers twice, HTTP/timeout
    /// failure) — null means "speak the template". Each call inside carries its own timeout
    /// budget (see <see cref="OpenAiChatClient"/>); the predecessor shared one expiring window
    /// across both calls, which starved the re-ask.</summary>
    private async Task<string?> StyleWithLlmAsync(
        DebriefFacts facts, DebriefVerbosity verbosity, CancellationToken cancellationToken)
    {
        try
        {
            // Persona fragment (empty when off) colours tone only; the debrief prompt still
            // locks the operational content and the number verifier backs it up.
            var system = (_persona?.SystemPromptFragment(Persona.PersonaStyleCategory.Debrief) ?? "")
                + DebriefLlm.SystemPrompt(verbosity);
            var factBlock = DebriefLlm.FactBlock(facts, icao => _airportNames?.SpokenName(icao));
            var allowed = DebriefLlm.AllowedNumbers(facts);

            var narrative = await _llm.CompleteAsync(
                system, factBlock + "\n\nWrite the spoken debrief now.", cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(narrative))
            {
                _logger.LogWarning("LLM returned no debrief text — using the template");
                return null;
            }

            narrative = narrative.Trim();
            var check = NumberVerifier.Check(narrative, allowed);
            if (check.Ok)
            {
                return narrative;
            }

            _logger.LogWarning("Debrief number verification failed — unverified: {Tokens}",
                string.Join(", ", check.Offending));

            var retry = await _llm.CompleteAsync(
                system,
                factBlock + "\n\nUse ONLY these numbers, exactly as written, and no others: "
                    + NumberVerifier.DescribeAllowed(allowed)
                    + "\nWrite the spoken debrief now.",
                cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(retry))
            {
                retry = retry.Trim();
                if (NumberVerifier.Check(retry, allowed).Ok)
                {
                    return retry;
                }
            }

            _logger.LogWarning("Debrief re-ask still unverified — using the template");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM debrief styling failed — using the template");
        }

        return null;
    }

    private void Persist(string sessionPath, string text)
    {
        try
        {
            // "session-x.jsonl" → "session-x.debrief.txt", beside the log it summarizes.
            var path = Path.ChangeExtension(sessionPath, ".debrief.txt");
            File.WriteAllText(path, text);
            _logger.LogDebug("Debrief text saved to {Path}", path);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Could not persist debrief text");
        }
    }
}
