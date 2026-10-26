using System.Globalization;
using ProsimCompanion.Core.Diagnostics;
using ProsimCompanion.Core.Diagnostics.Ecam;
using ProsimCompanion.Core.Diagnostics.Metrics;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Web.Diagnostics;

/// <summary>
/// The REMOTE page's web half: the WEB HOST box (open circuits, API requests per second,
/// listen endpoint, TLS), one DISPLAY box per open circuit and the STREAM DECK box from the
/// API client registry (the plugin's user agent, see docs/integrations/streamdeck.md).
/// </summary>
public sealed class WebHostDiagnosticsCollector : IDiagnosticsCollector
{
    /// <summary>A Stream Deck poll older than this is amber: the plugin polls every second,
    /// and its own back-off caps at 30 s, so five seconds of silence is a real gap.</summary>
    public static readonly TimeSpan StreamDeckStaleAfter = TimeSpan.FromSeconds(5);

    /// <summary>How many DISPLAY boxes the page has room for.</summary>
    public const int MaxDisplays = 4;

    private readonly CircuitRegistry _circuits;
    private readonly WebListenerStatus _listener;
    private readonly RateRegistry _rates;
    private readonly ApiClientRegistry _apiClients;
    private readonly StateTransitions<string> _edges = new();

    public WebHostDiagnosticsCollector(
        CircuitRegistry circuits,
        WebListenerStatus listener,
        RateRegistry rates,
        ApiClientRegistry apiClients)
    {
        ArgumentNullException.ThrowIfNull(circuits);
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentNullException.ThrowIfNull(rates);
        ArgumentNullException.ThrowIfNull(apiClients);
        _circuits = circuits;
        _listener = listener;
        _rates = rates;
        _apiClients = apiClients;
    }

    public string Page => DiagPageIds.Remote;

    public void Contribute(DiagPageBuilder page, DiagCautionSink cautions)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(cautions);

        var now = page.Events.Now;
        var circuits = _circuits.Snapshot();
        var listener = _listener.Snapshot();
        var lost = circuits.Count(c => !c.Connected);

        page.Node("webhost", DiagState.Normal,
            $"{circuits.Count} {(circuits.Count == 1 ? "CLIENT" : "CLIENTS")}",
            $"{_rates[RateNames.Api].PerSecond.ToString("0.0", CultureInfo.InvariantCulture)} REQ/S");
        page.Readout("listen", ListenText(listener.HttpUrl));
        page.Readout("tls", listener.HttpsUp ? EcamTokens.On : EcamTokens.Off);
        page.Readout("tls_detail", listener.HttpsProblem?.ToUpperInvariant());
        page.Readout("memo_clients", circuits.Count.ToString(CultureInfo.InvariantCulture));
        page.Readout("memo_clients_lost", lost > 0 ? $"{lost} LOST" : null);

        for (var i = 0; i < Math.Min(circuits.Count, MaxDisplays); i++)
        {
            var c = circuits[i];
            var id = $"display_{i + 1}";
            page.Node(id, c.Connected ? DiagState.Normal : DiagState.Caution,
                c.Client, c.Connected ? c.Route.ToUpperInvariant() : "RECONNECTING");
            page.Readout(id + "_addr", c.RemoteAddress);
            page.Readout(id + "_ms", c.RoundTripMs is { } ms ? ms.ToString("0", CultureInfo.InvariantCulture) : null);
            page.Link(id, "webhost");
        }

        var deck = _apiClients.StreamDeck();
        if (deck is null)
        {
            page.NoData("streamdeck", "NEVER POLLED");
        }
        else
        {
            var age = now - deck.LastSeenUtc;
            var stale = age > StreamDeckStaleAfter;
            var version = deck.UserAgent[ApiClientRegistry.StreamDeckUserAgentPrefix.Length..].Trim();
            page.Node("streamdeck", stale ? DiagState.Caution : DiagState.Normal,
                $"{Math.Max(0, (int)age.TotalSeconds)} S",
                $"PLUGIN {version}".ToUpperInvariant());
            page.Readout("streamdeck_addr", deck.RemoteAddress);
            page.Link("streamdeck", "webhost");
            if (stale && age < TimeSpan.FromMinutes(10))
            {
                cautions.Raise("REMOTE", "STREAM DECK NOT POLLING", DiagState.Caution,
                    new DiagAction("LAST POLL", $"{(int)age.TotalSeconds} S AGO"));
            }
        }

        foreach (var (id, _, to) in _edges.Observe([new("STREAM DECK", deck is null ? DiagState.Normal : now - deck.LastSeenUtc > StreamDeckStaleAfter ? DiagState.Caution : DiagState.Normal)]))
        {
            page.Events.Push($"{id} {(to == DiagState.Normal ? "POLLING" : "SILENT")}", to);
        }
    }

    /// <summary>"0.0.0.0:5320" from "http://0.0.0.0:5320"; the URL itself when it does not parse.</summary>
    public static string? ListenText(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? $"{uri.Host}:{uri.Port}" : url;
    }
}
