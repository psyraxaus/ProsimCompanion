using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Core.Weather;

/// <summary>
/// Weather from ActiveSky (HiFi) — the weather actually injected into the sim, so it is what
/// briefings and any destination weather-watch should compare against. Tries two documented
/// interfaces in order:
/// <list type="number">
/// <item>the <c>current_wx_snapshot.txt</c> file (the mechanism ProSim / PFPX / other add-ons
///   read): one <c>ICAO::METAR::TAF::windsAloft</c> line per station;</item>
/// <item>the local HTTP API (<c>http://host:port/ActiveSky/API/GetMetarInfoAt</c>) when
///   ActiveSky is running.</item>
/// </list>
/// Returns an empty probe when neither is available (e.g. ActiveSky not installed) so the
/// composite provider can fall through — <see cref="WxProbeStatus.Unavailable"/> when no
/// interface answered at all, <see cref="WxProbeStatus.NoData"/> when ActiveSky answered but
/// has nothing for the ICAO. Never throws.
/// </summary>
public sealed class ActiveSkyWxProvider : IWxProvider
{
    // 2026-08: live-verified — the API returns the raw METAR text with an EMPTY Content-Type.
    // Never require JSON or send an Accept header; a plain GetStringAsync tolerates both the
    // raw-text and any future JSON shape (handled in ExtractMetarFromApiBody).
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly IOptionsMonitor<WeatherOptions> _options;
    private readonly ILogger<ActiveSkyWxProvider> _logger;

    public ActiveSkyWxProvider(IOptionsMonitor<WeatherOptions> options, ILogger<ActiveSkyWxProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _logger = logger;
    }

    public async Task<WxProbe> ProbeAsync(string? icao, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(icao))
        {
            return WxProbe.NoData(null);
        }

        var id = icao.Trim().ToUpperInvariant();
        var options = _options.CurrentValue;

        // "Source seen" = a snapshot file existed or the API answered — that turns an empty
        // result into "ActiveSky has no METAR for X" instead of "ActiveSky not connected".
        var snapshotPath = ResolveSnapshotPath(options);
        var sourceSeen = snapshotPath is not null;

        var fromFile = snapshotPath is null ? null : ReadFromFile(id, snapshotPath);
        if (fromFile is not null)
        {
            return WxProbe.Found(MetarParser.ToFacts(fromFile));
        }

        if (options.UseActiveSkyApi)
        {
            var (apiReachable, fromApi) = await ReadFromApiAsync(id, options, cancellationToken).ConfigureAwait(false);
            sourceSeen |= apiReachable;
            if (fromApi is not null)
            {
                return WxProbe.Found(MetarParser.ToFacts(fromApi));
            }
        }

        return sourceSeen
            ? WxProbe.NoData($"ActiveSky has no METAR for {id}")
            : WxProbe.Unavailable("ActiveSky not connected");
    }

    // ---- file snapshot ----

    private string? ReadFromFile(string icao, string path)
    {
        try
        {
            // ActiveSky rewrites the file continuously — share read/write so a concurrent
            // write doesn't block us (an exclusive open here fails intermittently in flight).
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var metar = FindMetarInSnapshot(ReadLines(reader), icao);
            if (metar is not null)
            {
                _logger.LogInformation("ActiveSky WX {Icao} from snapshot: \"{Metar}\"", icao, metar);
            }

            return metar;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ActiveSky snapshot read failed ({Path})", path);
            return null;
        }
    }

    private static IEnumerable<string> ReadLines(TextReader reader)
    {
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    /// <summary>
    /// Scans snapshot lines (<c>ICAO::METAR::TAF::windsAloft</c>) for <paramref name="icao"/>.
    /// <c>*</c> is ActiveSky's no-data sentinel; a matched station without a usable METAR
    /// returns null <b>without scanning further</b> — the file has one line per station, so a
    /// later duplicate would be stale. Pure so it is testable without a file.
    /// </summary>
    public static string? FindMetarInSnapshot(IEnumerable<string> lines, string icao)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(icao);

        var prefix = icao + "::";
        foreach (var line in lines)
        {
            if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fields = line.Split("::");
            var metar = fields.Length > 1 ? fields[1].Trim() : null;
            return !string.IsNullOrWhiteSpace(metar) && metar != "*" ? metar : null;
        }

        return null;
    }

    private string? ResolveSnapshotPath(WeatherOptions options)
    {
        // An explicit path is authoritative: when set but missing we do NOT fall through to
        // auto-detect, so a deliberate configuration never silently reads another install.
        if (!string.IsNullOrWhiteSpace(options.ActiveSkySnapshotPath))
        {
            return File.Exists(options.ActiveSkySnapshotPath) ? options.ActiveSkySnapshotPath : null;
        }

        foreach (var candidate in CandidateSnapshotPaths())
        {
            if (File.Exists(candidate))
            {
                _logger.LogDebug("ActiveSky snapshot auto-detected at {Path}", candidate);
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidateSnapshotPaths()
    {
        const string file = "current_wx_snapshot.txt";
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        yield return Path.Combine(appData, "HiFi", "ASFS", file);
        yield return Path.Combine(local, "HiFi", "ASFS", file);
        yield return Path.Combine(docs, "HiFi", "ASFS", file);
        yield return Path.Combine(docs, "Active Sky", "ASFS", file);
    }

    // ---- HTTP API ----

    private async Task<(bool Reachable, string? Metar)> ReadFromApiAsync(string icao, WeatherOptions options, CancellationToken cancellationToken)
    {
        var url = $"http://{options.ActiveSkyApiHost}:{options.ActiveSkyApiPort}/ActiveSky/API/GetMetarInfoAt?ICAO={Uri.EscapeDataString(icao)}";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.ActiveSkyApiTimeoutSeconds)));
            var body = await Http.GetStringAsync(new Uri(url), timeout.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
            {
                return (true, null); // API answered — ActiveSky just has nothing for this ICAO
            }

            var metar = ExtractMetarFromApiBody(body);
            if (!string.IsNullOrWhiteSpace(metar))
            {
                _logger.LogInformation("ActiveSky WX {Icao} from API: \"{Metar}\"", icao, metar);
                return (true, metar);
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ActiveSky API unavailable ({Url})", url);
            return (false, null);
        }
    }

    /// <summary>Pulls a METAR out of the API response whether it is JSON carrying a metar
    /// field (any of the known spellings, at any depth) or the live-verified raw-text body: a
    /// line with a Zulu time group and a KT wind group, else the whole body if it has KT.</summary>
    public static string? ExtractMetarFromApiBody(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        body = body.Trim();
        if (body.StartsWith('{') || body.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var fromJson = FindMetar(doc.RootElement);
                if (!string.IsNullOrWhiteSpace(fromJson))
                {
                    return fromJson;
                }
            }
            catch (JsonException)
            {
                // Fall through to raw handling — the body only looked like JSON.
            }
        }

        foreach (var line in body.Split('\n'))
        {
            if (line.Contains('Z', StringComparison.Ordinal) && line.Contains("KT", StringComparison.Ordinal))
            {
                return line.Trim();
            }
        }

        return body.Contains("KT", StringComparison.Ordinal) ? body : null;
    }

    private static string? FindMetar(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var name in new[] { "metar", "MetarString", "RawMetar", "raw_metar", "rawText" })
                {
                    if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                    {
                        return value.GetString();
                    }
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (FindMetar(property.Value) is { } nested)
                    {
                        return nested;
                    }
                }

                return null;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (FindMetar(item) is { } nested)
                    {
                        return nested;
                    }
                }

                return null;
            default:
                return null;
        }
    }
}
