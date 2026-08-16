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
    private readonly IOptionsMonitor<SpeechOptions> _options;
    private readonly ILogger<SystemSpeechRecognizer> _logger;
    private readonly object _gate = new();
    private readonly SpeechRecognitionEngine? _engine;

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
                    _engine.RecognizeAsyncCancel();
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
                _logger.LogDebug(ex, "System.Speech grammar update failed");
            }
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

            _engine.RecognizeAsyncCancel();
            _listening = false;
        }
    }

    public void Dispose()
    {
        StopListening();
        _engine?.Dispose();
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
