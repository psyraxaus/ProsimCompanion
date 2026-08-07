using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Speech.SayIntentions;

/// <summary>
/// SayIntentions batch weather (ATIS/METAR/TAF per airport via <c>getWX</c>) and the CPDLC
/// logon station (via <c>getCurrentFrequencies</c>), published into the <see cref="WeatherStore"/>
/// for the web Weather page and the composite weather-provider backfill.
/// <para>Unlike the ATC-request path, these endpoints need only the API key, NOT an active
/// flight — so this service deliberately does not gate on flight state, only on the key.</para>
/// <para>Cache policy (predecessor-proven): a TTL serves repeat visits from cache, a
/// forced-refresh debounce absorbs button-spam, and a semaphore dedupes concurrent clients —
/// the second caller waits, hits the fresh-cache short-circuit and gets the first call's
/// result for free. Weather and CPDLC are fetched in parallel in the same window; CPDLC is
/// fetched even with no OFP ICAOs (it needs none — a predecessor quirk fixed here).</para>
/// </summary>
public sealed class SayIntentionsWeatherService : IWeatherControl, IDisposable
{
    private const string ApiBase = "https://apipri.sayintentions.ai/sapi";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly IOptionsMonitor<SayIntentionsOptions> _options;
    private readonly WeatherStore _store;
    private readonly OfpStore _ofp;
    private readonly ILogger<SayIntentionsWeatherService> _logger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private DateTimeOffset? _lastForcedRefreshUtc;

    public SayIntentionsWeatherService(
        IOptionsMonitor<SayIntentionsOptions> options,
        WeatherStore store,
        OfpStore ofp,
        ILogger<SayIntentionsWeatherService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(ofp);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _store = store;
        _ofp = ofp;
        _logger = logger;
    }

    public void Dispose() => _refreshGate.Dispose();

    /// <inheritdoc />
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var options = _options.CurrentValue;
        var apiKey = options.Enabled && options.WeatherEnabled ? ResolveApiKey(options) : null;
        if (apiKey is null)
        {
            _store.Update(s => s with
            {
                Departure = null,
                Arrival = null,
                CpdlcStation = "",
                FetchedAtUtc = null,
                Status = "SayIntentions not active. Enable it in Speech Settings and ensure flight.json (or a manual API key) is present.",
            });
            return;
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var snapshot = _store.Snapshot();
            var ttl = TimeSpan.FromMinutes(Math.Max(1, options.WeatherCacheMinutes));
            var debounce = TimeSpan.FromSeconds(Math.Max(0, options.WeatherRefreshDebounceSeconds));

            // Cache fresh → serve as-is, no HTTP. This is also how a second concurrent caller
            // exits after waiting on the gate.
            if (snapshot.FetchedAtUtc is { } fetched && now - fetched <= ttl)
            {
                return;
            }

            // Stale but refreshed again too soon → do not re-fetch.
            var sinceForced = _lastForcedRefreshUtc is { } last ? now - last : TimeSpan.MaxValue;
            if (sinceForced < debounce)
            {
                _store.Update(s => s with
                {
                    Status = $"Refresh limited — last update <{(int)debounce.TotalSeconds}s ago.",
                });
                return;
            }

            var (depIcao, arrIcao, icaos) = ResolveIcaos();

            _store.Update(s => s with { IsRefreshing = true, Status = "" });
            try
            {
                // Weather + CPDLC in parallel — independent calls sharing the TTL window.
                // CPDLC takes no ICAO, so it is fetched even before an OFP is loaded.
                var cpdlcTask = GetCpdlcStationAsync(apiKey, cancellationToken);
                var weatherTask = icaos.Count > 0
                    ? GetWeatherAsync(apiKey, icaos, cancellationToken)
                    : Task.FromResult<IReadOnlyList<AirportWeather>>([]);
                await Task.WhenAll(weatherTask, cpdlcTask).ConfigureAwait(false);

                var cpdlc = await cpdlcTask.ConfigureAwait(false);
                AirportWeather? departure = null, arrival = null;
                foreach (var wx in await weatherTask.ConfigureAwait(false))
                {
                    if (string.Equals(wx.Airport, depIcao, StringComparison.OrdinalIgnoreCase))
                    {
                        departure = wx;
                    }
                    else if (string.Equals(wx.Airport, arrIcao, StringComparison.OrdinalIgnoreCase))
                    {
                        arrival = wx;
                    }
                }

                var status = icaos.Count == 0
                    ? "No ICAOs available — load an OFP first."
                    : departure is null && arrival is null ? "Weather request returned no data." : "";

                _lastForcedRefreshUtc = now;
                _store.Update(s => s with
                {
                    Departure = departure,
                    Arrival = arrival,
                    CpdlcStation = cpdlc,
                    Status = status,
                    // Never pin an OFP-less result behind the TTL: leaving FetchedAt unset
                    // lets the first refresh after an OFP load fetch immediately (the
                    // debounce alone throttles the no-OFP case).
                    FetchedAtUtc = icaos.Count > 0 ? now : null,
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // No debounce stamp on failure — an immediate retry is allowed (predecessor rule).
                _logger.LogWarning(ex, "SayIntentions weather refresh failed");
                _store.Update(s => s with { Status = $"Weather request failed: {ex.Message}" });
            }
            finally
            {
                _store.Update(s => s with { IsRefreshing = false });
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>Origin/destination from the current OFP, arrival deduped against departure.</summary>
    private (string DepIcao, string ArrIcao, IReadOnlyList<string> Icaos) ResolveIcaos()
    {
        var ofp = _ofp.Current;
        var dep = Normalize(ofp?.OriginIcao);
        var arr = Normalize(ofp?.DestinationIcao);

        var icaos = new List<string>(2);
        if (dep.Length > 0)
        {
            icaos.Add(dep);
        }

        if (arr.Length > 0 && !string.Equals(arr, dep, StringComparison.Ordinal))
        {
            icaos.Add(arr);
        }

        return (dep, arr, icaos);

        static string Normalize(string? icao) => icao?.Trim().ToUpperInvariant() ?? "";
    }

    private async Task<IReadOnlyList<AirportWeather>> GetWeatherAsync(
        string apiKey, IReadOnlyList<string> icaos, CancellationToken cancellationToken)
    {
        var icaoParam = string.Join(",", icaos);
        using var response = await Http.GetAsync(
            $"{ApiBase}/getWX?api_key={Uri.EscapeDataString(apiKey)}&icao={Uri.EscapeDataString(icaoParam)}",
            cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var result = ParseAirports(body);
        _logger.LogDebug("SayIntentions getWX returned {Count} airport(s) for [{Icaos}]",
            result.Count, icaoParam);
        return result;
    }

    private async Task<string> GetCpdlcStationAsync(string apiKey, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Http.GetAsync(
                $"{ApiBase}/getCurrentFrequencies?api_key={Uri.EscapeDataString(apiKey)}",
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("SayIntentions getCurrentFrequencies HTTP {Status}", (int)response.StatusCode);
                return "";
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseCpdlcStation(body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // CPDLC is decoration on the weather panel — its failure must not fail the refresh.
            _logger.LogDebug(ex, "SayIntentions getCurrentFrequencies failed");
            return "";
        }
    }

    /// <summary>
    /// Parses the getWX body — <c>{"airports":[{airport,atis,metar,taf,active_runway,
    /// wind_direction,wind_speed}]}</c> — tolerantly: missing fields become empty/null, wind
    /// values may be numbers or numeric strings, and a malformed body yields an empty list
    /// rather than an exception. Pure so it is testable without HTTP.
    /// </summary>
    public static IReadOnlyList<AirportWeather> ParseAirports(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        try
        {
            if (JsonNode.Parse(body)?["airports"] is not JsonArray airports)
            {
                return [];
            }

            var result = new List<AirportWeather>(airports.Count);
            foreach (var node in airports)
            {
                if (node is null)
                {
                    continue;
                }

                result.Add(new AirportWeather(
                    Airport: Str(node, "airport"),
                    Atis: Str(node, "atis"),
                    Metar: Str(node, "metar"),
                    Taf: Str(node, "taf"),
                    ActiveRunway: Str(node, "active_runway"),
                    WindDirection: Int(node, "wind_direction"),
                    WindSpeed: Int(node, "wind_speed")));
            }

            return result;
        }
        catch (Exception)
        {
            return [];
        }

        static string Str(JsonNode node, string name)
        {
            try
            {
                return node[name]?.GetValue<string>() ?? "";
            }
            catch (Exception)
            {
                return node[name]?.ToString() ?? "";
            }
        }

        static int? Int(JsonNode node, string name)
        {
            try
            {
                return node[name]?.GetValue<int>();
            }
            catch (Exception)
            {
                var text = node[name]?.ToString();
                return int.TryParse(text, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;
            }
        }
    }

    /// <summary>
    /// Scans a getCurrentFrequencies body's <c>frequencies[]</c> for the entry whose
    /// <c>station</c> is <c>CPDLC</c> (case-insensitive) and returns its <c>freq</c> — the
    /// CPDLC logon code (e.g. "EFIN"). Empty string is the only "absent" value: inactive SI,
    /// no CPDLC entry, and malformed bodies all reduce to it. Pure so it is testable without HTTP.
    /// </summary>
    public static string ParseCpdlcStation(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        try
        {
            if (JsonNode.Parse(body)?["frequencies"] is not JsonArray frequencies)
            {
                return "";
            }

            foreach (var node in frequencies)
            {
                if (node is null)
                {
                    continue;
                }

                string? station;
                try
                {
                    station = node["station"]?.GetValue<string>();
                }
                catch (Exception)
                {
                    station = node["station"]?.ToString();
                }

                if (string.Equals(station, "CPDLC", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        return node["freq"]?.GetValue<string>() ?? "";
                    }
                    catch (Exception)
                    {
                        return node["freq"]?.ToString() ?? "";
                    }
                }
            }

            return "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>Same key resolution as the ATC-request service (manual key or flight.json) —
    /// but without the active-flight gate: the key survives in flight.json between flights
    /// and getWX / getCurrentFrequencies are valid then too.</summary>
    private string? ResolveApiKey(SayIntentionsOptions options)
    {
        if (options.ApiKeySource.Equals("manual", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(options.ManualApiKey) ? null : options.ManualApiKey;
        }

        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SayIntentionsAI", "flight.json");
            if (!File.Exists(path))
            {
                return null;
            }

            var root = JsonNode.Parse(File.ReadAllText(path));
            var key = (root?["flight_details"] ?? root)?["api_key"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(key) ? null : key;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "flight.json api-key read failed");
            return null;
        }
    }
}
