using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Gateway;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Briefings;

/// <summary>
/// Voice departure/arrival briefings: procedure identifiers resolved FMS-first
/// (aircraft.fms.flightPlanXml) with manual-settings fallback, nav facts from the Navigraph
/// DFD, V-speeds from the FMS, weather from the ProSim EFB gateway METAR (wind + QNH parsed
/// from the raw METAR), minima echoed from the crew-entered store. Composition: optional
/// OpenAI-compatible LLM behind the number verifier (one re-ask with the allowed set; any
/// failure falls back), else the deterministic template — the template is always the floor.
/// </summary>
public sealed class BriefingService : IVoiceFeature, IDisposable
{
    private static readonly string[] DeparturePhrases =
    [
        "brief departure", "departure briefing", "brief the departure", "departure brief",
        "run departure brief", "run the departure brief", "run the departure briefing",
        "do the departure briefing",
    ];

    private static readonly string[] ArrivalPhrases =
    [
        "brief arrival", "arrival briefing", "brief the arrival", "arrival brief",
        "approach briefing", "run the arrival brief", "run the arrival briefing",
    ];

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly IOptionsMonitor<BriefingOptions> _options;
    private readonly DfdNavDataProvider _navData;
    private readonly IProsimDataRefs _dataRefs;
    private readonly IProsimGateway _gateway;
    private readonly ArrivalMinimaStore _minima;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<BriefingService> _logger;
    private readonly Dictionary<string, IDataRefSubscription> _reads = new(StringComparer.Ordinal);

    public BriefingService(
        IOptionsMonitor<BriefingOptions> options,
        DfdNavDataProvider navData,
        IProsimDataRefs dataRefs,
        IProsimGateway gateway,
        ArrivalMinimaStore minima,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        ILogger<BriefingService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(navData);
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(minima);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _navData = navData;
        _dataRefs = dataRefs;
        _gateway = gateway;
        _minima = minima;
        _arbiter = arbiter;
        _eventLog = eventLog;
        _logger = logger;
    }

    public IEnumerable<string> Phrases => [.. DeparturePhrases, .. ArrivalPhrases];

    public bool ValueParse => false;

    public bool TryHandle(string utterance)
    {
        var text = CommandMatcher.Normalize(utterance);
        if (DeparturePhrases.Contains(text))
        {
            _ = RunAsync(departure: true);
            return true;
        }

        if (ArrivalPhrases.Contains(text))
        {
            _ = RunAsync(departure: false);
            return true;
        }

        return false;
    }

    /// <summary>Composes and speaks a briefing (also callable from the web UI).</summary>
    public async Task RunAsync(bool departure)
    {
        try
        {
            var facts = await BuildFactsAsync(departure).ConfigureAwait(false);
            var narrative = await ComposeAsync(facts).ConfigureAwait(false);
            // Route breadcrumb for the logbook/debrief extractor: the briefing is the one place
            // the resolved airport + runway exist as facts (Prosim2FO emitted flight.route from
            // the same spot). A flight flown without a briefing simply has no route on record.
            if (!string.IsNullOrWhiteSpace(facts.Airport))
            {
                _eventLog.Record("flight.route", new
                {
                    role = departure ? "departure" : "arrival",
                    airport = facts.Airport,
                    runway = facts.Runway,
                });
            }

            _eventLog.Record("briefing.spoken", new { departure, narrative });
            await _arbiter.EnqueueAsync(new SpeechRequest(
                narrative, SpeechPriority.Normal, Tag: departure ? "briefing.departure" : "briefing.arrival"))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Briefing failed");
            _ = _arbiter.SpeakAsync("Unable to compose the briefing.", SpeechPriority.Normal);
        }
    }

    public void Dispose()
    {
        foreach (var read in _reads.Values)
        {
            read.Dispose();
        }
    }

    private async Task<BriefingFacts> BuildFactsAsync(bool departure)
    {
        var options = _options.CurrentValue;
        var plan = ParseFmsPlan(Read<string>("aircraft.fms.flightPlanXml"));

        var airport = FirstNonEmpty(
            departure ? plan.Origin : plan.Destination,
            departure ? options.DepartureAirport : options.ArrivalAirport);
        var runway = FirstNonEmpty(
            departure ? plan.OriginRunway : plan.DestinationRunway,
            departure ? options.DepartureRunway : options.ArrivalRunway);
        var sid = departure ? FirstNonEmpty(plan.Sid, options.DepartureSid) : null;
        var star = departure ? null : FirstNonEmpty(plan.Star, options.ArrivalStar);
        var approach = departure ? null : NullIfEmpty(options.ArrivalApproach);

        var nav = airport is null
            ? new NavDataFacts(null, null, null, null, null, null, null, null, null)
            : _navData.Lookup(airport, runway);

        int? v1 = null, vr = null, v2 = null;
        if (departure)
        {
            v1 = PositiveOrNull(Read<int>("aircraft.fms.perf.takeOff.v1"));
            vr = PositiveOrNull(Read<int>("aircraft.fms.perf.takeOff.vr"));
            v2 = PositiveOrNull(Read<int>("aircraft.fms.perf.takeOff.v2"));
        }

        int? windDir = null, windSpeed = null, qnh = null;
        if (airport is not null)
        {
            var metar = await FetchMetarAsync(airport).ConfigureAwait(false);
            (windDir, windSpeed, qnh) = ParseMetarBasics(metar);
        }

        return new BriefingFacts(
            departure, airport, runway, sid, star, approach, nav, v1, vr, v2,
            windDir, windSpeed, qnh, departure ? null : _minima.Current);
    }

    private async Task<string> ComposeAsync(BriefingFacts facts)
    {
        var template = BriefingComposer.Template(facts);
        var options = _options.CurrentValue;
        if (!options.LlmEnabled || string.IsNullOrWhiteSpace(options.LlmModel))
        {
            return template;
        }

        try
        {
            var factBlock = BriefingComposer.FactBlock(facts);
            var narrative = await CompleteAsync(
                BriefingComposer.SystemPrompt(facts.IsDeparture),
                factBlock + "\n\nWrite the spoken briefing now.").ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(narrative))
            {
                return template;
            }

            if (!options.VerifyNumbers)
            {
                return narrative;
            }

            var (offending, allowed) = BriefingComposer.VerifyNumbers(narrative, facts);
            if (offending.Count == 0)
            {
                return narrative;
            }

            _logger.LogWarning("Briefing number verification failed — unverified: {Tokens}",
                string.Join(", ", offending));
            var retry = await CompleteAsync(
                BriefingComposer.SystemPrompt(facts.IsDeparture),
                factBlock + "\n\nUse ONLY these numbers, exactly as written, and no others: "
                    + string.Join(", ", allowed.Select(a => a.ToString("0.##", CultureInfo.InvariantCulture)).Distinct())
                    + "\nWrite the spoken briefing now.").ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(retry)
                && BriefingComposer.VerifyNumbers(retry, facts).Offending.Count == 0)
            {
                return retry;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM briefing composition failed — using the template");
        }

        return template;
    }

    private async Task<string?> CompleteAsync(string system, string user)
    {
        var options = _options.CurrentValue;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(5, options.LlmTimeoutSeconds)));
        using var request = new HttpRequestMessage(
            HttpMethod.Post, options.LlmBaseUrl.TrimEnd('/') + "/chat/completions");
        if (!string.IsNullOrWhiteSpace(options.LlmApiKey))
        {
            request.Headers.Authorization = new("Bearer", options.LlmApiKey);
        }

        request.Content = System.Net.Http.Json.JsonContent.Create(new
        {
            model = options.LlmModel,
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user },
            },
            max_tokens = options.LlmMaxTokens,
            stream = false,
        });

        using var response = await Http.SendAsync(request, cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false));
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message")
            .GetProperty("content").GetString();
    }

    private async Task<string?> FetchMetarAsync(string icao)
    {
        try
        {
            var metar = await _gateway.GetMetarAsync(icao).ConfigureAwait(false);
            return metar?.MetarText;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "METAR fetch failed for {Icao}", icao);
            return null;
        }
    }

    /// <summary>Minimal METAR body parse: wind dddss(Ggg)KT + QNH (Q hPa or A inHg).</summary>
    public static (int? WindDir, int? WindSpeed, int? QnhHpa) ParseMetarBasics(string? metar)
    {
        if (string.IsNullOrWhiteSpace(metar))
        {
            return (null, null, null);
        }

        var body = metar.Split(" RMK ")[0];
        int? dir = null, speed = null, qnh = null;

        var wind = Regex.Match(body, @"\b(\d{3})(\d{2,3})(?:G\d{2,3})?KT\b");
        if (wind.Success)
        {
            dir = int.Parse(wind.Groups[1].Value, CultureInfo.InvariantCulture);
            speed = int.Parse(wind.Groups[2].Value, CultureInfo.InvariantCulture);
        }

        var q = Regex.Match(body, @"\bQ(\d{3,4})\b");
        if (q.Success)
        {
            qnh = int.Parse(q.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        else
        {
            var a = Regex.Match(body, @"\bA(\d{4})\b");
            if (a.Success)
            {
                qnh = (int)Math.Round(
                    int.Parse(a.Groups[1].Value, CultureInfo.InvariantCulture) / 100.0 * 33.8639);
            }
        }

        return (dir, speed, qnh);
    }

    /// <summary>Parses the FMS active route: origin/destination (+runways) from attributes,
    /// SID/STAR from the dotted LNAV flight-plan string.</summary>
    public static (string? Origin, string? Destination, string? OriginRunway,
        string? DestinationRunway, string? Sid, string? Star) ParseFmsPlan(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return (null, null, null, null, null, null);
        }

        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(xml);
            var route = doc.Descendants("route")
                .FirstOrDefault(r => (string?)r.Attribute("type") == "act")
                ?? doc.Descendants("route").FirstOrDefault();
            if (route is null)
            {
                return (null, null, null, null, null, null);
            }

            var origin = (string?)route.Attribute("origin");
            var destination = (string?)route.Attribute("destination");
            var originRunway = (string?)route.Attribute("originRunway");
            var destinationRunway = (string?)route.Attribute("destinationRunway");

            string? sid = null, star = null;
            var lnav = (string?)route.Attribute("flightplan") ?? route.Element("flightplan")?.Value;
            if (!string.IsNullOrWhiteSpace(lnav) && origin is not null && destination is not null)
            {
                var tokens = lnav.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var afterOrigin = Array.FindIndex(tokens, t =>
                    t.StartsWith(origin, StringComparison.OrdinalIgnoreCase));
                if (afterOrigin >= 0 && afterOrigin + 1 < tokens.Length
                    && tokens[afterOrigin + 1].Contains('.', StringComparison.Ordinal))
                {
                    sid = tokens[afterOrigin + 1].Split('.')[0];
                }

                var beforeDest = Array.FindIndex(tokens, t =>
                    t.StartsWith(destination, StringComparison.OrdinalIgnoreCase));
                if (beforeDest > 0 && tokens[beforeDest - 1].Contains('.', StringComparison.Ordinal))
                {
                    star = tokens[beforeDest - 1].Split('.')[^1];
                }
            }

            return (origin, destination, originRunway, destinationRunway, sid, star);
        }
        catch
        {
            return (null, null, null, null, null, null);
        }
    }

    private T? Read<T>(string dataref)
    {
        if (!_reads.TryGetValue(dataref, out var read))
        {
            read = _dataRefs.Subscribe(dataref, DataRefTier.Infrequent);
            _reads[dataref] = read;
        }

        return read.GetValue<T?>(default);
    }

    private static int? PositiveOrNull(int? value) => value is > 0 ? value : null;

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string? NullIfEmpty(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
