using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Airports;
using ProsimCompanion.Core.Checklists;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.VoiceReference;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.SayIntentions;

/// <summary>One data-driven ATC request from config/atc-requests.json.</summary>
public sealed class AtcRequestDefinition
{
    public List<string> Phrases { get; set; } = [];
    public string Station { get; set; } = "";
    public string CommType { get; set; } = "";
    public bool IsDeparture { get; set; }
    public string Icao { get; set; } = "";
    public string Faa { get; set; } = "";
}

/// <summary>
/// SayIntentions integration (Prosim2FO semantics): flight.json polled at 1 Hz for the active
/// flight context, ATC requests transmitted via GET sayAs on COM1 (255-char cap), optional
/// getWX comms lookup for auto-tune via setFreq, and who works the radios:
/// <list type="bullet">
/// <item>the comms hand-over by flight phase (issue #171, <c>sayIntentions.commsHandover.*</c>,
/// off by default) — <see cref="CommsHandoverCore"/> decides from the app's own phase edges,
/// its runway geometry and ATC's hand-offs; this shell writes <c>SIAI_COPILOT</c>, speaks the
/// one FO line and records <c>sayintentions.comms</c>;</item>
/// <item>the legacy departure gate (<c>departureGatingEnabled</c>): a new active flight hands
/// comms to the SI copilot (SIAI_COPILOT=1); flight.json's <c>distance_to_runway</c> ≤ 0.3 nm
/// takes them back, tunes Tower, announces, and the takeoff request restores them. Ignored
/// while the hand-over is on.</item>
/// </list>
/// The SIAI L:var radio-clear gate (<see cref="RadioClearGate"/>, 2026-10-08) holds a
/// transmission while COM1 is busy; without a sim connection it is the predecessor's fixed
/// 400 ms settle. Request text comes from <see cref="AtcRequestTemplate"/> (issue #140): a
/// stand SayIntentions has not named yet falls back to the stand GSX loaded, and a clause
/// with no value is dropped whole.
/// </summary>
public sealed class SayIntentionsService : IVoiceFeature, Core.Hosting.IStartupModule, IDisposable
{
    private const string ApiBase = "https://apipri.sayintentions.ai/sapi";
    private const int ReadoutMaxChars = 220;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private static readonly string[] CaptainOverridePhrases =
        ["i have the radios", "i have comms", "my radios"];

    private static readonly string[] CopilotOverridePhrases =
        ["you have the radios", "you have comms", "your radios"];

    private readonly IOptionsMonitor<SayIntentionsOptions> _options;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<SayIntentionsService> _logger;
    private readonly OfpStore? _ofp; // issue #184: the {diversion} template value
    private readonly Core.State.ConfigProblemStore? _configProblems;
    private readonly IFlightPhaseSource? _phase;
    private readonly RunwayLocator? _runways;
    private readonly IDepartureRunwaySource? _departureRunway;
    private readonly PushbackChoiceStore? _pushback;
    private readonly GsxDiagnosticsStore? _gsx;
    private readonly SayIntentionsFrequencyMonitor? _frequencyMonitor;

    /// <summary>The FO working the radios (owner request 2026-10-25): while its switch is on the
    /// hand-over by phase, the legacy gate and the readouts here are inert and SIAI_COPILOT is
    /// always written 0; the voice override flips its owner instead.</summary>
    private readonly FoRadiosService? _foRadios;
    private readonly StepClimbStore? _stepClimb; // {stepLevel} for "request climb" (issue #189)
    private readonly RadioClearGate _radioGate;
    private readonly CommsHandoverCore _handover = new();
    private readonly RunwayLookupCache _runwayCache = new();
    private readonly object _gate = new();

    private List<AtcRequestDefinition> _requests = [];
    private Timer? _timer;
    private FlightContext _flight = FlightContext.Empty;
    private bool _wasActive;
    private DepartureState _departure = DepartureState.Idle;
    private RunwayGeometry? _departureThreshold;
    private int _runwayLookupBusy;

    private enum DepartureState
    {
        Idle,
        CommsTakenBack,
        Requested,
        Restored,
    }

    private sealed record FlightContext(
        bool IsActive,
        string? ApiKey,
        string? Callsign,
        string? Gate,
        string? DepartureRunway,
        string? Airport,
        string? Origin,
        double DistanceToRunway)
    {
        public static FlightContext Empty { get; } =
            new(false, null, null, null, null, null, null, double.MaxValue);
    }

    /// <summary>Optional <paramref name="configProblems"/>: a malformed atc-requests.json
    /// surfaces on the web UI (issue #74) instead of only a log warning. Optional
    /// <paramref name="simVars"/>: the SIAI L:vars for the radio-clear gate; null = the fixed
    /// 400 ms settle. The remaining optional collaborators serve the comms hand-over (issue
    /// #171: phase edges, runway geometry) and the stand fallback (issue #140: the stand GSX
    /// loaded, via the pushback advisor's store or the GSX gate context); each is null in a
    /// composition that lacks it and the feature degrades to "unknown".</summary>
    public SayIntentionsService(
        IOptionsMonitor<SayIntentionsOptions> options,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        ILogger<SayIntentionsService> logger,
        Core.State.ConfigProblemStore? configProblems = null,
        Core.Aircraft.ISimVars? simVars = null,
        IFlightPhaseSource? phase = null,
        RunwayLocator? runways = null,
        IDepartureRunwaySource? departureRunway = null,
        PushbackChoiceStore? pushback = null,
        GsxDiagnosticsStore? gsx = null,
        SayIntentionsFrequencyMonitor? frequencyMonitor = null,
        OfpStore? ofp = null,
        StepClimbStore? stepClimb = null,
        FoRadiosService? foRadios = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _ofp = ofp;
        _options = options;
        _arbiter = arbiter;
        _eventLog = eventLog;
        _logger = logger;
        _configProblems = configProblems;
        _phase = phase;
        _runways = runways;
        _departureRunway = departureRunway;
        _pushback = pushback;
        _gsx = gsx;
        _frequencyMonitor = frequencyMonitor;
        _foRadios = foRadios;
        _stepClimb = stepClimb;
        _radioGate = new RadioClearGate(simVars, logger);
    }

    /// <summary>Roster declaration (see <see cref="VoiceFeatureDeclaration"/>): the ATC requests,
    /// composed from atc-requests.json, after the company features and before ground control.</summary>
    public static VoiceFeatureDeclaration Declared { get; } = new()
    {
        Precedence = 200,
        Gate = VoiceFeatureGate.ByOption("sayIntentions.enabled", VoiceReferenceTextIds.OffSayIntentions),
        Description = new ComposedVoiceReference(AtcVoiceReference.Compose),
    };

    VoiceFeatureDeclaration IVoiceFeature.Declaration => Declared;

    public bool Enabled => _options.CurrentValue.Enabled;

    /// <summary>flight.json's <c>assigned_gate</c> as read on every 1 Hz poll (null when
    /// SayIntentions has no active flight). Consumed by
    /// <see cref="SayIntentionsArrivalGateSource"/>; timer thread.</summary>
    public event Action<string?>? AssignedGateObserved;

    public IEnumerable<string> Phrases
    {
        get
        {
            var phrases = _requests.SelectMany(r => r.Phrases);
            var handover = _options.CurrentValue.CommsHandover;
            return handover.Enabled && handover.VoiceOverride
                ? phrases.Concat(CaptainOverridePhrases).Concat(CopilotOverridePhrases)
                : phrases;
        }
    }

    /// <summary>The loaded request phrasebook (atc-requests.json) — the Voice Reference drawer
    /// (issue #136) shows each request with its station and transmission.</summary>
    public IReadOnlyList<AtcRequestDefinition> Requests => _requests;

    /// <summary>Who has the radios as far as the hand-over knows (status surfaces).</summary>
    public CommsOwner CommsOwner
    {
        get
        {
            lock (_gate)
            {
                return _handover.Owner;
            }
        }
    }

    public bool ValueParse => false;

    public void Start()
    {
        LoadRequests();
        if (_phase is not null)
        {
            _phase.PhaseChanged += OnPhaseChanged;
        }

        if (_frequencyMonitor is not null)
        {
            _frequencyMonitor.AtcTransmissionObserved += OnAtcTransmission;
        }

        _timer = new Timer(_ => PollFlightJson(), null, 1000, 1000);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        if (_phase is not null)
        {
            _phase.PhaseChanged -= OnPhaseChanged;
        }

        if (_frequencyMonitor is not null)
        {
            _frequencyMonitor.AtcTransmissionObserved -= OnAtcTransmission;
        }

        _radioGate.Dispose();
    }

    public bool TryHandle(string utterance)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled)
        {
            return false;
        }

        var text = CommandMatcher.Normalize(utterance);
        if (options.CommsHandover is { Enabled: true, VoiceOverride: true } && TryHandleOverride(text, options))
        {
            return true;
        }

        var request = _requests.FirstOrDefault(r =>
            r.Phrases.Any(p => CommandMatcher.Normalize(p).Equals(text, StringComparison.Ordinal)));
        if (request is null)
        {
            return false;
        }

        FlightContext flight;
        lock (_gate)
        {
            flight = _flight;
        }

        if (!flight.IsActive)
        {
            _ = _arbiter.SpeakAsync("SayIntentions has no active flight.", SpeechPriority.Normal);
            return true;
        }

        // Positive acknowledgement BEFORE any network work (issue #52): the silent-clearance
        // defect was exactly this gap — the request transmitted with no FO feedback at all.
        _ = _arbiter.EnqueueAsync(new SpeechRequest(
            PickAcknowledgement(request.Station),
            SpeechPriority.Normal,
            Ttl: TimeSpan.FromSeconds(20),
            Tag: "sayintentions.ack"));

        _ = TransmitRequestAsync(request, flight, options);
        return true;
    }

    /// <summary>"I have the radios" / "you have the radios": the pilot flips the owner.</summary>
    private bool TryHandleOverride(string normalized, SayIntentionsOptions options)
    {
        CommsOwner wanted;
        if (CaptainOverridePhrases.Any(p => CommandMatcher.Normalize(p).Equals(normalized, StringComparison.Ordinal)))
        {
            wanted = CommsOwner.Captain;
        }
        else if (CopilotOverridePhrases.Any(p => CommandMatcher.Normalize(p).Equals(normalized, StringComparison.Ordinal)))
        {
            wanted = CommsOwner.Copilot;
        }
        else
        {
            return false;
        }

        if (options.FoRadios.Enabled && _foRadios is not null)
        {
            // The FO works the radios: the override parks or wakes the FO; the SI copilot stays off.
            var captain = wanted == CommsOwner.Captain;
            if (_foRadios.CaptainOwns == captain)
            {
                _ = _arbiter.SpeakAsync(captain ? "The radios are already yours." : "I already have the radios.", SpeechPriority.Normal);
                return true;
            }

            _foRadios.SetCaptainOwns(captain);
            _ = _arbiter.SpeakAsync(captain ? "Your radios, Captain." : "I have the radios.", SpeechPriority.Normal);
            return true;
        }

        FlightContext flight;
        CommsHandoverDecision? decision;
        lock (_gate)
        {
            flight = _flight;
            decision = _handover.Override(wanted);
        }

        if (!flight.IsActive)
        {
            _ = _arbiter.SpeakAsync("SayIntentions has no active flight.", SpeechPriority.Normal);
            return true;
        }

        if (decision is null)
        {
            _ = _arbiter.SpeakAsync(
                wanted == CommsOwner.Captain ? "The radios are already yours." : "I already have the radios.",
                SpeechPriority.Normal);
            return true;
        }

        _ = ApplyHandoverAsync(decision, flight, options);
        return true;
    }

    /// <summary>Rotating "Roger — calling {station}" so repeated requests don't sound like a
    /// recording. Deterministic enough not to matter; variety is the whole point.</summary>
    private static string PickAcknowledgement(string station)
    {
        var name = string.IsNullOrWhiteSpace(station) ? "them" : station;
        string[] variants =
        [
            $"Roger — calling {name}.",
            $"Roger, I'll call {name}.",
            $"Copied — calling {name} now.",
        ];
        return variants[Random.Shared.Next(variants.Length)];
    }

    private async Task TransmitRequestAsync(
        AtcRequestDefinition request, FlightContext flight, SayIntentionsOptions options)
    {
        try
        {
            var apiKey = ResolveApiKey(flight, options);
            if (apiKey is null)
            {
                _logger.LogWarning("SayIntentions request skipped — no API key");
                _ = _arbiter.SpeakAsync(
                    "I can't reach SayIntentions — no API key.", SpeechPriority.Normal);
                return;
            }

            if (options.AutoTuneFrequency && request.CommType.Length > 0)
            {
                await TryAutoTuneAsync(apiKey, flight, request.CommType).ConfigureAwait(false);
            }

            // Radio-clear gate: hold while ATC is talking on COM1 (SIAI L:vars), bounded.
            var (radioWait, radioBusy) = await _radioGate.WaitForClearAsync().ConfigureAwait(false);
            var (values, standFrom) = Values(flight);
            var message = AtcRequestTemplate.Fill(PickTemplate(request, options), values);

            // The audible FO→ATC call (issue #52): the FO voices the exact transmission text
            // locally, awaited so it finishes before sayAs makes ATC react. Off when
            // SayIntentions itself voices sayAs audibly (live-verify) — double audio.
            if (options.FoSpeaksTransmission)
            {
                await _arbiter.EnqueueAsync(new SpeechRequest(
                    message,
                    SpeechPriority.Normal,
                    Ttl: TimeSpan.FromSeconds(30),
                    Tag: "sayintentions.tx")).ConfigureAwait(false);
            }

            await SayAsAsync(apiKey, message).ConfigureAwait(false);
            _eventLog.Record("sayintentions.request", new
            {
                station = request.Station,
                message,
                radioWaitMs = (int)radioWait.TotalMilliseconds,
                radioBusyAtTx = radioBusy,
                standFrom,
            });

            // Legacy gate only: the takeoff request hands the radios back so the SI copilot
            // reads the clearance back. Under the hand-over the captain keeps them through
            // the takeoff clearance and the first departure hand-off (issue #171, step d).
            if (request.IsDeparture && !options.CommsHandover.Enabled)
            {
                lock (_gate)
                {
                    _departure = DepartureState.Requested;
                }

                await SetVarAsync(apiKey, "SIAI_COPILOT", "1").ConfigureAwait(false);
                lock (_gate)
                {
                    _departure = DepartureState.Restored;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SayIntentions request failed");
            _ = _arbiter.SpeakAsync(
                "The SayIntentions request failed — see the log.", SpeechPriority.Normal);
        }
    }

    /// <summary>The template values for <paramref name="flight"/>, with the stand fallback
    /// (issue #140): SayIntentions' <c>assigned_gate</c>, else the stand GSX loaded — only on
    /// the ground before the takeoff roll, where every request that names a stand is made.</summary>
    private (AtcRequestValues Values, string StandFrom) Values(FlightContext flight)
    {
        var gate = flight.Gate?.Trim();
        var standFrom = "none";
        if (!string.IsNullOrWhiteSpace(gate))
        {
            standFrom = "sayintentions";
        }
        else if (GsxStandName() is { } gsxStand)
        {
            gate = gsxStand;
            standFrom = "gsx";
        }

        // Issue #184: the diversion destination while one overlays the OFP; blank otherwise, so
        // the "request diversion to {diversion}" entry is dropped whole without one.
        var diversion = _ofp is { IsDiverted: true } ? _ofp.Current?.DestinationIcao : null;
        var stepLevel = _stepClimb?.Snapshot().NextFlightLevel is { } fl ? fl.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        return (new AtcRequestValues(flight.Callsign, gate, flight.DepartureRunway, flight.Airport, flight.Origin, diversion, stepLevel), standFrom);
    }

    /// <summary>The departure stand as GSX knows it: the pushback advisor's resolved stand
    /// ("Gate 208", the parking catalogue's display name), else the GSX gate context's last
    /// segment ("Terminal 5B (531-548)|Stand 546R" → "Stand 546R"). Null after the takeoff
    /// roll — the arrival stand is a different question.</summary>
    private string? GsxStandName()
    {
        var phase = _phase?.CurrentPhase ?? FlightPhase.Unknown;
        if (!(phase.IsBeforeTaxiOut() || phase is FlightPhase.TaxiOut or FlightPhase.Unknown))
        {
            return null;
        }

        var stand = _pushback?.Snapshot().Stand;
        if (!string.IsNullOrWhiteSpace(stand))
        {
            return stand.Trim();
        }

        var context = _gsx?.Snapshot().GateContextKey;
        if (string.IsNullOrWhiteSpace(context))
        {
            return null;
        }

        var last = context.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        return string.IsNullOrWhiteSpace(last) ? null : last;
    }

    private void PollFlightJson()
    {
        try
        {
            var options = _options.CurrentValue;
            if (!options.Enabled)
            {
                return;
            }

            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SayIntentionsAI", "flight.json");
            var flight = ReadFlightJson(path);
            var handover = options.CommsHandover;
            var legacyGate = options.DepartureGatingEnabled && !handover.Enabled && !options.FoRadios.Enabled;

            bool becameActive;
            bool gateTripped;
            var needRunway = false;
            CommsHandoverDecision? decision = null;
            lock (_gate)
            {
                becameActive = flight.IsActive && !_wasActive;
                if (!flight.IsActive)
                {
                    _departure = DepartureState.Idle;
                    if (_wasActive)
                    {
                        _handover.Reset();
                    }
                }

                gateTripped = flight.IsActive
                    && _departure == DepartureState.Idle
                    && legacyGate
                    && flight.DistanceToRunway <= options.DepartureGateDistanceNm;
                if (gateTripped)
                {
                    _departure = DepartureState.CommsTakenBack;
                }

                _wasActive = flight.IsActive;
                _flight = flight;

                if (handover.Enabled && flight.IsActive)
                {
                    var rules = CommsHandoverRules.From(handover);
                    if (becameActive)
                    {
                        decision = _handover.OnFlightActive(_phase?.CurrentPhase ?? FlightPhase.Unknown, rules);
                    }

                    needRunway = _handover.Phase == FlightPhase.TaxiOut && _departureThreshold is null;
                    decision ??= _handover.OnDistanceToRunway(DistanceToDepartureRunwayNm(), rules);
                }
            }

            if (needRunway)
            {
                _ = PrimeDepartureRunwayAsync();
            }

            // Every poll, raw (null while no flight): the arrival-gate source dedupes and
            // phase-gates on its side. Raised on the timer thread.
            AssignedGateObserved?.Invoke(flight.IsActive ? flight.Gate : null);

            if (becameActive && legacyGate)
            {
                // The SI copilot owns comms on the ground and handles readbacks.
                _ = WithApiKey(flight, options, key => SetVarAsync(key, "SIAI_COPILOT", "1"));
            }

            if (gateTripped)
            {
                _ = TakeCommsForDepartureAsync(flight, options);
            }

            if (decision is not null)
            {
                _ = ApplyHandoverAsync(decision, flight, options);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "flight.json poll failed");
        }
    }

    // ---- Comms hand-over (issue #171) ----

    private void OnPhaseChanged(object? sender, FlightPhaseChangedEventArgs e)
    {
        try
        {
            var options = _options.CurrentValue;
            if (!options.Enabled || !options.CommsHandover.Enabled)
            {
                return;
            }

            if (e.Current == FlightPhase.TaxiOut)
            {
                _ = PrimeDepartureRunwayAsync();
            }

            FlightContext flight;
            CommsHandoverDecision? decision;
            lock (_gate)
            {
                flight = _flight;
                decision = _handover.OnPhaseChanged(e.Current, CommsHandoverRules.From(options.CommsHandover));
            }

            if (decision is null)
            {
                return;
            }

            if (!flight.IsActive)
            {
                _logger.LogDebug("Comms hand-over {Trigger} skipped — SayIntentions has no active flight", decision.Trigger);
                return;
            }

            _ = ApplyHandoverAsync(decision, flight, options);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Comms hand-over on phase change failed");
        }
    }

    private void OnAtcTransmission(SayIntentionsFrequencyMonitor.AtcTransmission tx)
    {
        try
        {
            var options = _options.CurrentValue;
            var handover = options.CommsHandover;
            if (!options.Enabled || !handover.Enabled || options.FoRadios.Enabled)
            {
                return;
            }

            FlightContext flight;
            CommsHandoverDecision? decision = null;
            CommsOwner owner;
            FlightPhase phase;
            lock (_gate)
            {
                flight = _flight;
                owner = _handover.Owner;
                phase = _handover.Phase;
                if (tx.Handoff is { } handoff && handoff.Station.Contains("tower", StringComparison.OrdinalIgnoreCase))
                {
                    decision = _handover.OnTowerHandoff(CommsHandoverRules.From(handover));
                }
            }

            if (decision is not null && flight.IsActive)
            {
                _ = ApplyHandoverAsync(decision, flight, options);
                return;
            }

            // The captain heard ATC himself; the FO relays only while the copilot has the radios.
            if (owner != CommsOwner.Copilot)
            {
                return;
            }

            if (tx.Handoff is { } assigned)
            {
                if (!handover.AnnounceHandoffs)
                {
                    return;
                }

                var frequency = Callouts.Aviation.ToDigits(
                    (assigned.Khz / 1000.0).ToString("F3", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.'));
                var station = SayIntentionsFrequencyMonitor.SpeakableStation(assigned.Station);
                Readout("handoff", $"ATC handed us to {station}, {frequency}.", tx.Text);
                return;
            }

            if (handover.ReadTaxiClearance
                && phase is FlightPhase.PushbackAndStart or FlightPhase.TaxiOut
                && tx.Text.Contains("taxi", StringComparison.OrdinalIgnoreCase))
            {
                var text = tx.Text.Length > ReadoutMaxChars ? tx.Text[..ReadoutMaxChars].TrimEnd() + "…" : tx.Text;
                Readout("taxi", $"Taxi clearance: {text}", tx.Text);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ATC transmission readout failed");
        }
    }

    private void Readout(string kind, string spoken, string source)
    {
        _eventLog.Record("sayintentions.atc-readout", new { kind, text = spoken, source });
        _ = _arbiter.EnqueueAsync(new SpeechRequest(
            spoken, SpeechPriority.Normal, Ttl: TimeSpan.FromSeconds(45), Tag: "sayintentions.readout"));
    }

    /// <summary>One hand-over: the setVar write, Tower at the holding point, the FO line and
    /// the <c>sayintentions.comms</c> event. Failures log and leave the core's owner as decided
    /// — the next edge corrects it.</summary>
    private async Task ApplyHandoverAsync(CommsHandoverDecision decision, FlightContext flight, SayIntentionsOptions options)
    {
        if (options.FoRadios.Enabled)
        {
            _logger.LogDebug("Comms hand-over {Trigger} skipped — the FO works the radios", decision.Trigger);
            return;
        }

        var handover = options.CommsHandover;
        var line = decision.Owner == CommsOwner.Copilot
            ? handover.CopilotLine
            : decision.Trigger switch
            {
                "holding-point" => handover.HoldingPointLine,
                "approach" or "tower-handoff" => handover.ApproachLine,
                _ => handover.CaptainLine,
            };

        _logger.LogInformation(
            "Comms hand-over: {Owner} has the radios ({Trigger}, {Phase})",
            decision.Owner, decision.Trigger, decision.Phase);
        _eventLog.Record("sayintentions.comms", new
        {
            owner = decision.Owner.ToString().ToLowerInvariant(),
            trigger = decision.Trigger,
            phase = decision.Phase.ToString(),
            text = line,
        });

        try
        {
            var apiKey = ResolveApiKey(flight, options);
            if (apiKey is null)
            {
                _logger.LogWarning("Comms hand-over {Trigger} not sent — no API key", decision.Trigger);
                return;
            }

            await SetVarAsync(apiKey, "SIAI_COPILOT", decision.Owner == CommsOwner.Copilot ? "1" : "0").ConfigureAwait(false);
            if (decision.Trigger == "holding-point" && handover.TuneTowerAtHoldingPoint)
            {
                await TuneTowerAsync(apiKey, flight).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(line))
            {
                await _arbiter.EnqueueAsync(new SpeechRequest(
                    line.Trim(), SpeechPriority.Normal, Ttl: TimeSpan.FromSeconds(45), Tag: "sayintentions.comms")).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Comms hand-over {Trigger} failed", decision.Trigger);
        }
    }

    /// <summary>Tower on COM1: ATC's last hand-off when it named a Tower, else the airport's
    /// comms list (getWX). Nothing known = nothing tuned.</summary>
    private async Task TuneTowerAsync(string apiKey, FlightContext flight)
    {
        var assigned = _frequencyMonitor?.CurrentAssignment;
        if (assigned is not null && assigned.Station.Contains("tower", StringComparison.OrdinalIgnoreCase))
        {
            await SetFreqAsync(apiKey, (assigned.Khz / 1000.0).ToString("F3", CultureInfo.InvariantCulture)).ConfigureAwait(false);
            return;
        }

        await TryAutoTuneAsync(apiKey, flight, "TOWER").ConfigureAwait(false);
    }

    /// <summary>Resolves the departure runway threshold once per (airport, runway) — the FMS
    /// runway via the briefing procedure source when the Speech pillar has it, else
    /// flight.json's — through the same gateway/DFD tiers as the pushback advisor.</summary>
    private async Task PrimeDepartureRunwayAsync()
    {
        if (_runways is null || Interlocked.Exchange(ref _runwayLookupBusy, 1) == 1)
        {
            return;
        }

        try
        {
            FlightContext flight;
            lock (_gate)
            {
                flight = _flight;
            }

            string? icao = null, runway = null;
            if (_departureRunway?.Current() is { } live)
            {
                (icao, runway) = live;
            }
            else if (RunwayLocator.NormalizeIdent(flight.DepartureRunway) is { } ident)
            {
                icao = flight.Origin ?? flight.Airport;
                runway = ident;
            }

            if (string.IsNullOrWhiteSpace(icao) || string.IsNullOrWhiteSpace(runway))
            {
                return;
            }

            var key = RunwayLookupCache.Key(icao, runway, null);
            RunwayGeometry? geometry;
            lock (_gate)
            {
                if (_runwayCache.TryGet(key, DateTimeOffset.UtcNow, out geometry))
                {
                    _departureThreshold = geometry;
                    return;
                }
            }

            geometry = await _runways.FindAsync(icao, runway).ConfigureAwait(false);
            lock (_gate)
            {
                _runwayCache.Store(key, geometry, DateTimeOffset.UtcNow);
                _departureThreshold = geometry;
            }

            _logger.LogInformation(
                "Comms hand-over: departure runway {Icao} {Runway} threshold {Result}",
                icao, runway, geometry is null ? "unknown — no holding-point hand-over" : $"from {geometry.Source}");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Departure runway lookup for the comms hand-over failed");
        }
        finally
        {
            Interlocked.Exchange(ref _runwayLookupBusy, 0);
        }
    }

    /// <summary>Great-circle distance from the aircraft to the departure runway threshold,
    /// null while either is unknown. Called under the lock on the poll thread; a missing
    /// threshold is primed outside the lock by the caller (the cache keeps a miss from hitting
    /// the gateway every second).</summary>
    private double? DistanceToDepartureRunwayNm()
    {
        if (_phase is null || _handover.Phase != FlightPhase.TaxiOut || _departureThreshold is null)
        {
            return null;
        }

        var view = _phase.Snapshot();
        if (!view.IsLive || view.Data?.Position is not { } position)
        {
            return null;
        }

        return GreatCircle.DistanceNm(position, _departureThreshold.Threshold);
    }

    // ---- Legacy departure gate ----

    private async Task TakeCommsForDepartureAsync(FlightContext flight, SayIntentionsOptions options)
    {
        try
        {
            var apiKey = ResolveApiKey(flight, options);
            if (apiKey is null)
            {
                return;
            }

            await SetVarAsync(apiKey, "SIAI_COPILOT", "0").ConfigureAwait(false);
            await TryAutoTuneAsync(apiKey, flight, "TOWER").ConfigureAwait(false);
            await _radioGate.WaitForClearAsync().ConfigureAwait(false);
            await SayAsAsync(apiKey, AtcRequestTemplate.Fill("Contact tower, {callsign}", Values(flight).Values)).ConfigureAwait(false);
            _eventLog.Record("sayintentions.departureGate", new { });
            await _arbiter.EnqueueAsync(new SpeechRequest(
                "Comms are ours — call the takeoff request when ready.", SpeechPriority.Normal,
                Tag: "sayintentions")).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Departure comms gate failed");
        }
    }

    private async Task TryAutoTuneAsync(string apiKey, FlightContext flight, string commType)
    {
        try
        {
            var airport = flight.Airport ?? flight.Origin;
            if (airport is null)
            {
                return;
            }

            using var response = await Http.GetAsync(
                $"{ApiBase}/getWX?api_key={Uri.EscapeDataString(apiKey)}&icao={Uri.EscapeDataString(airport)}&with_comms=1")
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            var freq = FindCommFrequency(doc.RootElement, commType);
            if (freq is not null)
            {
                await SetFreqAsync(apiKey, freq).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Auto-tune failed");
        }
    }

    private static async Task SetFreqAsync(string apiKey, string freq)
    {
        using var tune = await Http.GetAsync(
            $"{ApiBase}/setFreq?api_key={Uri.EscapeDataString(apiKey)}&freq={Uri.EscapeDataString(freq)}&com=1&mode=active")
            .ConfigureAwait(false);
    }

    private static string? FindCommFrequency(JsonElement root, string commType)
    {
        // comms[] can sit at root or one level deep.
        foreach (var element in EnumerateSelfAndChildren(root))
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in element.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.Object
                        && entry.TryGetProperty("type", out var type)
                        && entry.TryGetProperty("freq", out var freq)
                        && (type.GetString()?.Contains(commType, StringComparison.OrdinalIgnoreCase) ?? false))
                    {
                        return freq.ValueKind == JsonValueKind.String
                            ? freq.GetString()
                            : freq.GetRawText();
                    }
                }
            }
        }

        return null;
    }

    private static IEnumerable<JsonElement> EnumerateSelfAndChildren(JsonElement root)
    {
        yield return root;
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject())
            {
                yield return property.Value;
            }
        }
    }

    private async Task SayAsAsync(string apiKey, string message)
    {
        var truncated = message.Length > 255 ? message[..255] : message;
        using var response = await Http.GetAsync(
            $"{ApiBase}/sayAs?api_key={Uri.EscapeDataString(apiKey)}&channel=COM1&message={Uri.EscapeDataString(truncated)}")
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("sayAs failed: {Status}", (int)response.StatusCode);
        }
    }

    private async Task SetVarAsync(string apiKey, string name, string value)
    {
        if (name == "SIAI_COPILOT" && _options.CurrentValue.FoRadios.Enabled)
        {
            value = "0"; // the FO works the radios — the SI copilot never comes back on
        }

        using var response = await Http.GetAsync(
            $"{ApiBase}/setVar?api_key={Uri.EscapeDataString(apiKey)}&var={Uri.EscapeDataString(name)}&value={Uri.EscapeDataString(value)}&category=L")
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug("setVar {Name} failed: {Status}", name, (int)response.StatusCode);
        }
    }

    private static async Task WithApiKey(
        FlightContext flight, SayIntentionsOptions options, Func<string, Task> action)
    {
        var key = ResolveApiKey(flight, options);
        if (key is not null)
        {
            await action(key).ConfigureAwait(false);
        }
    }

    private static string? ResolveApiKey(FlightContext flight, SayIntentionsOptions options)
        => options.ApiKeySource.Equals("manual", StringComparison.OrdinalIgnoreCase)
            ? (string.IsNullOrWhiteSpace(options.ManualApiKey) ? null : options.ManualApiKey)
            : flight.ApiKey;

    private static string PickTemplate(AtcRequestDefinition request, SayIntentionsOptions options)
        => options.Phraseology.Equals("faa", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(request.Faa)
            ? request.Faa
            : request.Icao;

    private static FlightContext ReadFlightJson(string path)
    {
        if (!File.Exists(path))
        {
            return FlightContext.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(FlightJsonFile.ReadAllText(path));
            var root = doc.RootElement.TryGetProperty("flight_details", out var details)
                ? details
                : doc.RootElement;
            var current = root.TryGetProperty("current_flight", out var cf) ? cf : default;

            string? Str(JsonElement e, string name)
                => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
                    ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()
                    : null;

            var callsign = Str(root, "callsign") ?? Str(root, "callsign_icao");
            var distance = double.TryParse(
                Str(root, "distance_to_runway"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var d) ? d : double.MaxValue;

            return new FlightContext(
                IsActive: callsign is not null,
                ApiKey: Str(root, "api_key"),
                Callsign: callsign,
                Gate: Str(current, "assigned_gate"),
                DepartureRunway: Str(current, "flight_plan_departing_runway"),
                Airport: Str(root, "current_airport"),
                Origin: Str(current, "flight_origin"),
                DistanceToRunway: distance);
        }
        catch (IOException)
        {
            return FlightContext.Empty; // mid-write — keep polling
        }
        catch (JsonException)
        {
            return FlightContext.Empty;
        }
    }

    private void LoadRequests()
    {
        // User tree, not the install dir (ADR-0007) — seeded from shipped defaults at startup.
        var path = Core.Configuration.UserConfigPaths.File("atc-requests.json");
        try
        {
            if (!File.Exists(path))
            {
                _configProblems?.ClearArea(Core.State.ConfigAreas.AtcRequests);
                return;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("requests", out var requests))
            {
                _requests = JsonSerializer.Deserialize<List<AtcRequestDefinition>>(
                    requests.GetRawText(), ChecklistDefinition.JsonOptions) ?? [];
            }

            _logger.LogInformation("Loaded {Count} SayIntentions ATC requests", _requests.Count);
            _configProblems?.ClearArea(Core.State.ConfigAreas.AtcRequests);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "atc-requests.json load failed");
            // Surface on the web UI too (issue #74) — this load runs once at Start, so a
            // malformed file otherwise means a whole session of silently missing phrases.
            _configProblems?.Report(Core.State.ConfigAreas.AtcRequests, path, ex.Message);
        }
    }
}
