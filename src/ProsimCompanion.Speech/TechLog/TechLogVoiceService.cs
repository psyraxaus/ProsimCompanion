using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.TechLog;
using ProsimCompanion.Speech.Abnormals;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.TechLog;

/// <summary>
/// The tech log's voice: the once-per-flight preflight brief (only when open items exist —
/// a clean log never speaks unsolicited), the on-command brief, and the random-wear
/// announcement. The store and lifecycle live in Core (<see cref="ITechLogService"/>) so the
/// web page shares them without Web referencing Speech. The guided raise/rectify voice
/// dialogues are deferred with the free-form capture seam; per-flight abnormals are already
/// remembered here (<see cref="FiredAbnormals"/>) so the deferred post-abnormal offer can
/// build on this slice without re-deriving them.
/// </summary>
public sealed class TechLogVoiceService : IVoiceFeature, IDisposable
{
    private static readonly string[] BriefPhrases =
    [
        "tech log", "read the tech log", "tech log brief", "brief the tech log",
        "any open items", "open items",
    ];

    private readonly ITechLogService _techLog;
    private readonly ISpeechArbiter _arbiter;
    private readonly IFlightPhaseSource _flight;
    private readonly FailureMonitor _failures;
    private readonly IOptionsMonitor<TechLogOptions> _options;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<TechLogVoiceService> _logger;
    private readonly object _gate = new();

    private readonly Dictionary<string, string> _flightAbnormals = new(StringComparer.OrdinalIgnoreCase);
    private bool _briefedThisFlight;
    private bool _started;

    public TechLogVoiceService(
        ITechLogService techLog,
        ISpeechArbiter arbiter,
        IFlightPhaseSource flight,
        FailureMonitor failures,
        IOptionsMonitor<TechLogOptions> options,
        JsonlEventLog eventLog,
        ILogger<TechLogVoiceService> logger)
    {
        ArgumentNullException.ThrowIfNull(techLog);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(failures);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _techLog = techLog;
        _arbiter = arbiter;
        _flight = flight;
        _failures = failures;
        _options = options;
        _eventLog = eventLog;
        _logger = logger;
    }

    /// <summary>Abnormals fired this flight (id → title), for the deferred post-abnormal
    /// shutdown offer. Cleared when a new flight starts (ColdAndDark/Preflight).</summary>
    public IReadOnlyDictionary<string, string> FiredAbnormals
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, string>(_flightAbnormals, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public IEnumerable<string> Phrases => BriefPhrases;

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
        _failures.FailureDetected += NoteAbnormal;
        _techLog.WearRaised += OnWearRaised;
        _logger.LogInformation("Tech log voice started");
    }

    public void Dispose()
    {
        _flight.PhaseChanged -= OnPhaseChanged;
        _failures.FailureDetected -= NoteAbnormal;
        _techLog.WearRaised -= OnWearRaised;
    }

    public bool TryHandle(string utterance)
    {
        if (string.IsNullOrWhiteSpace(utterance))
        {
            return false;
        }

        var text = utterance.Trim();
        if (!BriefPhrases.Any(p => string.Equals(p, text, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        DoBrief(onCommand: true);
        return true;
    }

    /// <summary>Remembers an abnormal for this flight. Normally fed by
    /// <see cref="FailureMonitor.FailureDetected"/>; public so the deferred post-abnormal
    /// offer (and tests) can inject without a live monitor.</summary>
    public void NoteAbnormal(string id, string title)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        lock (_gate)
        {
            _flightAbnormals[id] = title;
        }
    }

    private void OnWearRaised(TechLogDefect defect)
        => _ = _arbiter.EnqueueAsync(new SpeechRequest(
            $"Noticed {defect.Title}. I'll pop it in the tech log.",
            SpeechPriority.Low,
            Ttl: TimeSpan.FromMinutes(2),
            Tag: "techlog"));

    private void OnPhaseChanged(object? sender, FlightPhaseChangedEventArgs e)
    {
        // Both edges reset the brief latch: Shutdown ends this flight, ColdAndDark covers a
        // full repower where Shutdown was never committed.
        if (e.Current is FlightPhase.ColdAndDark or FlightPhase.Shutdown)
        {
            lock (_gate)
            {
                _briefedThisFlight = false;
            }
        }

        if (e.Current is FlightPhase.ColdAndDark or FlightPhase.Preflight)
        {
            lock (_gate)
            {
                _flightAbnormals.Clear();
            }
        }

        if (e.Current == FlightPhase.Preflight)
        {
            MaybeAutoBrief();
        }
    }

    private void MaybeAutoBrief()
    {
        if (!_options.CurrentValue.Enabled)
        {
            return;
        }

        bool brief;
        lock (_gate)
        {
            // Unsolicited speech only when there is something to say.
            brief = !_briefedThisFlight && _techLog.OpenDefects.Count > 0;
            if (brief)
            {
                _briefedThisFlight = true;
            }
        }

        if (brief)
        {
            DoBrief(onCommand: false);
        }
    }

    private void DoBrief(bool onCommand)
    {
        if (!_options.CurrentValue.Enabled)
        {
            if (onCommand)
            {
                _ = _arbiter.SpeakAsync("The tech log is switched off.");
            }

            return;
        }

        var open = _techLog.OpenDefects;
        if (open.Count == 0)
        {
            if (onCommand)
            {
                _ = _arbiter.SpeakAsync("Tech log is clean.");
            }

            return;
        }

        // Facts are read verbatim (title, MEL ref, implications, days) — never restyled.
        _ = _arbiter.EnqueueAsync(new SpeechRequest(
            _techLog.BuildBrief(open),
            SpeechPriority.Normal,
            Ttl: TimeSpan.FromMinutes(3),
            Tag: "techlog"));
        _eventLog.Record("techlog.briefed", new { open = open.Count, onCommand });
    }
}
