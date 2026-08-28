using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Speech.Arbiter;

namespace ProsimCompanion.Speech.Monitoring;

/// <summary>
/// Silent-flow anomaly advisories (Prosim2FO semantics): each check watches one "somebody
/// forgot something" condition and speaks it through the arbiter ONCE on the tick it becomes
/// true (edge-triggered), then stays quiet until it resolves; a per-key rate limit stops nags,
/// stamped on SPEAK (not attempt — deliberately different from the callouts placards).
/// Weather extras: icing-conditions advisory, anti-ice-left-on with a sustain dwell so brief
/// warm layers don't trigger it, and a phase-aware ISA-deviation note (once per climb+cruise
/// episode: high climb or cruise, whichever comes first — issue #73). Advisory only —
/// never commands. Active/rate-limit state deliberately persists across phase changes and
/// flights (predecessor behavior; no PhaseChanged subscription). The persona styling layer is
/// not ported yet — the deterministic texts speak directly (they were the fallback anyway).
/// <see cref="ProcessSample"/> takes its clock as a parameter so tests can step rate-limit and
/// dwell windows deterministically.
/// </summary>
public sealed class FlowMonitor : Core.Hosting.IStartupModule, IDisposable
{
    private const double AdvisoryTtlSec = 10.0;
    private const int PollMs = 1000;

    /// <summary>Altitude (ft) above which a climb ISA note is worth making — below ~FL150 the
    /// deviation is dominated by low-level thermal noise, not the upper-air airmass.</summary>
    private const double IsaClimbMinAltFt = 15_000;

    private readonly IOptionsMonitor<SopOptions> _sop;
    private readonly IOptionsMonitor<SpeechOptions> _speech;
    private readonly ISpeechArbiter _arbiter;
    private readonly IFlightDataSource _source;
    private readonly IFlightPhaseSource _flight;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<FlowMonitor> _logger;
    private readonly Persona.StyledSpeechService? _styledSpeech;
    private readonly object _lock = new();

    private readonly HashSet<string> _active = [];
    private readonly Dictionary<string, long> _lastSpoken = [];
    private readonly Dictionary<string, long> _sustainedSince = [];
    private bool _isaAnnouncedThisEpisode;

    private Timer? _timer;
    private int _ticking;

    public FlowMonitor(
        IOptionsMonitor<SopOptions> sop,
        IOptionsMonitor<SpeechOptions> speech,
        ISpeechArbiter arbiter,
        IFlightDataSource source,
        IFlightPhaseSource flight,
        JsonlEventLog eventLog,
        ILogger<FlowMonitor> logger,
        Persona.StyledSpeechService? styledSpeech = null)
    {
        ArgumentNullException.ThrowIfNull(sop);
        ArgumentNullException.ThrowIfNull(speech);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _styledSpeech = styledSpeech;
        _sop = sop;
        _speech = speech;
        _arbiter = arbiter;
        _source = source;
        _flight = flight;
        _eventLog = eventLog;
        _logger = logger;
    }

    public void Start() => _timer = new Timer(_ => Tick(), null, PollMs, PollMs);

    public void Dispose() => _timer?.Dispose();

    /// <summary>One evaluation pass — public with an explicit clock for deterministic tests;
    /// the timer supplies <see cref="Environment.TickCount64"/>.</summary>
    public void ProcessSample(FlightDataSnapshot s, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(s);

        // Flight-live gate (issue #114): ProSim pushes plausible data with no MSFS session.
        if (!s.IsValid || !_flight.IsLive)
        {
            return;
        }

        lock (_lock)
        {
            var sop = _sop.CurrentValue;
            var fm = sop.FlowMonitor;
            if (!fm.Enabled)
            {
                return;
            }

            double ceiling = _speech.CurrentValue.SterileCockpitCeilingFt;
            var rateLimitMs = (long)(fm.RateLimitSeconds * 1000);
            var phase = _flight.CurrentPhase;

            var climbing = phase is FlightPhase.InitialClimb or FlightPhase.Climb;
            var descending = phase is FlightPhase.Descent or FlightPhase.Approach;
            var airborne = phase is FlightPhase.InitialClimb or FlightPhase.Climb
                or FlightPhase.Cruise or FlightPhase.Descent or FlightPhase.Approach;

            Check("landingLightsOn", fm.LandingLightsAboveCeiling, rateLimitMs, nowMs, s,
                c => climbing && c.AltitudeFt > ceiling && c.AnyLandingLightOn);

            Check("landingLightsOff", fm.LandingLightsBelowCeiling, rateLimitMs, nowMs, s,
                c => descending && c.AltitudeFt < ceiling && !c.AnyLandingLightOn);

            Check("flapsNotRetracted", fm.FlapsNotRetracted, rateLimitMs, nowMs, s,
                c => climbing && c.AltitudeAglFt > fm.FlapsCleanAboveAglFt && c.FlapHandle > 0);

            Check("gearStillDown", fm.GearStillDown, rateLimitMs, nowMs, s,
                c => climbing && c.AltitudeAglFt > fm.GearUpAboveAglFt && c.GearDown);

            // Deliberately no phase/ground gate (predecessor parity).
            Check("parkingBrakeWithThrust", fm.ParkingBrakeWithThrust, rateLimitMs, nowMs, s,
                c => c.ParkBrakeSet && c.AverageN1Percent > 50);

            // Note: != On means Auto also triggers — masked by the disabled default.
            Check("seatbeltSignsOff", fm.SeatbeltSignsOff, rateLimitMs, nowMs, s,
                c => airborne && c.AltitudeFt < ceiling && c.SeatbeltSignsMode != 1);

            // Raw running booleans on purpose: a spooling engine counts as running.
            Check("beaconOffEngineRunning", fm.BeaconOffEngineRunning, rateLimitMs, nowMs, s,
                c => !airborne && !c.BeaconOn && c.AnyEngineRunningRaw);

            Check("spoilersNotArmed", fm.SpoilersNotArmed, rateLimitMs, nowMs, s,
                c => _flight.CurrentPhase == FlightPhase.Approach && c.GearDown && !c.SpeedbrakeArmed);

            Check("transponderNotSet", fm.TransponderNotSet, rateLimitMs, nowMs, s,
                c => _flight.CurrentPhase is FlightPhase.TakeoffRoll or FlightPhase.InitialClimb
                    && c.XpdrMode != 2);

            RunWeatherChecks(sop.Weather, rateLimitMs, nowMs, s, phase, airborne);
        }
    }

    private void RunWeatherChecks(
        SopWeatherOptions w, long rateLimitMs, long nowMs, FlightDataSnapshot s,
        FlightPhase phase, bool airborne)
    {
        // ONE ISA note per climb+cruise episode (issue #73): a step climb between cruise
        // segments stays inside the episode (unlike the predecessor's once-per-cruise reset,
        // which re-announced after every step climb); descending/landing ends the episode and
        // re-arms the note for the next flight. Runs even while the weather block is disabled.
        if (phase is not (FlightPhase.InitialClimb or FlightPhase.Climb or FlightPhase.Cruise))
        {
            _isaAnnouncedThisEpisode = false;
        }

        if (!w.Enabled)
        {
            return;
        }

        var operating = airborne || phase is FlightPhase.TaxiOut or FlightPhase.TakeoffRoll;

        // Icing checks the ENGINE switches only; "anti-ice left on" includes wing — the
        // asymmetry is deliberate (the icing advisory is about engine anti-ice).
        Check("icingConditions", w.IcingConditions, rateLimitMs, nowMs, s,
            c => operating && c.TatC <= w.IcingTatMaxC
                && (!w.RequireVisibleMoisture || c.InCloud || c.VisibilityM <= w.MoistureVisibilityM)
                && !(c.EngineAntiIce1On || c.EngineAntiIce2On));

        var rawLeftOn = airborne && AnyAntiIceOn(s) && s.TatC > w.AntiIceClearC;
        var dwellMet = Dwell("antiIceLeftOn", rawLeftOn, w.AntiIceDwellSeconds, nowMs);
        // dwellMet is frozen in the lambda; the live re-check covers anti-ice + TAT only.
        Check("antiIceLeftOn", w.AntiIceLeftOn, rateLimitMs, nowMs, s,
            c => dwellMet && AnyAntiIceOn(c) && c.TatC > w.AntiIceClearC);

        // ISA bypasses Check(): no edge state, no rate limit, its own once-per-episode latch.
        // Eligible in cruise, or in the high climb (above the thermal-noise floor) so the
        // "expect reduced climb performance" note arrives while it is still actionable.
        var isaEligible = phase == FlightPhase.Cruise
            || (phase == FlightPhase.Climb && s.AltitudeFt >= IsaClimbMinAltFt);
        if (isaEligible && w.IsaDeviation.Enabled && !_isaAnnouncedThisEpisode)
        {
            var deviation = s.OatC - IsaTempC(s.AltitudeFt);
            if (Math.Abs(deviation) >= w.IsaDeviationThresholdC)
            {
                _isaAnnouncedThisEpisode = true;
                var text = IsaAdvisoryText((int)Math.Round(deviation), phase);
                var priority = ParsePriority(w.IsaDeviation.Priority);
                _eventLog.Record("flow.advisory", new
                {
                    id = "isaDeviation",
                    text,
                    priority = priority.ToString().ToLowerInvariant(),
                    spoken = true,
                });
                _ = StyleAndEnqueueAsync(
                    "isaDeviation", text, priority,
                    () => _flight.CurrentPhase is FlightPhase.Climb or FlightPhase.Cruise);
            }
        }
    }

    /// <summary>Edge-triggered advisory: speaks on false→true (rate-limited per key, stamp on
    /// speak), clears on true→false. A throwing condition skips the check with state
    /// unchanged; a rate-limited edge still marks the advisory active so it won't retry until
    /// it resolves and re-triggers.</summary>
    private void Check(
        string key, FlowCheckSetting fc, long rateLimitMs, long nowMs,
        FlightDataSnapshot s, Func<FlightDataSnapshot, bool> condition)
    {
        if (!fc.Enabled || string.IsNullOrWhiteSpace(fc.Text))
        {
            return;
        }

        bool now;
        try
        {
            now = condition(s);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Flow check {Key} threw", key);
            return;
        }

        // Live() closes over the CONDITION, not the pass's snapshot — the arbiter re-checks
        // current state at dequeue (the source memoises, so this is cheap).
        bool Live() => condition(_source.Sample());

        var wasActive = _active.Contains(key);
        if (now && !wasActive)
        {
            _active.Add(key);
            if (!_lastSpoken.TryGetValue(key, out var last) || nowMs - last >= rateLimitMs)
            {
                _lastSpoken[key] = nowMs;
                var priority = ParsePriority(fc.Priority);
                _logger.LogInformation("Flow advisory: {Key} \"{Text}\" ({Priority})", key, fc.Text, priority);
                _eventLog.Record("flow.advisory", new
                {
                    id = key,
                    text = fc.Text,
                    priority = priority.ToString().ToLowerInvariant(),
                    spoken = true,
                });
                _ = StyleAndEnqueueAsync(key, fc.Text, priority, Live);
            }
            else
            {
                _logger.LogDebug("Flow advisory {Key} suppressed (rate-limited)", key);
                _eventLog.Record("flow.advisory", new { id = key, text = fc.Text, spoken = false, reason = "rate-limited" });
            }
        }
        else if (!now && wasActive)
        {
            _active.Remove(key);
            _eventLog.Record("flow.resolved", new { id = key });
        }
    }

    /// <summary>Persona restyle (Advisory category) then enqueue. Without the styling service
    /// — or with persona off — the deterministic text speaks unchanged; the restyle is bounded
    /// by the persona timeout and the TTL/validity still gate at dequeue, so a slow LLM only
    /// delays the advisory, never wedges it.</summary>
    private async Task StyleAndEnqueueAsync(string key, string text, SpeechPriority priority, Func<bool> live)
    {
        var spoken = text;
        if (_styledSpeech is not null)
        {
            spoken = await _styledSpeech
                .StyleAsync(text, Persona.PersonaStyleCategory.Advisory, key)
                .ConfigureAwait(false);
        }

        _ = _arbiter.EnqueueAsync(new SpeechRequest(
            spoken, priority, TimeSpan.FromSeconds(AdvisoryTtlSec), live, Tag: key));
    }

    /// <summary>True once <paramref name="now"/> has held for the dwell window. Never
    /// satisfied on the first true observation; any false tick restarts the clock.</summary>
    private bool Dwell(string key, bool now, double dwellSec, long nowMs)
    {
        if (!now)
        {
            _sustainedSince.Remove(key);
            return false;
        }

        if (!_sustainedSince.TryGetValue(key, out var since))
        {
            _sustainedSince[key] = nowMs;
            return false;
        }

        return nowMs - since >= (long)(dwellSec * 1000);
    }

    private static bool AnyAntiIceOn(FlightDataSnapshot s)
        => s.EngineAntiIce1On || s.EngineAntiIce2On || s.WingAntiIceOn;

    private static SpeechPriority ParsePriority(string? name)
        => Enum.TryParse<SpeechPriority>(name, ignoreCase: true, out var p) ? p : SpeechPriority.High;

    /// <summary>ICAO standard atmosphere: 15 °C at MSL, −1.98 °C / 1000 ft, isothermal
    /// −56.5 °C above 36,089 ft.</summary>
    private static double IsaTempC(double altFt)
        => altFt <= 36_089.0 ? 15.0 - 1.98 * (altFt / 1000.0) : -56.5;

    /// <summary>Phase-aware ISA advisory wording (issue #73 — the old text always said
    /// "climb performance will be reduced", which sounds wrong when already level in cruise).
    /// Warm in the climb points at climb performance; warm in cruise points at step-climb
    /// capability and optimum level; cold keeps the icing note regardless of phase. Pure and
    /// public so tests pin every branch without a monitor.</summary>
    public static string IsaAdvisoryText(int deviation, FlightPhase phase)
    {
        var signed = $"{(deviation >= 0 ? "plus" : "minus")} {Math.Abs(deviation)}";
        if (deviation <= 0)
        {
            return $"ISA {signed} today. Colder than standard; watch for icing.";
        }

        return phase == FlightPhase.Climb
            ? $"ISA {signed} — expect reduced climb performance."
            : $"ISA {signed} today — expect reduced step-climb performance and a lower optimum level.";
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            ProcessSample(_source.Sample(), Environment.TickCount64);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Flow monitor tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }
}
