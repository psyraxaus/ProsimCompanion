using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ProsimCompanion.Core.Geo;

/// <summary>A short, sourced description of a place, for the FO's facts (issue #153).</summary>
public interface IPlaceSummarySource
{
    /// <summary>The first sentences of the place's encyclopaedia entry, or null when there is
    /// none, the network is down, or the call ran past the timeout. Never throws.</summary>
    Task<PlaceSummary?> SummaryAsync(string title, string? countryName, CancellationToken cancellationToken);
}

/// <param name="Title">The article actually used (after disambiguation).</param>
/// <param name="Extract">Plain-text opening, trimmed to a few sentences.</param>
public sealed record PlaceSummary(string Title, string Extract);

/// <summary>
/// Wikipedia's REST summary endpoint (<c>/api/rest_v1/page/summary/{title}</c>): one small
/// GET per place, three-second ceiling, answers cached for the session. A disambiguation page
/// is retried once as "Title, Country" ("Orange, France"). Optional — the switch on the
/// settings card — and a failure only means the FO's facts come from the model's own memory
/// instead. Wikimedia asks for a descriptive User-Agent; the app's name and repo go in it.
/// </summary>
public sealed class WikipediaSummaryClient : IPlaceSummarySource, IDisposable
{
    public const string Endpoint = "https://en.wikipedia.org/api/rest_v1/page/summary/";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    private readonly HttpClient _http;
    private readonly ILogger<WikipediaSummaryClient> _logger;
    private readonly ConcurrentDictionary<string, PlaceSummary?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public WikipediaSummaryClient(ILogger<WikipediaSummaryClient> logger, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Timeout;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ProsimCompanion/1.0 (https://github.com/psyraxaus/ProsimCompanion)");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public async Task<PlaceSummary?> SummaryAsync(string title, string? countryName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var key = title + "|" + countryName;
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        PlaceSummary? summary = null;
        try
        {
            summary = await FetchAsync(title, cancellationToken).ConfigureAwait(false);
            if (summary is null && !string.IsNullOrWhiteSpace(countryName))
            {
                summary = await FetchAsync($"{title}, {countryName}", cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Wikipedia summary for {Title} failed; the FO answers without it", title);
        }

        if (_cache.Count > 500)
        {
            _cache.Clear();
        }

        _cache[key] = summary;
        return summary;
    }

    private async Task<PlaceSummary?> FetchAsync(string title, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(Endpoint + Uri.EscapeDataString(title.Replace(' ', '_')), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return Parse(json.RootElement);
    }

    /// <summary>Null for a disambiguation page or an empty extract.</summary>
    public static PlaceSummary? Parse(JsonElement root)
    {
        if (root.TryGetProperty("type", out var type) && type.GetString() == "disambiguation")
        {
            return null;
        }

        var extract = root.TryGetProperty("extract", out var e) ? e.GetString() : null;
        if (string.IsNullOrWhiteSpace(extract))
        {
            return null;
        }

        var title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
        return new PlaceSummary(title, Trim(extract));
    }

    /// <summary>The first three sentences, at most ~500 characters — enough for two facts,
    /// small enough to keep the prompt quick on a local model.</summary>
    public static string Trim(string extract)
    {
        var text = extract.Replace('\n', ' ').Trim();
        var end = 0;
        var sentences = 0;
        for (var i = 0; i < text.Length && sentences < 3; i++)
        {
            if (text[i] is '.' or '!' or '?' && (i + 1 == text.Length || text[i + 1] == ' '))
            {
                end = i + 1;
                sentences++;
            }
        }

        if (end > 0 && end < text.Length)
        {
            text = text[..end];
        }

        return text.Length <= 500 ? text : text[..500].TrimEnd() + "…";
    }

    public void Dispose() => _http.Dispose();
}
