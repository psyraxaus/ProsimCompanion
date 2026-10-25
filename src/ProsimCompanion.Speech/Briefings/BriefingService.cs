using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Weather;
using ProsimCompanion.Core.VoiceReference;
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
    private readonly Core.Aircraft.ISimLocalTime? _localTime;

    // Streamed LLM speech (issue #147). Optional: hand-built instances (tests, tools)
    // without one simply keep the whole-reply path.
    private readonly StreamingNarrator? _narrator;

    // Tech-log consultation (2026-10-09). Optional: without the service (tests) the briefing
    // simply has no tech-log clause.
    private readonly Core.TechLog.ITechLogService? _techLog;
    private readonly IOptionsMonitor<TechLogOptions>? _techLogOptions;

    // Emergency brief (issue #180). Optional: the takeoff performance result fills FLEX/TOGA
    // and the V-speeds when the FMS PERF page is still empty; the threats store carries the
    // "any other threats?" question. Without either (tests) the briefing degrades to the facts
    // it has and asks nothing.
    private readonly TakeoffPerfStore? _takeoffPerf;
    private readonly ThreatsQuestionStore? _threats;
    // Wind limits (issue #182). Optional: without the options (tests) the briefing has no
    // wind-limit clause.
    private readonly IOptionsMonitor<SopOptions>? _sop;
    private readonly IOptionsMonitor<AircraftLimitsOptions>? _aircraftLimits;
    private readonly RunwayConditionStore? _runwayCondition;

    // The plan's NOTAMs for the threats clause (issue #189, wired at the merge). Optional: without
    // the store (tests) the threats carry no NOTAM lines.
    private readonly Core.Aircraft.Ofp.OfpStore? _ofp;

    /// <summary>The last departure briefing's emergency facts (issue #187 wire) — the EFATO drill
    /// reads the briefed engine-out routing from here. Null until a departure briefing with the
    /// emergency brief on has run this session.</summary>
    public DepartureEmergencyBrief? LastDepartureEmergency { get; private set; }

    // Spoken text (campaign #81): names when the DFD is present, NATO-spelled ICAO otherwise
    // — the fallback policy lives in the module, not here (issue #70).
    private readonly Core.Speech.ISpokenText _spokenText;
    private readonly Dictionary<string, IDataRefSubscription<int>> _reads = new(StringComparer.Ordinal);

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
        Core.Speech.ISpokenText spokenText,
        OpenAiChatClient? llm = null,
        Persona.PersonaService? persona = null,
        StreamingNarrator? narrator = null,
        Core.TechLog.ITechLogService? techLog = null,
        IOptionsMonitor<TechLogOptions>? techLogOptions = null,
        TakeoffPerfStore? takeoffPerf = null,
        ThreatsQuestionStore? threats = null,
        IOptionsMonitor<SopOptions>? sop = null,
        IOptionsMonitor<AircraftLimitsOptions>? aircraftLimits = null,
        RunwayConditionStore? runwayCondition = null,
        Core.Aircraft.Ofp.OfpStore? ofp = null,
        Core.Aircraft.ISimLocalTime? localTime = null)
    {
        _persona = persona;
        _narrator = narrator;
        _techLog = techLog;
        _techLogOptions = techLogOptions;
        _takeoffPerf = takeoffPerf;
        _threats = threats;
        _sop = sop;
        _aircraftLimits = aircraftLimits;
        _runwayCondition = runwayCondition;
        _ofp = ofp;
        _localTime = localTime;
        ArgumentNullException.ThrowIfNull(spokenText);
        _spokenText = spokenText;
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

    /// <summary>Roster declaration (see <see cref="VoiceFeatureDeclaration"/>): the briefings,
    /// after the MCDU trio; the full-briefing phrases keep precedence over the minima query.</summary>
    public static VoiceFeatureDeclaration Declared { get; } = new()
    {
        Precedence = 150,
        Gate = VoiceFeatureGate.AlwaysOn,
        Description = new DescribedVoiceReference("briefings", "voiceReference.group.briefings.title", VoiceReferenceTab.FirstOfficer, VoiceSpeaker.FirstOfficer,
        [
            // Issue #180: with the emergency brief on, the departure briefing ends in the FO's
            // "any other threats, captain?" — answered in free text or with "nothing else".
            new(["brief departure", "departure briefing", "brief the departure", "departure brief", "run departure brief",
                 "run the departure brief", "run the departure briefing", "do the departure briefing"],
                "voiceReference.group.briefings.departure", "Any other threats, captain?", [VoiceBadges.Dialogue]),
            new(["brief arrival", "arrival briefing", "brief the arrival", "arrival brief", "run arrival brief",
                 "run the arrival brief", "run the arrival briefing", "do the arrival briefing", "approach briefing"],
                "voiceReference.group.briefings.arrival", "Confirm the approach minimums.", [VoiceBadges.Dialogue]),
            new(["which approach", "approach options", "confirm approach", "what approach for landing"],
                "voiceReference.group.briefings.approaches"),
        ]),
    };

    VoiceFeatureDeclaration IVoiceFeature.Declaration => Declared;

    public bool Enabled => true;

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
            var tag = departure ? "briefing.departure" : "briefing.arrival";

            // Streamed path (issue #147): the model's sentences are verified and spoken as
            // they arrive, the template finishing whatever the model does not deliver.
            RecordEmergency(facts);
            if (_narrator is not null && _llm.IsConfigured && _options.CurrentValue.StreamLlm)
            {
                RecordRoute(facts, departure);
                var personaFragment = _persona?.SystemPromptFragment(Persona.PersonaStyleCategory.Briefing) ?? "";
                var result = await _narrator.RunAsync(new NarrationPlan(
                    tag,
                    personaFragment + BriefingComposer.SystemPrompt(facts.IsDeparture),
                    BriefingComposer.FactBlock(facts) + "\n\nWrite the spoken briefing now.",
                    BriefingComposer.AllowedNumbers(facts),
                    BriefingComposer.Sections(facts),
                    new SpeechRequest(
                        departure ? "Departure briefing" : "Arrival briefing", SpeechPriority.Normal, Tag: tag),
                    Epilogue: [])).ConfigureAwait(false);
                _eventLog.Record("briefing.spoken", new { departure, narrative = result.Text, streamed = true });
                AskForThreats(facts);
                return;
            }

            var narrative = await ComposeAsync(facts).ConfigureAwait(false);
            RecordRoute(facts, departure);

            _eventLog.Record("briefing.spoken", new { departure, narrative });
            // The outcome resolves when the utterance has played (or was dropped), so the
            // threats question below never talks over the briefing.
            await _arbiter.EnqueueAsync(new SpeechRequest(narrative, SpeechPriority.Normal, Tag: tag))
                .ConfigureAwait(false);
            AskForThreats(facts);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Briefing failed");
            _ = _arbiter.SpeakAsync("Unable to compose the briefing.", SpeechPriority.Normal);
        }
    }

    /// <summary>The <c>briefing.emergency</c> session event (issue #180): the facts the
    /// emergency section was built from and the threat items it spoke — what the probe
    /// <c>briefing-takeoff-emergency</c> reads. Nothing recorded while the switch is off.</summary>
    private void RecordEmergency(BriefingFacts facts)
    {
        if (facts.Emergency is not { } e)
        {
            return;
        }

        _eventLog.Record("briefing.emergency", new
        {
            airport = facts.Airport,
            runway = facts.Runway,
            surface = e.Surface.ToString().ToLowerInvariant(),
            thunderstorms = e.Thunderstorms,
            msaFt = e.MsaFt,
            accelerationAltitudeFt = e.AccelerationAltitudeFt,
            thrustReductionAltitudeFt = e.ThrustReductionAltitudeFt,
            altitudesAboveAirport = e.AltitudesAboveAirport,
            engineOut = e.EngineOutProcedure,
            engineOutPublished = e.EngineOutIsPublished,
            returnRunway = e.ReturnRunway,
            thrust = e.ThrustMode,
            ceilingFt = e.CeilingFt,
            icingLikely = e.IcingLikely,
            v1 = facts.V1,
            vr = facts.Vr,
            v2 = facts.V2,
            threats = ThreatListBuilder.Build(facts, e),
        });
    }

    /// <summary>Raises "any other threats, captain?" after a departure briefing that spoke the
    /// emergency section (issue #180, <c>briefing.askForThreats</c>). The shared dialogue voices
    /// it and takes the answer; the store's TTL settles silence.</summary>
    private void AskForThreats(BriefingFacts facts)
    {
        if (_threats is null || facts.Emergency is null || !facts.IsDeparture || !_options.CurrentValue.AskForThreats)
        {
            return;
        }

        if (!_threats.Ask())
        {
            _logger.LogDebug("Threats question not raised — one already stands");
        }
    }

    /// <summary>Route breadcrumb for the logbook/debrief extractor: the briefing is the one
    /// place the resolved airport + runway exist as facts (Prosim2FO emitted flight.route
    /// from the same spot). A flight flown without a briefing simply has no route on record.</summary>
    private void RecordRoute(BriefingFacts facts, bool departure)
    {
        if (!string.IsNullOrWhiteSpace(facts.Airport))
        {
            _eventLog.Record("flight.route", new
            {
                role = departure ? "departure" : "arrival",
                airport = facts.Airport,
                runway = facts.Runway,
            });
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

    /// <summary>"04L" → "zero four left". Pronunciation is the shared spoken-text module's
    /// (campaign #81); a designator with no digits passes through raw.</summary>
    public static string RunwaySpoken(string runway)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runway);
        return Core.Speech.SpokenText.RunwayOrNull(runway) ?? runway.Trim().ToUpperInvariant();
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
            v1 = PositiveOrNull(Read(ProsimDataRefNames.FmsPerfTakeoffV1));
            vr = PositiveOrNull(Read(ProsimDataRefNames.FmsPerfTakeoffVr));
            v2 = PositiveOrNull(Read(ProsimDataRefNames.FmsPerfTakeoffV2));
            flexTemp = PositiveOrNull(Read(ProsimDataRefNames.FmsPerfTakeoffFlexTemp));
        }

        // Issue #180: the takeoff performance result stands in for an FMS PERF page that is
        // still empty (the gross-error check compares the two later; the briefing just needs
        // figures to speak).
        var perf = departure ? _takeoffPerf?.Snapshot() : null;
        if (departure && perf is not null && v1 is null && vr is null && v2 is null)
        {
            v1 = perf.V1;
            vr = perf.Vr;
            v2 = perf.V2;
        }

        int? windDir = null, windSpeed = null, qnh = null, visibility = null, temperature = null;
        string? atisLetter = null, activeRunway = null;
        var wx = WxFacts.None;
        if (ids.Airport is not null)
        {
            wx = await FetchWeatherAsync(ids.Airport).ConfigureAwait(false);
            windDir = wx.WindDirDeg;
            windSpeed = wx.WindSpeedKt;
            qnh = wx.QnhHpa is { } hpa ? (int)Math.Round(hpa) : null;
            visibility = wx.VisibilityMeters;
            temperature = wx.TemperatureC;
            atisLetter = wx.AtisLetter;
            activeRunway = wx.ActiveRunway;
        }

        var options = _options.CurrentValue;
        return new BriefingFacts(
            departure, ids.Airport, ids.Runway, ids.Sid, ids.Star, ids.Approach, nav, v1, vr, v2,
            windDir, windSpeed, qnh, departure ? null : _minima.Current,
            atisLetter, activeRunway, flexTemp ?? perf?.FlexTempC, visibility, temperature,
            AirportName: _spokenText.Airport(ids.Airport),
            TechLogItems: ConsultTechLog(departure),
            Emergency: departure && options.TakeoffEmergencyBrief ? BuildEmergencyFacts(ids, nav, wx, flexTemp, perf, options) : null,
            GoAround: !departure && options.ArrivalGoAroundBrief ? BuildGoAroundFacts(ids) : null,
            WindLimits: WindLimitsFor(departure, nav, wx),
            TimeOfDay: _localTime?.LocalTimeOfDay is { } localTime ? Core.Aircraft.TimeOfDayGreeting.Word(localTime) : null);
    }

    /// <summary>The emergency half's facts (issue #180). Every source is optional: no DFD = no
    /// MSA and the straight-ahead engine-out default, no elevation = AAL altitudes spoken as
    /// "above the airport", no weather = unknown surface and no weather threats. Pure apart
    /// from the two DFD reads, so the figures can be reasoned about from the event payload.</summary>
    private EmergencyBriefFacts BuildEmergencyFacts(
        ProcedureIdentifiers ids, NavDataFacts nav, WxFacts wx, int? fmsFlex, TakeoffPerfSnapshot? perf, BriefingOptions options)
    {
        var elevation = nav.RunwayElevationFt;
        var eosid = _navData.EngineOutProcedure(ids.Airport, ids.Runway);
        var thrust = fmsFlex is { } flex ? $"FLEX {flex}"
            : perf is null ? null
            : perf.FlexTempC is { } perfFlex ? $"FLEX {perfFlex}" : "TOGA";
        var notamThreats = NotamThreats.Lines(_ofp?.Current?.Notams ?? [], ids.Airport);
        var emergency = new EmergencyBriefFacts(
            Surface: MetarConditions.Surface(wx),
            Thunderstorms: wx.Precip == PrecipKind.Thunderstorm,
            MsaFt: _navData.AirportMsaFt(ids.Airport),
            AccelerationAltitudeFt: BriefedAltitude(options.AccelerationAltitudeFt, elevation),
            ThrustReductionAltitudeFt: BriefedAltitude(options.ThrustReductionAltitudeFt, elevation),
            AltitudesAboveAirport: elevation is null,
            EngineOutProcedure: eosid.Count == 0 ? EmergencyBriefFacts.DefaultEngineOutProcedure : BriefingComposer.MissedApproachLegsText(eosid),
            EngineOutIsPublished: eosid.Count > 0,
            // No landing-runway fact exists for the departure airport yet: a return is briefed
            // on the departure runway, which is what most crews say when nothing else is known.
            ReturnRunway: ids.Runway,
            ThrustMode: thrust,
            CeilingFt: wx.CeilingFt,
            IcingLikely: MetarConditions.GroundIcingLikely(wx),
            AirportElevationFt: elevation,
            NotamThreats: notamThreats);
        LastDepartureEmergency = new DepartureEmergencyBrief(ids.Airport, ids.Runway, emergency, DateTimeOffset.UtcNow);
        return emergency;
    }

    /// <summary>An AAL figure made MSL when the elevation is known, rounded to the nearest 100
    /// ft the way crews brief it (21 ft + 1500 → 1500, 1340 ft + 1500 → 2800); the AAL figure
    /// itself when it is not.</summary>
    public static int BriefedAltitude(int aboveAirportFt, double? airportElevationFt)
        => airportElevationFt is { } elevation
            ? (int)(Math.Round((elevation + aboveAirportFt) / 100.0) * 100)
            : aboveAirportFt;

    /// <summary>The go-around additions' facts (issue #180): the published missed approach's
    /// highest altitude and legs, and the destination MSA.</summary>
    private GoAroundBriefFacts BuildGoAroundFacts(ProcedureIdentifiers ids)
    {
        var missed = _navData.MissedApproach(ids.Airport, ids.Approach);
        var altitude = missed.Legs.Select(l => l.AltitudeFt).Where(a => a is > 0).Max();
        return new GoAroundBriefFacts(altitude, _navData.AirportMsaFt(ids.Airport), [.. missed.Legs.Select(l => l.Phrase)]);
    }

    /// <summary>The wind against the aircraft limits for the briefed runway (issue #182): with
    /// the wind-limits feature on, a runway true heading from the DFD and a wind in the
    /// observation. The landing runway condition applies to the arrival only.</summary>
    private WindLimitVerdict? WindLimitsFor(bool departure, NavDataFacts nav, WxFacts wx)
    {
        if (_sop?.CurrentValue.Monitoring.WindLimits.Enabled != true || _aircraftLimits is null
            || nav.RunwayTrueHeading is not { } heading
            || WindMath.Resolve(wx.WindDirDeg, wx.WindSpeedKt, wx.WindGustKt, heading) is not { } wind)
        {
            return null;
        }

        return WindLimitCheck.Evaluate(
            wind, _aircraftLimits.CurrentValue,
            departure ? WindPhase.Takeoff : WindPhase.Landing,
            departure ? null : _runwayCondition?.Current);
    }

    /// <summary>The open tech-log items this briefing names (2026-10-09): all of them for the
    /// departure, the landing-relevant ones for the arrival. Null when the tech log is off,
    /// the switch is off, or nothing is open — the template then has no clause at all. Records
    /// <c>techlog.consulted</c> when items were found, so the flight report can see the briefing
    /// read the log.</summary>
    private IReadOnlyList<Core.TechLog.TechLogDefect>? ConsultTechLog(bool departure)
    {
        if (_techLog is not { IsEnabled: true } techLog
            || _techLogOptions?.CurrentValue.BriefOpenItemsInBriefings != true)
        {
            return null;
        }

        try
        {
            var open = techLog.OpenDefects;
            var items = departure
                ? Core.TechLog.TechLogConsultation.DepartureItems(open)
                : Core.TechLog.TechLogConsultation.ArrivalItems(open);
            if (items.Count == 0)
            {
                return null;
            }

            _eventLog.Record("techlog.consulted", new
            {
                source = departure ? "departure-briefing" : "arrival-briefing",
                open = open.Count,
                items = items.Select(d => d.Title).ToArray(),
            });
            return items;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tech-log consultation failed — briefing without it");
            return null;
        }
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

    /// <summary>Null when no value has arrived yet — <see cref="PositiveOrNull"/> would treat
    /// the descriptors' 0 fallback ("not entered") the same way, but the distinction keeps
    /// the historical "no data" shape intact.</summary>
    private int? Read(DataRef<int> dataref)
    {
        if (!_reads.TryGetValue(dataref.Name, out var read))
        {
            read = _dataRefs.Subscribe(dataref);
            _reads[dataref.Name] = read;
        }

        return read.RawValue is null ? null : read.Value;
    }

    private static int? PositiveOrNull(int? value) => value is > 0 ? value : null;
}
