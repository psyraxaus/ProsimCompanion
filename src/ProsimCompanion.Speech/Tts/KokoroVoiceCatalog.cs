using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Speech.Tts;

/// <summary>
/// <c>GET {base}/v1/audio/voices</c> for the settings-page voice picker. kokoro-fastapi has
/// answered this two ways over its releases — <c>{"voices":["af_heart",…]}</c> (older) and
/// <c>{"voices":[{"id":"af_heart","name":"af_heart"},…]}</c> (seen live 2026-10-12) — so both
/// shapes are read. Failures throw with the status and at most 160 chars of body.
/// </summary>
public sealed class KokoroVoiceCatalog : IKokoroVoiceCatalog
{
    private static readonly HttpClient SharedHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly TimeSpan RequestBudget = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;
    private readonly ILogger<KokoroVoiceCatalog> _logger;

    public KokoroVoiceCatalog(ILogger<KokoroVoiceCatalog> logger)
        : this(logger, SharedHttp)
    {
    }

    /// <summary>Test seam: an injected client (fake handler) exercises the parser without a
    /// network.</summary>
    internal KokoroVoiceCatalog(ILogger<KokoroVoiceCatalog> logger, HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(http);
        _logger = logger;
        _http = http;
    }

    public async Task<IReadOnlyList<string>> ListVoiceIdsAsync(string baseUrl, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        var root = baseUrl.Trim().TrimEnd('/');
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(RequestBudget);

        using var response = await _http.GetAsync(root + "/v1/audio/voices", cts.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Kokoro voice list failed: HTTP {Status}", (int)response.StatusCode);
            throw new HttpRequestException(
                $"Kokoro voices HTTP {(int)response.StatusCode}: {body[..Math.Min(body.Length, 160)]}");
        }

        var ids = new List<string>();
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("voices", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return ids;
        }

        foreach (var entry in array.EnumerateArray())
        {
            var id = entry.ValueKind switch
            {
                JsonValueKind.String => entry.GetString(),
                JsonValueKind.Object when entry.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String => idProp.GetString(),
                JsonValueKind.Object when entry.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String => nameProp.GetString(),
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(id))
            {
                ids.Add(id.Trim());
            }
        }

        _logger.LogInformation("Kokoro voice list: {Count} voices", ids.Count);
        return ids;
    }
}
