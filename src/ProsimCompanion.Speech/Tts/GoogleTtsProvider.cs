using Google.Cloud.TextToSpeech.V1;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Speech.Tts;

/// <summary>
/// Google Cloud TTS (Chirp 3 HD) via a service-account JSON key. Chirp 3 HD ignores SSML and
/// rate/pitch, so plain text only is sent (volume is applied client-side in playback).
/// LINEAR16 at 24 kHz — Google returns those with a WAV header, so text is a sufficient cache
/// key. A failed client build latches until settings change (no point re-reading a missing key
/// file per utterance); the monthly character budget is enforced BEFORE the call so the router
/// falls through to offline voices instead of billing.
/// </summary>
public sealed class GoogleTtsProvider : ITtsProvider, IDisposable
{
    private const int SampleRateHz = 24_000;

    private readonly IOptionsMonitor<SpeechOptions> _options;
    private readonly TtsDiskCache _cache;
    private readonly TtsUsageTracker _usage;
    private readonly ILogger<GoogleTtsProvider> _logger;
    private readonly IDisposable? _optionsSubscription;
    private readonly object _clientGate = new();

    private TextToSpeechClient? _client;
    private bool _clientFailed;

    public GoogleTtsProvider(
        IOptionsMonitor<SpeechOptions> options,
        TtsDiskCache cache,
        TtsUsageTracker usage,
        ILogger<GoogleTtsProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _cache = cache;
        _usage = usage;
        _logger = logger;
        // A settings save resets the failed latch — the fix for a wrong key path is editing it.
        _optionsSubscription = options.OnChange(_ => Invalidate());
    }

    public string Name => "google";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.CurrentValue.GoogleKeyFilePath)
        && !string.IsNullOrWhiteSpace(_options.CurrentValue.GoogleVoice);

    public bool IsNetworkProvider => true;

    public async Task<TtsAudio> SynthesizeAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var options = _options.CurrentValue;
        var voice = options.GoogleVoice;
        var root = SpeechPaths.CacheRoot(options);

        var cached = await _cache.GetAsync(root, Name, voice, text).ConfigureAwait(false);
        if (cached is not null)
        {
            return new TtsAudio(cached, Name);
        }

        if (_usage.IsOverBudget(root, options.GoogleMonthlyCharBudget, text.Length))
        {
            throw new InvalidOperationException(
                $"Google TTS monthly character budget ({options.GoogleMonthlyCharBudget:N0}) reached — skipping until next month");
        }

        var client = Client(options) ?? throw new InvalidOperationException("Google TTS client unavailable");

        var response = await client.SynthesizeSpeechAsync(
            new SynthesisInput { Text = text },
            new VoiceSelectionParams { Name = voice, LanguageCode = LanguageCodeFromVoice(voice) },
            new AudioConfig { AudioEncoding = AudioEncoding.Linear16, SampleRateHertz = SampleRateHz },
            cancellationToken).ConfigureAwait(false);

        var wav = response.AudioContent.ToByteArray();
        if (wav.Length == 0)
        {
            throw new InvalidOperationException("Google TTS returned empty audio");
        }

        _usage.Increment(root, text.Length);
        WavRepair.NormalizeSizes(wav);
        // The Google cache is deliberately uncapped — paid audio is the last thing to evict.
        await _cache.PutAsync(root, Name, voice, text, wav, maxMbPerVoice: 0).ConfigureAwait(false);
        return new TtsAudio(wav, Name);
    }

    public void Dispose() => _optionsSubscription?.Dispose();

    private TextToSpeechClient? Client(SpeechOptions options)
    {
        lock (_clientGate)
        {
            if (_client is not null)
            {
                return _client;
            }

            if (_clientFailed)
            {
                return null;
            }

            try
            {
                if (!File.Exists(options.GoogleKeyFilePath))
                {
                    _logger.LogWarning(
                        "Google TTS credentials file not found: '{Path}' — provider unavailable",
                        options.GoogleKeyFilePath);
                    _clientFailed = true;
                    return null;
                }

                // CredentialFactory + explicit credential type: the key file MUST be a
                // service-account key — anything else (user creds, external accounts) is
                // rejected here instead of silently accepted (why GoogleCredential.FromFile
                // was deprecated).
                _client = new TextToSpeechClientBuilder
                {
                    GoogleCredential = Google.Apis.Auth.OAuth2.CredentialFactory
                        .FromFile<Google.Apis.Auth.OAuth2.ServiceAccountCredential>(options.GoogleKeyFilePath)
                        .ToGoogleCredential(),
                }.Build();
                return _client;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Google TTS client build failed — provider unavailable until settings change");
                _clientFailed = true;
                return null;
            }
        }
    }

    private void Invalidate()
    {
        lock (_clientGate)
        {
            _client = null;
            _clientFailed = false;
        }
    }

    /// <summary>"en-US-Chirp3-HD-Aoede" → "en-US"; anything unparseable falls back to en-US.</summary>
    private static string LanguageCodeFromVoice(string voice)
    {
        var parts = voice.Split('-');
        return parts.Length >= 2 ? $"{parts[0]}-{parts[1]}" : "en-US";
    }
}
