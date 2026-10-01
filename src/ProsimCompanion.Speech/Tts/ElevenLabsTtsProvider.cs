using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Speech.Tts;

/// <summary>
/// ElevenLabs cloud neural TTS (<c>POST /v1/text-to-speech/{voice_id}</c>, key in the
/// <c>xi-api-key</c> header). ElevenLabs never emits WAV: the default <c>mp3_*</c> formats
/// (every plan, Free included) are decoded locally through Media Foundation, the paid-plan
/// <c>pcm_*</c> formats are wrapped in a WAV header. Paid-provider patterns follow Google:
/// the per-request cap and the monthly character budget are enforced BEFORE the call so the
/// router falls through to offline voices instead of billing, the cache is uncapped, a
/// rejected key (401) latches until settings change, and the usage counter only moves on a
/// real call — never a cache hit.
/// <para>Role voices: <c>voiceOverride</c> ids are passed verbatim as the voice_id, so the
/// crew role ids (<c>voices.purser</c> / <c>company</c> / <c>ground</c>) must be ElevenLabs
/// voice_ids while this provider serves the chain — the same single-string-per-role rule
/// the Kokoro/Google split already lives with.</para>
/// </summary>
public sealed class ElevenLabsTtsProvider : ITtsProvider, IDisposable
{
    /// <summary>Fallbacks for blank option strings, so a half-edited settings file still
    /// produces a sane request.</summary>
    private const string DefaultBaseUrl = "https://api.elevenlabs.io";
    private const string DefaultModelId = "eleven_flash_v2_5";
    private const string DefaultOutputFormat = "mp3_44100_128";

    private static readonly HttpClient SharedHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly HttpClient _http;
    private readonly IOptionsMonitor<SpeechOptions> _options;
    private readonly TtsDiskCache _cache;
    private readonly TtsUsageTracker _usage;
    private readonly ILogger<ElevenLabsTtsProvider> _logger;
    private readonly IDisposable? _optionsSubscription;

    private volatile bool _keyRejected;

    public ElevenLabsTtsProvider(
        IOptionsMonitor<SpeechOptions> options,
        TtsDiskCache cache,
        TtsUsageTracker usage,
        ILogger<ElevenLabsTtsProvider> logger)
        : this(options, cache, usage, logger, SharedHttp)
    {
    }

    /// <summary>Test seam: an injected client (fake handler) exercises the request shape,
    /// the pre-call guards and the decode paths without a network or a real key.</summary>
    internal ElevenLabsTtsProvider(
        IOptionsMonitor<SpeechOptions> options,
        TtsDiskCache cache,
        TtsUsageTracker usage,
        ILogger<ElevenLabsTtsProvider> logger,
        HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(http);

        _options = options;
        _cache = cache;
        _usage = usage;
        _logger = logger;
        _http = http;
        // A settings save clears the rejected-key latch — the fix for a bad key is editing it.
        _optionsSubscription = options.OnChange(_ => Invalidate());
    }

    public string Name => "elevenlabs";

    public bool IsConfigured
        => !string.IsNullOrWhiteSpace(_options.CurrentValue.ElevenLabsApiKey)
            && !string.IsNullOrWhiteSpace(_options.CurrentValue.ElevenLabsVoiceId);

    public bool IsNetworkProvider => true;

    public async Task<TtsAudio> SynthesizeAsync(string text, CancellationToken cancellationToken, string? voiceOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var options = _options.CurrentValue;
        var voice = string.IsNullOrWhiteSpace(voiceOverride) ? options.ElevenLabsVoiceId.Trim() : voiceOverride.Trim();
        var modelId = string.IsNullOrWhiteSpace(options.ElevenLabsModelId) ? DefaultModelId : options.ElevenLabsModelId.Trim();
        var format = string.IsNullOrWhiteSpace(options.ElevenLabsOutputFormat) ? DefaultOutputFormat : options.ElevenLabsOutputFormat.Trim();
        // TtsDiskCache keys on provider+voice+text only, so the model and format ride in the
        // voice segment — a model or format switch must never serve the old audio.
        var cacheVoice = $"{voice}@{modelId}@{format}";
        var root = SpeechPaths.CacheRoot(options);

        var cached = await _cache.GetAsync(root, Name, cacheVoice, text).ConfigureAwait(false);
        if (cached is not null)
        {
            return new TtsAudio(cached, Name);
        }

        if (_keyRejected)
        {
            throw new InvalidOperationException(
                "ElevenLabs API key was rejected (HTTP 401) — provider disabled until settings change");
        }

        // Validate the format before spending credits on audio we could not decode.
        var kind = ClassifyFormat(format);

        var cap = options.ElevenLabsMaxCharsPerRequest;
        if (cap > 0 && text.Length > cap)
        {
            throw new InvalidOperationException(
                $"ElevenLabs request of {text.Length} characters exceeds the per-request cap ({cap:N0}) — skipping");
        }

        if (_usage.IsOverBudget(root, options.ElevenLabsMonthlyCharBudget, text.Length, Name))
        {
            throw new InvalidOperationException(
                $"ElevenLabs monthly character budget ({options.ElevenLabsMonthlyCharBudget:N0}) reached — skipping until next month");
        }

        var baseUrl = string.IsNullOrWhiteSpace(options.ElevenLabsBaseUrl) ? DefaultBaseUrl : options.ElevenLabsBaseUrl.Trim().TrimEnd('/');
        var url = $"{baseUrl}/v1/text-to-speech/{Uri.EscapeDataString(voice)}?output_format={Uri.EscapeDataString(format)}";
        var budget = TimeSpan.FromMilliseconds(Math.Max(200, options.ElevenLabsTimeoutMs));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(budget);

        byte[] audio;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("xi-api-key", options.ElevenLabsApiKey.Trim());
            request.Content = JsonContent.Create(new
            {
                text,
                model_id = modelId,
                voice_settings = new
                {
                    stability = options.ElevenLabsStability,
                    similarity_boost = options.ElevenLabsSimilarityBoost,
                    speed = options.ElevenLabsSpeed,
                },
                apply_text_normalization = options.ElevenLabsTextNormalization ? "auto" : "off",
            });

            using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // Latch: every further call would fail the same way and the router would
                // burn its cooldown cycle on a key that cannot work. Cleared on settings change.
                _keyRejected = true;
                _logger.LogWarning("ElevenLabs rejected the API key (HTTP 401) — provider disabled until settings change");
                throw new HttpRequestException("ElevenLabs TTS HTTP 401: API key rejected");
            }

            if (!response.IsSuccessStatusCode)
            {
                // 429 (quota / concurrency) and 422 (bad voice or model id) land here too:
                // the router cools the provider down on any throw. Status + a body snippet is
                // all the diagnosis needs; the key is never part of any message.
                var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                throw new HttpRequestException(
                    $"ElevenLabs TTS HTTP {(int)response.StatusCode}: {body[..Math.Min(body.Length, 160)]}");
            }

            audio = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"ElevenLabs TTS timed out after {(int)budget.TotalMilliseconds} ms");
        }

        if (audio.Length == 0)
        {
            throw new InvalidOperationException("ElevenLabs TTS returned empty audio");
        }

        var wav = kind switch
        {
            FormatKind.Pcm => WavRepair.WrapPcm16(audio, PcmSampleRate(format)),
            _ => Mp3Wav.DecodeMp3ToWav(audio),
        };

        WavRepair.NormalizeSizes(wav);
        _usage.Increment(root, text.Length, Name);
        // Uncapped like Google — paid audio is the last thing to evict.
        await _cache.PutAsync(root, Name, cacheVoice, text, wav, maxMbPerVoice: 0).ConfigureAwait(false);
        return new TtsAudio(wav, Name);
    }

    public void Dispose() => _optionsSubscription?.Dispose();

    private void Invalidate() => _keyRejected = false;

    private enum FormatKind
    {
        Pcm,
        Mp3,
    }

    /// <summary><c>pcm_*</c> is wrapped, <c>mp3_*</c> is decoded; anything else (ulaw, alaw,
    /// opus) has no local decode path and is refused before the request is sent.</summary>
    private static FormatKind ClassifyFormat(string format)
    {
        if (format.StartsWith("pcm_", StringComparison.OrdinalIgnoreCase))
        {
            return FormatKind.Pcm;
        }

        if (format.StartsWith("mp3_", StringComparison.OrdinalIgnoreCase))
        {
            return FormatKind.Mp3;
        }

        throw new NotSupportedException($"ElevenLabs output format '{format}' is not supported — use mp3_* or pcm_*");
    }

    /// <summary>"pcm_24000" → 24000. The API's PCM formats are S16LE mono at the named rate.</summary>
    private static int PcmSampleRate(string format)
    {
        var digits = format["pcm_".Length..];
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var rate) && rate > 0
            ? rate
            : throw new NotSupportedException($"ElevenLabs output format '{format}' has no sample rate");
    }
}
