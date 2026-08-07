using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Debrief;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Logbook;
using ProsimCompanion.Core.Sessions;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Debrief;

/// <summary>
/// The post-flight debrief: extracts the session's facts, builds the deterministic template
/// (LLM styling deferred), appends the logbook comparison line, persists the text beside the
/// session log, and speaks it at Low priority so any late safety speech outranks it. Runs as
/// the FIRST session-finalization step (order 10) — it reads the session log before the
/// logbook/tech-log folds mutate their stores — plus on voice command or the web button.
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
    private readonly ILogger<DebriefService> _logger;
    private readonly object _gate = new();

    private bool _started;
    private bool _doneThisFlight;

    public DebriefService(
        IDebriefFactExtractor extractor,
        ILogbookService logbook,
        ISpeechArbiter arbiter,
        IFlightPhaseSource flight,
        JsonlEventLog eventLog,
        IOptionsMonitor<DebriefOptions> options,
        ILogger<DebriefService> logger)
    {
        ArgumentNullException.ThrowIfNull(extractor);
        ArgumentNullException.ThrowIfNull(logbook);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _extractor = extractor;
        _logbook = logbook;
        _arbiter = arbiter;
        _flight = flight;
        _eventLog = eventLog;
        _options = options;
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
    /// what has happened so far.</summary>
    public void TriggerNow() => Run(manual: true);

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

        Run(manual: false);
        return Task.CompletedTask;
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

    /// <summary>Synchronous on purpose: extraction is a local file read and the speech enqueue
    /// is fire-and-forget, so there is nothing to await — callers (finalizer thread, voice
    /// dispatch, web button) all tolerate the file-read latency.</summary>
    private void Run(bool manual)
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

            // One notable, fact-locked logbook line. Excludes this session so the count is
            // right whether or not the logbook fold has already run.
            var comparison = _logbook.DescribeComparison(
                facts, Path.GetFileNameWithoutExtension(sessionPath));
            if (!string.IsNullOrWhiteSpace(comparison))
            {
                text = text.TrimEnd() + " " + comparison;
            }

            Persist(sessionPath, text);

            // Low priority with a validity window: the debrief expires unspoken if a new
            // flight is already underway by the time the queue reaches it.
            _ = _arbiter.EnqueueAsync(new SpeechRequest(
                text,
                SpeechPriority.Low,
                Ttl: TimeSpan.FromMinutes(10),
                IsStillValid: () => _flight.CurrentPhase
                    is FlightPhase.Shutdown or FlightPhase.ColdAndDark or FlightPhase.TaxiIn,
                Tag: "debrief"));

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
