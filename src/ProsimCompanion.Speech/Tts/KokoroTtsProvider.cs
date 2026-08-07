using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Speech.Tts;

/// <summary>
/// Local neural TTS via a kokoro-fastapi instance (OpenAI-compatible
/// <c>POST {base}/v1/audio/speech</c>). The HttpClient deliberately has no global timeout —
/// each call gets its own token from the current settings so a timeout change applies at once
/// (floor 200 ms). Responses are per-voice disk-cached; streaming WAV headers are repaired
/// before caching (see <see cref="WavRepair"/>).
/// </summary>
public sealed class KokoroTtsProvider : ITtsProvider
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly IOptionsMonitor<SpeechOptions> _options;
    private readonly TtsDiskCache _cache;
    private readonly ILogger<KokoroTtsProvider> _logger;

    public KokoroTtsProvider(
        IOptionsMonitor<SpeechOptions> options,
        TtsDiskCache cache,
        ILogger<KokoroTtsProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _cache = cache;
        _logger = logger;
    }

    public string Name => "kokoro";

    public bool IsConfigured
        => !string.IsNullOrWhiteSpace(_options.CurrentValue.KokoroBaseUrl)
            && !string.IsNullOrWhiteSpace(_options.CurrentValue.KokoroVoice);

    public bool IsNetworkProvider => true;

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

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(Math.Max(200, options.KokoroTimeoutMs));

        var url = options.KokoroBaseUrl.TrimEnd('/') + "/v1/audio/speech";
        var model = string.IsNullOrWhiteSpace(options.KokoroModel) ? "kokoro" : options.KokoroModel;
        using var response = await Http.PostAsJsonAsync(
            url,
            new { model, input = text, voice, response_format = "wav" },
            cts.Token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Kokoro TTS HTTP {(int)response.StatusCode}: {body[..Math.Min(body.Length, 160)]}");
        }

        var wav = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
        if (wav.Length == 0)
        {
            throw new InvalidOperationException("Kokoro TTS returned empty audio");
        }

        WavRepair.NormalizeSizes(wav);
        await _cache.PutAsync(root, Name, voice, text, wav, options.KokoroMaxCacheMbPerVoice)
            .ConfigureAwait(false);
        return new TtsAudio(wav, Name);
    }
}
