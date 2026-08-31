using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Recognition;

/// <summary>
/// Owns the active recognizer and the listen decision:
/// <c>shouldListen = windowOpen &amp;&amp; !paused &amp;&amp; !atcMuted &amp;&amp; (continuous || pttPressed)</c>.
/// <c>paused</c> is the pilot's "ear off" latch (<see cref="IVoiceListeningControl"/>, Stream
/// Deck / web toggle for talking to real people): a latched ATC mute, deliberately silent —
/// no FO acknowledgement, since the pilot is about to talk to someone else.
/// The recognizer chain is LAN faster-whisper (when configured, with a background readiness
/// probe and a one-way swap to the offline engine if it never comes up) → System.Speech.
/// Consumers open/close grammar windows and subscribe to the Recognized events; engine
/// identity is stable across the swap.
///
/// Reconciliation is against ENGINE reality, never a latched intent (issue #61): the old
/// short-circuit compared the desired state to its own previous decision, so one swallowed
/// start failure (mic busy at app boot) left continuous listening dead for an entire flight
/// until a PTT toggle forced a state change through the latch. A failed start now retries
/// with backoff (2 s, 5 s, 10 s, then every 30 s) until it sticks or the desired state
/// changes, so a busy mic self-heals when the device frees up.
/// </summary>
public sealed class RecognitionController : IRecognitionWindow, IVoiceListeningControl, IDisposable
{
    private static readonly IReadOnlyList<TimeSpan> DefaultRetryBackoff =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30), // last entry repeats indefinitely
    ];

    private readonly IOptionsMonitor<SpeechOptions> _options;
    private readonly PushToTalkService _ptt;
    private readonly SpeechStatusStore _store;
    private readonly ILogger<RecognitionController> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IReadOnlyList<TimeSpan> _retryBackoff;
    private readonly Core.EventLog.JsonlEventLog? _eventLog;
    private readonly IDisposable? _optionsSubscription;
    private readonly object _gate = new();

    private IVoiceRecognizer _recognizer;
    private IReadOnlyList<string> _grammar = [];
    private bool _windowOpen;
    private bool _paused;
    private bool _desiredListening;
    private bool _reportedListening; // last state logged/pushed to the store — transition edge detector
    private bool _swapped;
    private CancellationTokenSource? _retry;
    private int _retryAttempt;

    /// <param name="recognizerFactory">Test seam: overrides the engine chain so the listen
    /// decision is testable without a Windows speech engine; DI leaves it null.</param>
    /// <param name="retryBackoff">Test seam: start-retry delays (last entry repeats). DI
    /// leaves it null for the production 2/5/10/30 s ladder.</param>
    /// <param name="eventLog">Session JSONL, forwarded to the LAN recognizer for per-utterance
    /// VAD diagnostics; optional so tests need no sessions directory.</param>
    public RecognitionController(
        IOptionsMonitor<SpeechOptions> options,
        PushToTalkService ptt,
        SpeechStatusStore store,
        ILoggerFactory loggerFactory,
        Func<IVoiceRecognizer>? recognizerFactory = null,
        IReadOnlyList<TimeSpan>? retryBackoff = null,
        Core.EventLog.JsonlEventLog? eventLog = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(ptt);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _options = options;
        _ptt = ptt;
        _store = store;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<RecognitionController>();
        _retryBackoff = retryBackoff is { Count: > 0 } ? retryBackoff : DefaultRetryBackoff;
        _eventLog = eventLog;

        _recognizer = recognizerFactory?.Invoke() ?? BuildRecognizer();
        _recognizer.Accepted += OnAccepted;
        _recognizer.Rejected += OnRejected;
        // PTT edges logged at Info (issue #61): the flight log had ONE recognition line all
        // flight — transition evidence is what makes the next silent failure diagnosable.
        _ptt.OwnPttChanged += pressed =>
        {
            _logger.LogInformation("FO PTT {Edge}", pressed ? "down" : "up");
            Evaluate();
        };
        _ptt.AtcPttChanged += pressed =>
        {
            _logger.LogInformation("ATC PTT {Edge}", pressed ? "down" : "up");
            Evaluate();
        };

        // Settings hot-reload: a listening-mode flip (pushToTalk ↔ continuous) must take
        // effect immediately — the idle window stays open for hours, so without this the
        // new mode waited for the next window or PTT edge (i.e. an app restart in practice).
        // Evaluate() reconciles against engine state, so duplicate reload notifications are
        // harmless — and double as desync repair opportunities.
        _optionsSubscription = _options.OnChange(_ => Evaluate());

        if (_recognizer is LanAsrRecognizer)
        {
            _ = ProbeReadinessAsync();
        }
    }

    /// <summary>Raised per recognized utterance (engine threads).</summary>
    public event EventHandler<RecognizedEventArgs>? Accepted;

    /// <summary>Raised per unusable utterance (engine threads).</summary>
    public event EventHandler<RecognizedEventArgs>? Rejected;

    public string EngineName => _recognizer is LanAsrRecognizer ? "whisper" : "systemSpeech";

    public bool WindowOpen
    {
        get
        {
            lock (_gate)
            {
                return _windowOpen;
            }
        }
    }

    public IReadOnlyList<string> CurrentGrammar
    {
        get
        {
            lock (_gate)
            {
                return _grammar;
            }
        }
    }

    // ---- IVoiceListeningControl (pilot "ear off" latch) ----

    public bool Paused
    {
        get
        {
            lock (_gate)
            {
                return _paused;
            }
        }
    }

    /// <inheritdoc/>
    /// <remarks>Logged at Info on every edge (same reasoning as the PTT edges, issue #61): a
    /// paused mic looks exactly like a dead engine in the flight log otherwise.</remarks>
    public bool SetPaused(bool paused)
    {
        lock (_gate)
        {
            if (_paused == paused)
            {
                return false;
            }

            _paused = paused;
        }

        _logger.LogInformation("Voice recognition {PauseEdge} by the pilot", paused ? "paused" : "resumed");
        Evaluate();
        return true;
    }

    public void OpenListeningWindow(IReadOnlyList<string> grammar)
    {
        ArgumentNullException.ThrowIfNull(grammar);

        lock (_gate)
        {
            _grammar = grammar;
            _recognizer.SetGrammar(grammar);
            _windowOpen = true;
        }

        _logger.LogDebug("Listening window opened ({PhraseCount} phrases)", grammar.Count);
        Evaluate();
    }

    public void CloseListeningWindow()
    {
        lock (_gate)
        {
            _windowOpen = false;
        }

        _logger.LogDebug("Listening window closed");
        Evaluate();
    }

    public void Dispose()
    {
        _optionsSubscription?.Dispose();
        lock (_gate)
        {
            CancelRetryLocked();
        }

        _recognizer.Dispose();
    }

    /// <summary>Recomputes the desired state and reconciles the ENGINE against it. Called on
    /// every edge (window, PTT, options reload) — never periodically, so all logging here is
    /// one line per transition.</summary>
    private void Evaluate()
    {
        bool shouldListen;
        bool actual;
        lock (_gate)
        {
            var continuous = _options.CurrentValue.RecognitionMode
                .Equals("continuous", StringComparison.OrdinalIgnoreCase);
            shouldListen = _windowOpen && !_paused && !_ptt.AtcPttPressed && (continuous || _ptt.OwnPttPressed);
            _desiredListening = shouldListen;

            if (shouldListen)
            {
                if (_recognizer.IsListening || _recognizer.StartListening())
                {
                    CancelRetryLocked();
                }
                else
                {
                    ScheduleRetryLocked();
                }
            }
            else
            {
                CancelRetryLocked();
                if (_recognizer.IsListening)
                {
                    _recognizer.StopListening();
                }
            }

            actual = _recognizer.IsListening;
        }

        PublishListeningState(actual);
    }

    /// <summary>Pushes the ACTUAL engine state to the store and logs the transition once —
    /// the store's Listening flag used to be latched intent, which is how the UI showed
    /// "listening" over a dead engine.</summary>
    private void PublishListeningState(bool actual)
    {
        bool transition;
        bool paused;
        lock (_gate)
        {
            transition = actual != _reportedListening;
            _reportedListening = actual;
            paused = _paused;
        }

        if (transition)
        {
            _logger.LogInformation(
                "Recognition {ListenState} ({Engine})", actual ? "listening" : "stopped", EngineName);
        }

        _store.Update(s => s with { Listening = actual, ListeningPaused = paused });
    }

    // ---- Start-failure retry (issue #61) ----

    /// <summary>Kicks off one retry episode; no-op while one is already running. Caller holds
    /// the gate.</summary>
    private void ScheduleRetryLocked()
    {
        if (_retry is not null)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _retry = cts;
        _retryAttempt = 0;
        _logger.LogWarning(
            "Speech recognition failed to start ({Engine}) — retrying with backoff until the device frees up",
            EngineName);
        _ = Task.Run(() => RetryLoopAsync(cts));
    }

    /// <summary>Ends the current retry episode, if any. Caller holds the gate. Deliberately
    /// no Dispose: the loop may still be inside Task.Delay on this token, and a CTS without
    /// a timer holds nothing worth racing a dispose for.</summary>
    private void CancelRetryLocked()
    {
        if (_retry is null)
        {
            return;
        }

        _retry.Cancel();
        _retry = null;
    }

    private async Task RetryLoopAsync(CancellationTokenSource cts)
    {
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var delay = _retryBackoff[Math.Min(_retryAttempt, _retryBackoff.Count - 1)];
                _retryAttempt++;
                try
                {
                    await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                bool recovered;
                lock (_gate)
                {
                    if (!ReferenceEquals(_retry, cts))
                    {
                        return; // superseded — a newer episode owns the retries
                    }

                    if (!_desiredListening)
                    {
                        CancelRetryLocked();
                        return; // desire changed while we were waiting — nothing to repair
                    }

                    recovered = _recognizer.IsListening || _recognizer.StartListening();
                    if (recovered)
                    {
                        CancelRetryLocked();
                    }
                }

                if (recovered)
                {
                    _logger.LogInformation(
                        "Speech recognition recovered on retry {Attempt} ({Engine})",
                        _retryAttempt, EngineName);
                    PublishListeningState(true);
                    return;
                }

                _logger.LogDebug(
                    "Recognition start retry {Attempt} failed ({Engine})", _retryAttempt, EngineName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Recognition start retry loop stopped unexpectedly");
        }
    }

    private IVoiceRecognizer BuildRecognizer()
    {
        if (!string.IsNullOrWhiteSpace(_options.CurrentValue.AsrBaseUrl))
        {
            return new LanAsrRecognizer(_options, _loggerFactory.CreateLogger<LanAsrRecognizer>(), _eventLog);
        }

        return new SystemSpeechRecognizer(_options, _loggerFactory.CreateLogger<SystemSpeechRecognizer>());
    }

    /// <summary>Polls the LAN ASR health endpoint (3 s cadence, 120 s budget); if it never
    /// comes up, swaps one-way to the offline engine, replaying grammar and listen state.</summary>
    private async Task ProbeReadinessAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var url = _options.CurrentValue.AsrBaseUrl.TrimEnd('/') + "/health";
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(120);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await http.GetAsync(url).ConfigureAwait(false);
                var api = _options.CurrentValue.AsrApi;
                if (AsrServerApi.IsReady(api, (int)response.StatusCode))
                {
                    _logger.LogInformation("LAN ASR ready ({Api} at {Url})", api, url);
                    return;
                }
            }
            catch
            {
                // Keep polling — the box may still be booting.
            }

            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }

        SwapToOffline("LAN ASR not ready within 120 s");
    }

    private void SwapToOffline(string reason)
    {
        bool actual;
        lock (_gate)
        {
            if (_swapped)
            {
                return;
            }

            _swapped = true;
            _logger.LogWarning("Falling back to offline recognition: {Reason}", reason);

            var old = _recognizer;
            old.Accepted -= OnAccepted;
            old.Rejected -= OnRejected;

            _recognizer = new SystemSpeechRecognizer(
                _options, _loggerFactory.CreateLogger<SystemSpeechRecognizer>());
            _recognizer.Accepted += OnAccepted;
            _recognizer.Rejected += OnRejected;
            _recognizer.SetGrammar(_grammar);
            if (_desiredListening && !_recognizer.StartListening())
            {
                // The replacement engine could not start either — same retry path as any
                // other failed start, so the swap can't reintroduce the dead-latch bug.
                ScheduleRetryLocked();
            }

            old.Dispose();
            actual = _recognizer.IsListening;
        }

        PublishListeningState(actual);
    }

    private void OnAccepted(object? sender, RecognizedEventArgs e)
    {
        _store.Update(s => s with { LastHeard = e.Text });
        Accepted?.Invoke(this, e);
    }

    private void OnRejected(object? sender, RecognizedEventArgs e) => Rejected?.Invoke(this, e);
}
