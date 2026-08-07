using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Recognition;

/// <summary>
/// Owns the active recognizer and the listen decision:
/// <c>shouldListen = windowOpen &amp;&amp; !atcMuted &amp;&amp; (continuous || pttPressed)</c>.
/// The recognizer chain is LAN faster-whisper (when configured, with a background readiness
/// probe and a one-way swap to the offline engine if it never comes up) → System.Speech.
/// Consumers open/close grammar windows and subscribe to the Recognized events; engine
/// identity is stable across the swap.
/// </summary>
public sealed class RecognitionController : IRecognitionWindow, IDisposable
{
    private readonly IOptionsMonitor<SpeechOptions> _options;
    private readonly PushToTalkService _ptt;
    private readonly SpeechStatusStore _store;
    private readonly ILogger<RecognitionController> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly object _gate = new();

    private IVoiceRecognizer _recognizer;
    private IReadOnlyList<string> _grammar = [];
    private bool _windowOpen;
    private bool _listening;
    private bool _swapped;

    public RecognitionController(
        IOptionsMonitor<SpeechOptions> options,
        PushToTalkService ptt,
        SpeechStatusStore store,
        ILoggerFactory loggerFactory)
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

        _recognizer = BuildRecognizer();
        _recognizer.Accepted += OnAccepted;
        _recognizer.Rejected += OnRejected;
        _ptt.OwnPttChanged += _ => Evaluate();
        _ptt.AtcPttChanged += _ => Evaluate();

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

    public void OpenListeningWindow(IReadOnlyList<string> grammar)
    {
        ArgumentNullException.ThrowIfNull(grammar);

        lock (_gate)
        {
            _grammar = grammar;
            _recognizer.SetGrammar(grammar);
            _windowOpen = true;
        }

        Evaluate();
    }

    public void CloseListeningWindow()
    {
        lock (_gate)
        {
            _windowOpen = false;
        }

        Evaluate();
    }

    public void Dispose() => _recognizer.Dispose();

    private void Evaluate()
    {
        bool shouldListen;
        lock (_gate)
        {
            var continuous = _options.CurrentValue.RecognitionMode
                .Equals("continuous", StringComparison.OrdinalIgnoreCase);
            shouldListen = _windowOpen && !_ptt.AtcPttPressed && (continuous || _ptt.OwnPttPressed);
            if (shouldListen == _listening)
            {
                return;
            }

            _listening = shouldListen;
            if (shouldListen)
            {
                _recognizer.StartListening();
            }
            else
            {
                _recognizer.StopListening();
            }
        }

        _store.Update(s => s with { Listening = shouldListen });
    }

    private IVoiceRecognizer BuildRecognizer()
    {
        if (!string.IsNullOrWhiteSpace(_options.CurrentValue.AsrBaseUrl))
        {
            return new LanAsrRecognizer(_options, _loggerFactory.CreateLogger<LanAsrRecognizer>());
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
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("LAN ASR ready");
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
            if (_listening)
            {
                _recognizer.StartListening();
            }

            old.Dispose();
        }
    }

    private void OnAccepted(object? sender, RecognizedEventArgs e)
    {
        _store.Update(s => s with { LastHeard = e.Text });
        Accepted?.Invoke(this, e);
    }

    private void OnRejected(object? sender, RecognizedEventArgs e) => Rejected?.Invoke(this, e);
}
