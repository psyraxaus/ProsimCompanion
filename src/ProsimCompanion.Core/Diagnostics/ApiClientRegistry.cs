using System.Collections.Concurrent;

namespace ProsimCompanion.Core.Diagnostics;

/// <summary>One HTTP API client as last seen: who, with which user agent, when, how often.</summary>
public sealed record ApiClientView(string RemoteAddress, string UserAgent, DateTimeOffset LastSeenUtc, long Requests);

/// <summary>
/// Presence of the HTTP API's clients, fed by the App's <c>/api/*</c> middleware and read by
/// the REMOTE diagnostics page. The Stream Deck plugin polls <c>/api/status</c> once a second
/// and identifies itself with a <c>ProsimCompanion-StreamDeck/&lt;version&gt;</c> user agent
/// (docs/integrations/streamdeck.md), so "is the deck connected" is "did that user agent poll
/// in the last few seconds". Keyed by remote address + user agent; bounded so a port scan
/// cannot grow it without limit.
/// </summary>
public sealed class ApiClientRegistry
{
    /// <summary>The user-agent prefix the Stream Deck plugin sends.</summary>
    public const string StreamDeckUserAgentPrefix = "ProsimCompanion-StreamDeck/";

    private const int MaxClients = 64;
    private readonly ConcurrentDictionary<string, ApiClientView> _clients = new(StringComparer.Ordinal);

    /// <summary>Records one request.</summary>
    public void Seen(string remoteAddress, string? userAgent, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(remoteAddress);
        var agent = string.IsNullOrWhiteSpace(userAgent) ? "" : userAgent.Trim();
        var id = remoteAddress + "|" + agent;
        if (_clients.Count >= MaxClients && !_clients.ContainsKey(id))
        {
            return;
        }

        _clients.AddOrUpdate(
            id,
            _ => new ApiClientView(remoteAddress, agent, now, 1),
            (_, previous) => previous with { LastSeenUtc = now, Requests = previous.Requests + 1 });
    }

    /// <summary>Every client seen, most recent first.</summary>
    public IReadOnlyList<ApiClientView> Snapshot()
        => [.. _clients.Values.OrderByDescending(c => c.LastSeenUtc)];

    /// <summary>The most recently seen Stream Deck plugin, or null when none ever polled.</summary>
    public ApiClientView? StreamDeck()
        => _clients.Values
            .Where(c => c.UserAgent.StartsWith(StreamDeckUserAgentPrefix, StringComparison.Ordinal))
            .OrderByDescending(c => c.LastSeenUtc)
            .FirstOrDefault();
}
