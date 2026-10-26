using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace ProsimCompanion.Web.Diagnostics;

/// <summary>One open Blazor circuit as the REMOTE page draws it.</summary>
/// <param name="Id">The circuit id (opaque; never shown).</param>
/// <param name="RemoteAddress">The browser's address as the first request saw it.</param>
/// <param name="Client">Browser · device class, upper-case ("CHROME · SIM PC" is the user's
/// label, "SAFARI · IPAD" the user-agent read).</param>
/// <param name="Route">The page the circuit is on ("/briefing").</param>
/// <param name="RoundTripMs">The last JS ping round trip; null before the first.</param>
/// <param name="Connected">False between a connection drop and its recovery (the circuit is
/// retained for a while; the tablet may come back).</param>
/// <param name="OpenedUtc">When the circuit opened.</param>
public sealed record CircuitView(
    string Id,
    string RemoteAddress,
    string Client,
    string Route,
    double? RoundTripMs,
    bool Connected,
    DateTimeOffset OpenedUtc);

/// <summary>The open circuits, one entry per <see cref="CircuitTracker"/> instance; read by
/// the REMOTE page's collector and the nav badge.</summary>
public sealed class CircuitRegistry
{
    private readonly ConcurrentDictionary<string, CircuitView> _circuits = new(StringComparer.Ordinal);

    public void Set(CircuitView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        _circuits[view.Id] = view;
    }

    public void Update(string id, Func<CircuitView, CircuitView> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        if (_circuits.TryGetValue(id, out var current))
        {
            _circuits[id] = mutate(current);
        }
    }

    public void Remove(string id) => _circuits.TryRemove(id, out _);

    /// <summary>Oldest first, so DISPLAY 1 stays DISPLAY 1 while others come and go.</summary>
    public IReadOnlyList<CircuitView> Snapshot() => [.. _circuits.Values.OrderBy(c => c.OpenedUtc)];

    public int Count => _circuits.Count;
}

/// <summary>
/// Per-circuit presence for the REMOTE diagnostics page: who is looking at the app, from
/// where, on which page, and how far away in milliseconds. A <see cref="CircuitHandler"/> is
/// created per circuit by Blazor, so this is the one object that sees the circuit's HTTP
/// context (address, user agent), its <see cref="NavigationManager"/> (route) and its
/// <see cref="IJSRuntime"/> (a 5 s round-trip ping). Every interop call is guarded: a circuit
/// that is reconnecting throws, and the ping simply records nothing until it is back.
/// </summary>
public sealed class CircuitTracker : CircuitHandler, IDisposable
{
    /// <summary>How often the round trip is measured.</summary>
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(5);

    private readonly CircuitRegistry _registry;
    private readonly NavigationManager _navigation;
    private readonly IJSRuntime _js;
    private readonly IHttpContextAccessor _http;
    private readonly ILogger<CircuitTracker> _logger;
    private Timer? _ping;
    private string? _id;
    private int _pinging;

    public CircuitTracker(
        CircuitRegistry registry,
        NavigationManager navigation,
        IJSRuntime js,
        IHttpContextAccessor http,
        ILogger<CircuitTracker> logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(js);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(logger);
        _registry = registry;
        _navigation = navigation;
        _js = js;
        _http = http;
        _logger = logger;
    }

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        _id = circuit.Id;
        var context = _http.HttpContext;
        var address = context?.Connection.RemoteIpAddress?.ToString() ?? "?";
        var agent = context?.Request.Headers.UserAgent.ToString();
        _registry.Set(new CircuitView(
            circuit.Id, address, ClassifyUserAgent(agent), RouteOf(_navigation.Uri), null, true, DateTimeOffset.UtcNow));
        _navigation.LocationChanged += OnLocationChanged;
        _ping = new Timer(_ => _ = PingAsync(), null, TimeSpan.FromSeconds(2), PingInterval);
        return Task.CompletedTask;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        _registry.Update(circuit.Id, c => c with { Connected = false });
        return Task.CompletedTask;
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        _registry.Update(circuit.Id, c => c with { Connected = true });
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        _registry.Remove(circuit.Id);
        Dispose();
        return Task.CompletedTask;
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        if (_id is { } id)
        {
            _registry.Update(id, c => c with { Route = RouteOf(e.Location) });
        }
    }

    private async Task PingAsync()
    {
        if (_id is not { } id || Interlocked.Exchange(ref _pinging, 1) == 1)
        {
            return;
        }

        try
        {
            var started = Stopwatch.GetTimestamp();
            await _js.InvokeAsync<int>("prosimCompanion.diagPing").ConfigureAwait(false);
            var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            _registry.Update(id, c => c with { RoundTripMs = Math.Round(ms, 0), Connected = true });
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or TaskCanceledException or ObjectDisposedException)
        {
            // Reconnecting, prerendering, or gone: no sample this round.
            _logger.LogTrace(ex, "Circuit ping skipped");
        }
        finally
        {
            Volatile.Write(ref _pinging, 0);
        }
    }

    /// <summary>"/briefing" from a full URI; "/" for the root. Exposed for tests.</summary>
    public static string RouteOf(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return parsed.AbsolutePath.Length == 0 ? "/" : parsed.AbsolutePath;
        }

        return uri.StartsWith('/') ? uri : "/" + uri;
    }

    /// <summary>"BROWSER · DEVICE" from a user agent: Safari on an iPad reads as
    /// "SAFARI · IPAD"; anything unknown as "BROWSER · DESKTOP". Exposed for tests.</summary>
    public static string ClassifyUserAgent(string? userAgent)
    {
        var ua = userAgent ?? "";
        var device = ua.Contains("iPad", StringComparison.OrdinalIgnoreCase) ? "IPAD"
            : ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase) ? "PHONE"
            : ua.Contains("Android", StringComparison.OrdinalIgnoreCase) && ua.Contains("Mobile", StringComparison.OrdinalIgnoreCase) ? "PHONE"
            : ua.Contains("Android", StringComparison.OrdinalIgnoreCase) ? "TABLET"
            : ua.Contains("Macintosh", StringComparison.OrdinalIgnoreCase) && ua.Contains("Mobile", StringComparison.OrdinalIgnoreCase) ? "IPAD"
            : "DESKTOP";
        var browser = ua.Contains("Edg/", StringComparison.Ordinal) ? "EDGE"
            : ua.Contains("OPR/", StringComparison.Ordinal) ? "OPERA"
            : ua.Contains("Firefox/", StringComparison.Ordinal) ? "FIREFOX"
            : ua.Contains("Chrome/", StringComparison.Ordinal) || ua.Contains("CriOS/", StringComparison.Ordinal) ? "CHROME"
            : ua.Contains("Safari/", StringComparison.Ordinal) ? "SAFARI"
            : "BROWSER";
        return $"{browser} · {device}";
    }

    public void Dispose()
    {
        _navigation.LocationChanged -= OnLocationChanged;
        _ping?.Dispose();
        _ping = null;
    }
}
