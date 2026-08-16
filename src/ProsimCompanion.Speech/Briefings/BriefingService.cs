using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Weather;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Llm;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Briefings;

/// <summary>
/// Voice departure/arrival briefings: procedure identifiers resolved by
/// <see cref="ProcedureSource"/> (FMS → flight.json → manual, with a DFD approach auto-fill),
/// nav facts from the Navigraph DFD, V-speeds + flex from the FMS, weather from the composite
/// <see cref="IWxProvider"/> chain (ActiveSky → ProSim gateway METAR → SayIntentions cache —
/// so the briefing speaks the weather actually injected into the sim when ActiveSky is
/// present), minima echoed from the crew-entered store. Composition: optional
/// OpenAI-compatible LLM behind the number verifier (one re-ask with the allowed set; any
/// failure falls back), else the deterministic template — the template is always the floor.
/// Also answers "which approach" with the DFD's ranked candidates, because the FMS route
/// never carries the approach.
/// </summary>
public sealed class BriefingService : IVoiceFeature, IDisposable
{
    // Voice phrases deliberately mirror the predecessor's set (which mirrored its GUI labels).
    private static readonly string[] DeparturePhrases =
    [
        "brief departure", "departure briefing", "brief the departure", "departure brief",
        "run departure brief", "run the departure brief", "run the departure briefing",
        "do the departure briefing",
    ];

    private static readonly string[] ArrivalPhrases =
    [
        "brief arrival", "arrival briefing", "brief the arrival", "arrival brief",
        "run arrival brief", "run the arrival brief", "run the arrival briefing",
        "do the arrival briefing", "approach briefing",
    ];

    private static readonly string[] ApproachOptionPhrases =
    [
        "which approach", "approach options", "confirm approach", "what approach for landing",
    ];

    private readonly IOptionsMonitor<BriefingOptions> _options;
    private readonly OpenAiChatClient _llm;
    private readonly DfdNavDataProvider _navData;
    private readonly ProcedureSource _procedures;
    private readonly IProsimDataRefs _dataRefs;
    private readonly IWxProvider _weather;
    private readonly ArrivalMinimaStore _minima;
    private readonly MinimaCaptureDialogue _minimaCapture;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<BriefingService> _logger;
    private readonly Persona.PersonaService? _persona;

    // Optional (like the LLM client) so the briefing degrades to spoken ICAO idents when no
    // name source is registered — issue #70.
    private readonly Core.Airports.IAirportNames? _airportNames;
    private readonly Dictionary<string, IDataRefSubscription> _reads = new(StringComparer.Ordinal);

    public BriefingService(
        IOptionsMonitor<BriefingOptions> options,
        DfdNavDataProvider navData,
        ProcedureSource procedures,
        IProsimDataRefs dataRefs,
        IWxProvider weather,
        ArrivalMinimaStore minima,
        MinimaCaptureDialogue minimaCapture,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        ILogger<BriefingService> logger,
        OpenAiChatClient? llm = null,
        Persona.PersonaService? persona = null,
        Core.Airports.IAirportNames? airportNames = null)
    {
        _persona = persona;
        _airportNames = airportNames;
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(navData);
        ArgumentNullException.ThrowIfNull(procedures);
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(weather);
        ArgumentNullException.ThrowIfNull(minima);
        ArgumentNullException.ThrowIfNull(minimaCapture);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        // Optional so DI needs no extra registration; a test passes a client over a fake
        // HTTP handler here.
        _llm = llm ?? new OpenAiChatClient(options);
        _navData = navData;
        _procedures = procedures;
        _dataRefs = dataRefs;
        _weather = weather;
        _minima = minima;
        _minimaCapture = minimaCapture;
        _arbiter = arbiter;
        _eventLog = eventLog;
        _logger = logger;
    }

    public IEnumerable<string> Phrases => [.. DeparturePhrases, .. ArrivalPhrases, .. ApproachOptionPhrases];

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

        if (ApproachOptionPhrases.Contains(text))
        {
            _ = AnnounceApproachOptionsAsync();
            return true;
        }

        return false;
    }

    /// <summary>Composes and speaks a briefing (also callable from the web UI).</summary>
    public async Task RunAsync(bool departure)
    {
        try
        {
            if (!departure)
            {
                // Interactive minima sub-dialogue: capture → read back → confirm. Never
                // proceed past minima on an unconfirmed value — confirmed or "not briefed"
                // only; the confirmed value lands in the store BuildFactsAsync reads.
                await _minimaCapture.RunAsync(CancellationToken.None).ConfigureAwait(false);
            }

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

    /// <summary>Speaks the DFD's ranked approach candidates for the arrival runway — ILS
    /// variants preferred when present (the ranked list is ILS-first anyway).</summary>
    public async Task AnnounceApproachOptionsAsync()
    {
        try
        {
            var ids = _procedures.Resolve(departure: false);
            if (string.IsNullOrWhiteSpace(ids.Runway))
            {
                await _arbiter.SpeakAsync("No arrival runway is set yet.", SpeechPriority.Normal)
                    .ConfigureAwait(false);
                return;
            }

            var options = _navData.ApproachesForRunway(ids.Airport, ids.Runway);
            var runwaySpoken = RunwaySpoken(ids.Runway);
            if (options.Count == 0)
            {
                await _arbiter.SpeakAsync(
                    $"No published approach found for runway {runwaySpoken}.", SpeechPriority.Normal)
                    .ConfigureAwait(false);
                return;
            }

            var ils = options.Where(o => o.Kind == "ILS").ToList();
            var offer = (ils.Count > 0 ? ils : options).Take(3).ToList();
            var list = string.Join(", or ", offer.Select(o => o.Spoken));
            await _arbiter.SpeakAsync($"For runway {runwaySpoken}, {list}.", SpeechPriority.Normal)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Approach options failed");
        }
    }

    /// <summary>"04L" → "zero four left" (aviation digits + side word).</summary>
    public static string RunwaySpoken(string runway)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runway);
        var s = runway.Trim().ToUpperInvariant();
        if (s.StartsWith("RW", StringComparison.Ordinal))
        {
            s = s[2..];
        }

        var digits = new string(s.TakeWhile(char.IsAsciiDigit).ToArray());
        var side = s.SkipWhile(char.IsAsciiDigit).FirstOrDefault();
        var sideWord = side switch { 'L' => " left", 'R' => " right", 'C' => " center", _ => "" };
        return (digits.Length > 0 ? Callouts.Aviation.ToDigits(digits) : s) + sideWord;
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
        var ids = _procedures.Resolve(departure);
        var nav = ids.Airport is null
            ? NavDataFacts.None
            : _navData.Lookup(ids.Airport, ids.Runway, ids.Sid, ids.Star, ids.Approach);

        int? v1 = null, vr = null, v2 = null, flexTemp = null;
        if (departure)
        {
            v1 = PositiveOrNull(Read<int>("aircraft.fms.perf.takeOff.v1"));
            vr = PositiveOrNull(Read<int>("aircraft.fms.perf.takeOff.vr"));
            v2 = PositiveOrNull(Read<int>("aircraft.fms.perf.takeOff.v2"));
            flexTemp = PositiveOrNull(Read<int>("aircraft.fms.perf.takeOff.flexTemp"));
        }

        int? windDir = null, windSpeed = null, qnh = null, visibility = null, temperature = null;
        string? atisLetter = null, activeRunway = null;
        if (ids.Airport is not null)
        {
            var wx = await FetchWeatherAsync(ids.Airport).ConfigureAwait(false);
            windDir = wx.WindDirDeg;
            windSpeed = wx.WindSpeedKt;
            qnh = wx.QnhHpa is { } hpa ? (int)Math.Round(hpa) : null;
            visibility = wx.VisibilityMeters;
            temperature = wx.TemperatureC;
            atisLetter = wx.AtisLetter;
            activeRunway = wx.ActiveRunway;
        }

        return new BriefingFacts(
            departure, ids.Airport, ids.Runway, ids.Sid, ids.Star, ids.Approach, nav, v1, vr, v2,
            windDir, windSpeed, qnh, departure ? null : _minima.Current,
            atisLetter, activeRunway, flexTemp, visibility, temperature,
            AirportName: _airportNames?.SpokenName(ids.Airport));
    }

    private async Task<string> ComposeAsync(BriefingFacts facts)
    {
        var template = BriefingComposer.Template(facts);
        var options = _options.CurrentValue;
        if (!_llm.IsConfigured)
        {
            return template;
        }

        try
        {
            // Persona fragment (empty when the persona or its briefing toggle is off) colours
            // tone only — the composer's own prompt still locks the operational content.
            var personaFragment = _persona?.SystemPromptFragment(Persona.PersonaStyleCategory.Briefing) ?? "";
            var factBlock = BriefingComposer.FactBlock(facts);
            var narrative = await _llm.CompleteAsync(
                personaFragment + BriefingComposer.SystemPrompt(facts.IsDeparture),
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
            var retry = await _llm.CompleteAsync(
                personaFragment + BriefingComposer.SystemPrompt(facts.IsDeparture),
                factBlock + "\n\nUse ONLY these numbers, exactly as written, and no others: "
                    + NumberVerifier.DescribeAllowed(allowed)
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

    private async Task<WxFacts> FetchWeatherAsync(string icao)
    {
        try
        {
            // The composite provider (ActiveSky → gateway → SI cache) promises not to throw,
            // but a missing observation must never take the whole briefing down either way.
            return await _weather.GetAsync(icao).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Weather fetch failed for {Icao}", icao);
            return WxFacts.None;
        }
    }

    /// <summary>Minimal METAR body parse: wind dddss(Ggg)KT + QNH (Q hPa or A inHg). Superseded
    /// by <see cref="MetarParser"/> for the briefing itself; kept public because callers/tests
    /// may still rely on the historical two-field behaviour.</summary>
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

    /// <summary>Historic entry point over <see cref="FmsPlanParser"/> (element-first parse with
    /// attribute fallback) — kept for callers/tests of the original tuple shape.</summary>
    public static (string? Origin, string? Destination, string? OriginRunway,
        string? DestinationRunway, string? Sid, string? Star) ParseFmsPlan(string? xml)
    {
        var plan = FmsPlanParser.Parse(xml);
        return (plan.Origin, plan.Destination, plan.OriginRunway, plan.DestinationRunway, plan.Sid, plan.Star);
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
}
