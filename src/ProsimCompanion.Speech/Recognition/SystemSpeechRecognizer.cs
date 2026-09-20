using System.Speech.Recognition;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Speech.Recognition;

/// <summary>
/// Offline System.Speech last resort: closed Choices grammar (plus a 1–6-word aviation digit
/// grammar when the number sentinel is present), default audio device, continuous multiple
/// recognition. A machine without a usable engine degrades to a silent no-op rather than
/// throwing (predecessor behavior).
/// </summary>
public sealed class SystemSpeechRecognizer : IVoiceRecognizer
{
    /// <summary>How long a cancel may take to settle before we give up waiting and try to
    /// restart anyway. The engine normally raises RecognizeCompleted within milliseconds.</summary>
    private static readonly TimeSpan CancelSettleTimeout = TimeSpan.FromSeconds(2);

    private readonly IOptionsMonitor<SpeechOptions> _options;
    private readonly ILogger<SystemSpeechRecognizer> _logger;
    private readonly object _gate = new();
    private readonly SpeechRecognitionEngine? _engine;

    // RecognizeAsyncCancel is asynchronous: the engine keeps "doing recognition" until it
    // raises RecognizeCompleted. On the 2026-09-20 EGLL flight a grammar swap called
    // RecognizeAsync straight after the cancel, the engine threw InvalidOperationException,
    // the catch left _listening true and the FO was deaf for four to five minutes three
    // times (each right after a checklist opened its window). The cancel now waits for the
    // completion edge before re-arming.
    private readonly ManualResetEventSlim _recognizeCompleted = new(false);

    private bool _listening;

    public SystemSpeechRecognizer(IOptionsMonitor<SpeechOptions> options, ILogger<SystemSpeechRecognizer> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _logger = logger;

        try
        {
            _engine = new SpeechRecognitionEngine();
            _engine.SetInputToDefaultAudioDevice();
            _engine.SpeechRecognized += OnRecognized;
            _engine.SpeechRecognitionRejected += OnRejected;
            _engine.RecognizeCompleted += (_, _) => _recognizeCompleted.Set();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "System.Speech unavailable — offline recognition disabled");
            _engine = null;
        }
    }

    public event EventHandler<RecognizedEventArgs>? Accepted;

    public event EventHandler<RecognizedEventArgs>? Rejected;

    public void SetGrammar(IReadOnlyList<string> phrases)
    {
        ArgumentNullException.ThrowIfNull(phrases);
        if (_engine is null)
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                var wantNumbers = phrases.Contains(NumberGrammar.Sentinel);
                var distinct = phrases
                    .Where(p => p != NumberGrammar.Sentinel && !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var wasListening = _listening;
                if (wasListening)
                {
                    CancelAndSettleLocked();
                }

                _engine.UnloadAllGrammars();
                if (distinct.Length > 0)
                {
                    var builder = new GrammarBuilder(new Choices(distinct))
                    {
                        Culture = _engine.RecognizerInfo.Culture,
                    };
                    _engine.LoadGrammar(new Grammar(builder) { Name = "companion" });
                }

                if (wantNumbers)
                {
                    var numbers = new GrammarBuilder(
                        new Choices([.. Callouts.Aviation.NumberGrammarWords]), 1, 6)
                    {
                        Culture = _engine.RecognizerInfo.Culture,
                    };
                    _engine.LoadGrammar(new Grammar(numbers) { Name = "numbers" });
                }

                if (wasListening && (distinct.Length > 0 || wantNumbers))
                {
                    _engine.RecognizeAsync(RecognizeMode.Multiple);
                }
                else if (wasListening)
                {
                    // Cancelled above and nothing to re-arm with (an empty free-form grammar
                    // the closed engine cannot serve): the flag must follow reality or
                    // IsListening would lie to the controller's reconcile (issue #61).
                    _listening = false;
                }
            }
            catch (Exception ex)
            {
                // Whatever failed, the engine is NOT capturing after a cancel: the flag must
                // say so, or the controller's reconcile sees "listening" over a dead engine
                // (issue #61's dead latch, re-found on 2026-09-20 via this very path). With
                // the flag false the next Evaluate restarts the engine or enters the retry
                // ladder — a 2 s gap instead of a four-minute one.
                _listening = false;
                _logger.LogWarning(ex, "System.Speech grammar update failed — engine stopped, the controller will restart it");
            }
        }
    }

    /// <summary>Cancels the running recognition and waits for the engine to confirm it
    /// stopped (RecognizeCompleted), so the next RecognizeAsync cannot collide with a cancel
    /// still in flight. Caller holds the gate.</summary>
    private void CancelAndSettleLocked()
    {
        _recognizeCompleted.Reset();
        _engine!.RecognizeAsyncCancel();
        if (!_recognizeCompleted.Wait(CancelSettleTimeout))
        {
            _logger.LogDebug("System.Speech cancel did not settle within {Timeout} ms", CancelSettleTimeout.TotalMilliseconds);
        }
    }

    /// <summary>Reality, not intent: false until RecognizeAsync actually succeeded (a
    /// no-engine machine is permanently false) — the controller's reconcile reads this.</summary>
    public bool IsListening
    {
        get
        {
            lock (_gate)
            {
                return _listening;
            }
        }
    }

    public bool StartListening()
    {
        if (_engine is null)
        {
            return false;
        }

        lock (_gate)
        {
            if (_listening)
            {
                return true;
            }

            try
            {
                _engine.RecognizeAsync(RecognizeMode.Multiple);
                _listening = true;
            }
            catch (Exception ex)
            {
                // Debug on purpose — the controller owns the once-per-episode Warning and
                // the retry backoff (issue #61), so this must not spam per attempt.
                _logger.LogDebug(ex, "System.Speech start failed (no grammar yet?)");
            }

            return _listening;
        }
    }

    public void StopListening()
    {
        if (_engine is null)
        {
            return;
        }

        lock (_gate)
        {
            if (!_listening)
            {
                return;
            }

            CancelAndSettleLocked();
            _listening = false;
        }
    }

    public void Dispose()
    {
        StopListening();
        _engine?.Dispose();
        _recognizeCompleted.Dispose();
    }

    private void OnRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        var threshold = _options.CurrentValue.RecognitionConfidenceThreshold;
        var args = new RecognizedEventArgs(e.Result.Text, e.Result.Confidence, null, null);
        if (e.Result.Confidence >= threshold)
        {
            Accepted?.Invoke(this, args);
        }
        else
        {
            Rejected?.Invoke(this, args);
        }
    }

    private void OnRejected(object? sender, SpeechRecognitionRejectedEventArgs e)
        => Rejected?.Invoke(this, new RecognizedEventArgs(
            e.Result?.Text ?? "", e.Result?.Confidence ?? 0, null, null));
}
