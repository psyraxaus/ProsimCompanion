using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Speech.Tts;

/// <summary>
/// <c>GET {base}/v2/voices?category=premade</c> for the settings-page voice picker. The
/// response shape (<c>voices[] { voice_id, name, labels { accent, gender }, preview_url }</c>)
/// is read with JsonDocument so an extra or missing label never breaks the list. Failures
/// throw with the status and at most 160 chars of body; the key is never in a message or log.
/// </summary>
public sealed class ElevenLabsVoiceCatalog : IElevenLabsVoiceCatalog
{
    private static readonly HttpClient SharedHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly TimeSpan RequestBudget = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http;
    private readonly ILogger<ElevenLabsVoiceCatalog> _logger;

    public ElevenLabsVoiceCatalog(ILogger<ElevenLabsVoiceCatalog> logger)
        : this(logger, SharedHttp)
    {
    }

    /// <summary>Test seam: an injected client (fake handler) exercises the parser without a
    /// network.</summary>
    internal ElevenLabsVoiceCatalog(ILogger<ElevenLabsVoiceCatalog> logger, HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(http);
        _logger = logger;
        _http = http;
    }

    public async Task<IReadOnlyList<(string Id, string Name, string? Accent, string? Gender, string? PreviewUrl)>> FetchPremadeVoicesAsync(
        string baseUrl, string apiKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var root = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.elevenlabs.io" : baseUrl.Trim().TrimEnd('/');
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(RequestBudget);

        using var request = new HttpRequestMessage(HttpMethod.Get, root + "/v2/voices?category=premade");
        request.Headers.Add("xi-api-key", apiKey.Trim());

        using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("ElevenLabs voice list failed: HTTP {Status}", (int)response.StatusCode);
            throw new HttpRequestException(
                $"ElevenLabs voices HTTP {(int)response.StatusCode}: {body[..Math.Min(body.Length, 160)]}");
        }

        var voices = new List<(string Id, string Name, string? Accent, string? Gender, string? PreviewUrl)>();
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("voices", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return voices;
        }

        foreach (var voice in array.EnumerateArray())
        {
            var id = StringOrNull(voice, "voice_id");
            var name = StringOrNull(voice, "name");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            string? accent = null;
            string? gender = null;
            if (voice.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Object)
            {
                accent = StringOrNull(labels, "accent");
                gender = StringOrNull(labels, "gender");
            }

            voices.Add((id, name, accent, gender, StringOrNull(voice, "preview_url")));
        }

        _logger.LogInformation("ElevenLabs voice list: {Count} premade voices", voices.Count);
        return voices;
    }

    private static string? StringOrNull(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
