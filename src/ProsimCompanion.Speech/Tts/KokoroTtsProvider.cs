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
public sealed class KokoroTtsProvider : ITtsProvider
{
    /// <summary>Body budget growth per character of input text.</summary>
    public const int BodyTimeoutPerCharMs = 100;

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
                Content = JsonContent.Create(new { model, input = text, voice, response_format = "wav" }),
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
