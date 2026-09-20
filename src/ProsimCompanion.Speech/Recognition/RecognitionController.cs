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

    /// <summary>Start-up readiness: how long the LAN engine may take to answer before the
    /// offline engine covers, and how often it is asked meanwhile.</summary>
    private static readonly TimeSpan DefaultReadinessBudget = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan DefaultReadinessCadence = TimeSpan.FromSeconds(3);

    /// <summary>After a fallback the LAN engine is re-probed at this cadence for the rest of
    /// the session (2026-09-20: the voice box finished a macOS update minutes after the app
    /// gave up, and the whole flight ran on the offline engine).</summary>
    private static readonly TimeSpan DefaultReprobeInterval = TimeSpan.FromSeconds(30);

    private readonly IOptionsMonitor<SpeechOptions> _options;
    private readonly PushToTalkService _ptt;
    private readonly SpeechStatusStore _store;
    private readonly ConnectionStatusStore? _connections;
    private readonly ILogger<RecognitionController> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IReadOnlyList<TimeSpan> _retryBackoff;
    private readonly Core.EventLog.JsonlEventLog? _eventLog;
    private readonly RecognitionEngineSeams _seams;
    private readonly IDisposable? _optionsSubscription;
    private readonly object _gate = new();

    private IVoiceRecognizer _recognizer;
    private IReadOnlyList<string> _grammar = [];
    private bool _windowOpen;
    private bool _paused;
    private bool _desiredListening;
    private bool _reportedListening; // last state logged/pushed to the store — transition edge detector
    private bool _onLan;
    private CancellationTokenSource? _retry;
    private int _retryAttempt;
    private CancellationTokenSource? _reprobe;

    /// <param name="recognizerFactory">Test seam: overrides the engine chain so the listen
    /// decision is testable without a Windows speech engine; DI leaves it null.</param>
    /// <param name="retryBackoff">Test seam: start-retry delays (last entry repeats). DI
    /// leaves it null for the production 2/5/10/30 s ladder.</param>
    /// <param name="eventLog">Session JSONL, forwarded to the LAN recognizer for per-utterance
    /// VAD diagnostics; optional so tests need no sessions directory.</param>
    /// <param name="connections">Footer/status dot for the LAN engine (<see cref="Subsystems.Asr"/>);
    /// optional so tests need no store.</param>
    /// <param name="seams">Test seams for the LAN/offline chain (health probe, engine
    /// factories, timings); DI leaves it null.</param>
    public RecognitionController(
        IOptionsMonitor<SpeechOptions> options,
        PushToTalkService ptt,
        SpeechStatusStore store,
        ILoggerFactory loggerFactory,
        Func<IVoiceRecognizer>? recognizerFactory = null,
        IReadOnlyList<TimeSpan>? retryBackoff = null,
        Core.EventLog.JsonlEventLog? eventLog = null,
        ConnectionStatusStore? connections = null,
        RecognitionEngineSeams? seams = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(ptt);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _options = options;
        _ptt = ptt;
        _store = store;
        _connections = connections;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<RecognitionController>();
        _retryBackoff = retryBackoff is { Count: > 0 } ? retryBackoff : DefaultRetryBackoff;
        _eventLog = eventLog;
        _seams = seams ?? new RecognitionEngineSeams();

        if (recognizerFactory is not null)
        {
            _recognizer = recognizerFactory();
            _onLan = false;
        }
        else if (LanConfigured)
        {
            _recognizer = BuildLanRecognizer();
            _onLan = true;
        }
        else
        {
            _recognizer = BuildOfflineRecognizer();
            _onLan = false;
        }

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

        if (_onLan)
        {
            PublishEngine(ConnectionState.Connecting, $"waiting for the LAN engine at {AsrUrl}");
            _ = ProbeReadinessAsync();
        }
        else
        {
            PublishEngine(
                LanConfigured ? ConnectionState.Disconnected : ConnectionState.Disabled,
                LanConfigured ? "offline engine" : "no LAN engine configured — offline engine only");
        }
    }

    /// <summary>Raised per recognized utterance (engine threads).</summary>
    public event EventHandler<RecognizedEventArgs>? Accepted;

    /// <summary>Raised per unusable utterance (engine threads).</summary>
    public event EventHandler<RecognizedEventArgs>? Rejected;

    public string EngineName => _onLan ? "whisper" : "systemSpeech";

    /// <summary>True while the LAN engine is the active recognizer (reality, not
    /// configuration: false after a fallback until the swap back).</summary>
    public bool OnLanEngine
    {
        get
        {
            lock (_gate)
            {
                return _onLan;
            }
        }
    }

    private bool LanConfigured => !string.IsNullOrWhiteSpace(_options.CurrentValue.AsrBaseUrl);

    private string AsrUrl => _options.CurrentValue.AsrBaseUrl.TrimEnd('/');

    /// <summary>Probes the LAN engine once, now, and swaps back to it if it answers — the
    /// Status section's "Reconnect voice services" button. One readable sentence.</summary>
    public async Task<string> ReprobeNowAsync(CancellationToken cancellationToken)
    {
        if (!LanConfigured)
        {
            return "Speech recognition: no LAN engine configured — the offline engine is the only one.";
        }

        var ready = await ProbeHealthAsync(cancellationToken).ConfigureAwait(false);
        if (OnLanEngine)
        {
            return ready
                ? $"Speech recognition: whisper answers at {AsrUrl}."
                : $"Speech recognition: whisper is the active engine but {AsrUrl}/health did not answer — the next utterance will show whether it still works.";
        }

        if (!ready)
        {
            return $"Speech recognition: whisper still not reachable at {AsrUrl} — the offline engine keeps covering; the app retries every {(int)ReprobeInterval.TotalSeconds} s.";
        }

        SwapToLan("reconnect requested — LAN engine answered");
        return $"Speech recognition: whisper is back at {AsrUrl} — swapped from the offline engine.";
    }

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
            CancelReprobeLocked();
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

    private IVoiceRecognizer BuildLanRecognizer()
        => _seams.LanFactory?.Invoke()
           ?? new LanAsrRecognizer(_options, _loggerFactory.CreateLogger<LanAsrRecognizer>(), _eventLog);

    private IVoiceRecognizer BuildOfflineRecognizer()
        => _seams.OfflineFactory?.Invoke()
           ?? new SystemSpeechRecognizer(_options, _loggerFactory.CreateLogger<SystemSpeechRecognizer>());

    private TimeSpan ReprobeInterval => _seams.ReprobeInterval ?? DefaultReprobeInterval;

    /// <summary>One health check of the LAN engine: true when the configured API shape says
    /// ready. Never throws — an unreachable box is "not ready".</summary>
    private async Task<bool> ProbeHealthAsync(CancellationToken cancellationToken)
    {
        if (_seams.HealthProbe is { } probe)
        {
            return await probe(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await http.GetAsync(AsrUrl + "/health", cancellationToken).ConfigureAwait(false);
            return AsrServerApi.IsReady(_options.CurrentValue.AsrApi, (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Polls the LAN ASR health endpoint (3 s cadence, 120 s budget); if it never
    /// comes up, swaps to the offline engine, replaying grammar and listen state — and keeps
    /// re-probing so the swap is no longer one-way.</summary>
    private async Task ProbeReadinessAsync()
    {
        var deadline = DateTimeOffset.UtcNow + (_seams.ReadinessBudget ?? DefaultReadinessBudget);
        var cadence = _seams.ReadinessCadence ?? DefaultReadinessCadence;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await ProbeHealthAsync(CancellationToken.None).ConfigureAwait(false))
            {
                _logger.LogInformation("LAN ASR ready ({Api} at {Url})", _options.CurrentValue.AsrApi, AsrUrl);
                PublishEngine(ConnectionState.Connected, $"whisper at {AsrUrl}");
                return;
            }

            await Task.Delay(cadence).ConfigureAwait(false);
        }

        SwapToOffline($"LAN ASR not ready within {(int)(_seams.ReadinessBudget ?? DefaultReadinessBudget).TotalSeconds} s");
    }

    private void SwapToOffline(string reason)
    {
        bool actual;
        lock (_gate)
        {
            if (!_onLan)
            {
                return;
            }

            _onLan = false;
            _logger.LogWarning("Falling back to offline recognition: {Reason}", reason);
            ReplaceEngineLocked(BuildOfflineRecognizer());
            ScheduleReprobeLocked();
            actual = _recognizer.IsListening;
        }

        PublishEngine(
            ConnectionState.Disconnected,
            $"offline engine covering — {reason}; retrying {AsrUrl} every {(int)ReprobeInterval.TotalSeconds} s");
        PublishListeningState(actual);
    }

    private void SwapToLan(string reason)
    {
        bool actual;
        lock (_gate)
        {
            if (_onLan)
            {
                return;
            }

            _onLan = true;
            CancelReprobeLocked();
            _logger.LogInformation("LAN ASR recovered — swapping back to whisper: {Reason}", reason);
            ReplaceEngineLocked(BuildLanRecognizer());
            actual = _recognizer.IsListening;
        }

        PublishEngine(ConnectionState.Connected, $"whisper at {AsrUrl} (recovered)");
        PublishListeningState(actual);
    }

    /// <summary>Swaps the active engine, replaying grammar and listen state onto the new one.
    /// Caller holds the gate.</summary>
    private void ReplaceEngineLocked(IVoiceRecognizer replacement)
    {
        var old = _recognizer;
        old.Accepted -= OnAccepted;
        old.Rejected -= OnRejected;

        _recognizer = replacement;
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
    }

    // ---- LAN re-probe after a fallback ----

    /// <summary>Starts the background re-probe episode; no-op while one runs. Caller holds
    /// the gate.</summary>
    private void ScheduleReprobeLocked()
    {
        if (_reprobe is not null)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _reprobe = cts;
        _ = Task.Run(() => ReprobeLoopAsync(cts));
    }

    private void CancelReprobeLocked()
    {
        if (_reprobe is null)
        {
            return;
        }

        _reprobe.Cancel();
        _reprobe = null;
    }

    private async Task ReprobeLoopAsync(CancellationTokenSource cts)
    {
        try
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(ReprobeInterval, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (await ProbeHealthAsync(cts.Token).ConfigureAwait(false))
                {
                    lock (_gate)
                    {
                        if (!ReferenceEquals(_reprobe, cts))
                        {
                            return; // superseded — a newer episode (or a manual reconnect) owns the swap
                        }
                    }

                    SwapToLan("health check answered after the fallback");
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "LAN ASR re-probe loop stopped unexpectedly");
        }
    }

    /// <summary>Publishes the engine identity for the Status section and the footer dot —
    /// engine reality, the same way the listening flag is published.</summary>
    private void PublishEngine(ConnectionState state, string detail)
    {
        _connections?.Set(Subsystems.Asr, state);
        _store.Update(s => s with { RecognizerEngine = EngineName, RecognizerDetail = detail });
    }

    private void OnAccepted(object? sender, RecognizedEventArgs e)
    {
        _store.Update(s => s with { LastHeard = e.Text });
        Accepted?.Invoke(this, e);
    }

    private void OnRejected(object? sender, RecognizedEventArgs e) => Rejected?.Invoke(this, e);
}
