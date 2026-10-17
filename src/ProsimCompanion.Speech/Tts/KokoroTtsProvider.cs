using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Speech.Tts;

/// <summary>
/// Local neural TTS over kokoro-fastapi. Two timeout phases (issue #113): a tight
/// connect/first-byte budget catches a sleeping LAN host without stalling a callout, and a
/// separate body budget scaled to the text length lets a healthy host stream a long WAV —
/// kokoro-fastapi synthesises while it streams, so the 2026-08-28 flight's single 1.5 s
/// budget aborted every long utterance mid-download and tripped the router's 60 s cooldown
/// eight times in one leg (the FO voice audibly changed each time).
/// </summary>
public sealed class KokoroTtsProvider : ITtsProvider, IProbeableTtsProvider
{
    /// <summary>Body budget growth per character of input text.</summary>
    public const int BodyTimeoutPerCharMs = 100;

    /// <summary>Inputs shorter than this (after trimming) are padded by <see cref="PadShort"/>.</summary>
    private const int PadThresholdChars = 20;

    private static readonly HttpClient SharedHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly HttpClient _http;
    private readonly IOptionsMonitor<SpeechOptions> _options;
    private readonly TtsDiskCache _cache;
    private readonly ILogger<KokoroTtsProvider> _logger;

    public KokoroTtsProvider(
        IOptionsMonitor<SpeechOptions> options,
        TtsDiskCache cache,
        ILogger<KokoroTtsProvider> logger)
        : this(options, cache, logger, SharedHttp)
    {
    }

    /// <summary>Test seam: an injected client (fake handler) exercises the timeout phases
    /// without a network.</summary>
    internal KokoroTtsProvider(
        IOptionsMonitor<SpeechOptions> options,
        TtsDiskCache cache,
        ILogger<KokoroTtsProvider> logger,
        HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(http);

        _options = options;
        _cache = cache;
        _logger = logger;
        _http = http;
    }

    public string Name => "kokoro";

    /// <summary>kokoro-fastapi's <c>/health</c>: a 2xx means the service is up (2026-09-20:
    /// after a macOS update the box answered ping but refused this port for minutes).</summary>
    public async Task<bool> ProbeAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return false;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var request = new HttpRequestMessage(HttpMethod.Get, _options.CurrentValue.KokoroBaseUrl.TrimEnd('/') + "/health");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public bool IsConfigured
        => !string.IsNullOrWhiteSpace(_options.CurrentValue.KokoroBaseUrl)
            && !string.IsNullOrWhiteSpace(_options.CurrentValue.KokoroVoice);

    public bool IsNetworkProvider => true;

    /// <summary>Effective body budget for a text: base plus per-character growth, never
    /// below the connect budget. Exposed for tests and the settings-page hint.</summary>
    public static TimeSpan BodyBudget(SpeechOptions options, int textLength)
    {
        ArgumentNullException.ThrowIfNull(options);
        var connectMs = Math.Max(200, options.KokoroTimeoutMs);
        var bodyMs = Math.Max(connectMs, options.KokoroBodyTimeoutMs + BodyTimeoutPerCharMs * Math.Max(0, textLength));
        return TimeSpan.FromMilliseconds(bodyMs);
    }

    /// <summary>
    /// Gives a lone word or two the prosodic context Kokoro needs to say it whole. Measured
    /// 2026-10-17 on the Mac mini (kokoro-fastapi 0.8.2rc1, am_adam, 24 kHz): a bare
    /// "Checked" came back as a ~15.6 KB / ~325 ms WAV that sounded clipped and mumbled —
    /// Kokoro has no sentence shape to time a single word against, and kokoro-fastapi trims
    /// chunk ends, which eats the consonants on a sub-half-second clip. The same word sent as
    /// ", Checked." or "... Checked." returned a longer, clearly articulated clip; longer
    /// phrases were already fine. So anything under <see cref="PadThresholdChars"/> gets a
    /// leading ", " and a closing full stop unless it already ends in terminal punctuation.
    /// Only the request body is padded: the disk cache stays keyed on the caller's text, so
    /// the kokoro cache folder must be cleared once to re-render previously cached short clips.
    /// </summary>
    internal static string PadShort(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.Trim();
        if (trimmed.Length >= PadThresholdChars)
        {
            return trimmed;
        }

        var terminated = trimmed.EndsWith('.') || trimmed.EndsWith('!') || trimmed.EndsWith('?');
        return ", " + (terminated ? trimmed : trimmed + ".");
    }

    public async Task<TtsAudio> SynthesizeAsync(string text, CancellationToken cancellationToken, string? voiceOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var options = _options.CurrentValue;
        // The EFFECTIVE voice keys the disk cache below, so role voices can never collide
        // with (or evict past) the FO voice's entries.
        var voice = string.IsNullOrWhiteSpace(voiceOverride) ? options.KokoroVoice : voiceOverride;
        var root = SpeechPaths.CacheRoot(options);

        var cached = await _cache.GetAsync(root, Name, voice, text).ConfigureAwait(false);
        if (cached is not null)
        {
            return new TtsAudio(cached, Name);
        }

        var url = options.KokoroBaseUrl.TrimEnd('/') + "/v1/audio/speech";
        var model = string.IsNullOrWhiteSpace(options.KokoroModel) ? "kokoro" : options.KokoroModel;
        var connectBudget = TimeSpan.FromMilliseconds(Math.Max(200, options.KokoroTimeoutMs));
        var bodyBudget = BodyBudget(options, text.Length);

        // Phase 1 — connect / first byte: "is the host awake?" Headers only.
        HttpResponseMessage response;
        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connectCts.CancelAfter(connectBudget);
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(new { model, input = PadShort(text), voice, response_format = "wav" }),
            };
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connectCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Kokoro TTS timed out after {(int)connectBudget.TotalMilliseconds} ms (connect — host asleep or unreachable)");
            }
        }

        using (response)
        {
            // Phase 2 — the streamed body, under its own budget from the CALLER's token (a
            // pre-emption still cancels; the connect budget no longer applies).
            using var bodyCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bodyCts.CancelAfter(bodyBudget);
            try
            {
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(bodyCts.Token).ConfigureAwait(false);
                    throw new HttpRequestException(
                        $"Kokoro TTS HTTP {(int)response.StatusCode}: {body[..Math.Min(body.Length, 160)]}");
                }

                var wav = await response.Content.ReadAsByteArrayAsync(bodyCts.Token).ConfigureAwait(false);
                if (wav.Length == 0)
                {
                    throw new InvalidOperationException("Kokoro TTS returned empty audio");
                }

                WavRepair.NormalizeSizes(wav);
                await _cache.PutAsync(root, Name, voice, text, wav, options.KokoroMaxCacheMbPerVoice)
                    .ConfigureAwait(false);
                return new TtsAudio(wav, Name);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Kokoro TTS timed out after {(int)bodyBudget.TotalMilliseconds} ms (body — {text.Length} chars still streaming)");
            }
        }
    }
}
