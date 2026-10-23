using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Airports;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.VoiceReference;
using ProsimCompanion.Core.Weather;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Briefings;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Monitoring;

/// <summary>
/// "Wind check" and "runway condition" (issue #182): the FO resolves the current METAR wind
/// onto the runway in use — the departure runway until the aircraft is airborne, the landing
/// runway from then on — and judges it against the aircraft limits with the selected runway
/// condition: "Crosswind component 24 knots, tailwind 3, within limits." / "… exceeds the
/// wet-runway crosswind limit of 33 knots." Deterministic figures, no model. Read-only.
/// </summary>
public sealed class WindLimitsVoiceFeature : IVoiceFeature
{
    private static readonly string[] WindPhrases =
        ["wind check", "check the wind", "crosswind check", "what's the crosswind", "what is the crosswind", "wind components"];

    private static readonly string[] ConditionPhrases =
        ["runway condition", "what's the runway condition", "what is the runway condition", "runway state"];

    private readonly IOptionsMonitor<SopOptions> _sop;
    private readonly IOptionsMonitor<AircraftLimitsOptions> _limits;
    private readonly IFlightPhaseSource _flight;
    private readonly ProcedureSource _procedures;
    private readonly IWxProvider _weather;
    private readonly RunwayLocator _runways;
    private readonly RunwayConditionStore _condition;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<WindLimitsVoiceFeature> _logger;

    public WindLimitsVoiceFeature(
        IOptionsMonitor<SopOptions> sop,
        IOptionsMonitor<AircraftLimitsOptions> limits,
        IFlightPhaseSource flight,
        ProcedureSource procedures,
        IWxProvider weather,
        RunwayLocator runways,
        RunwayConditionStore condition,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        ILogger<WindLimitsVoiceFeature> logger)
    {
        ArgumentNullException.ThrowIfNull(sop);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(procedures);
        ArgumentNullException.ThrowIfNull(weather);
        ArgumentNullException.ThrowIfNull(runways);
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);
        _sop = sop;
        _limits = limits;
        _flight = flight;
        _procedures = procedures;
        _weather = weather;
        _runways = runways;
        _condition = condition;
        _arbiter = arbiter;
        _eventLog = eventLog;
        _logger = logger;
    }

    /// <summary>Roster declaration: between the minima query and the company channel.</summary>
    public static VoiceFeatureDeclaration Declared { get; } = new()
    {
        Precedence = 175,
        Gate = VoiceFeatureGate.ByOption("sop.monitoring.windLimits.enabled", VoiceReferenceTextIds.OffMonitoring),
        Description = new DescribedVoiceReference("windLimits", "voiceReference.group.windLimits.title", VoiceReferenceTab.FirstOfficer, VoiceSpeaker.FirstOfficer,
        [
            new(["wind check", "check the wind", "crosswind check", "what's the crosswind", "what is the crosswind", "wind components"],
                "voiceReference.group.windLimits.windCheck", "Crosswind component 24 knots, tailwind 3, within limits."),
            new(["runway condition", "what's the runway condition", "what is the runway condition", "runway state"],
                "voiceReference.group.windLimits.runwayCondition", "Runway condition wet — crosswind limit 33 knots."),
        ]),
    };

    VoiceFeatureDeclaration IVoiceFeature.Declaration => Declared;

    public bool Enabled => _sop.CurrentValue.Monitoring.WindLimits.Enabled;

    public IEnumerable<string> Phrases => [.. WindPhrases, .. ConditionPhrases];

    public bool ValueParse => false;

    public bool TryHandle(string utterance)
    {
        var text = CommandMatcher.Normalize(utterance);
        if (WindPhrases.Any(p => CommandMatcher.Normalize(p) == text))
        {
            _ = WindCheckAsync();
            return true;
        }

        if (ConditionPhrases.Any(p => CommandMatcher.Normalize(p) == text))
        {
            _ = RunwayConditionAsync();
            return true;
        }

        return false;
    }

    /// <summary>The runway in use is the departure one until the aircraft is airborne.</summary>
    public static bool IsDeparturePhase(FlightPhase phase)
        => phase is FlightPhase.Unknown or FlightPhase.ColdAndDark or FlightPhase.Preflight or FlightPhase.Departure
            or FlightPhase.PushbackAndStart or FlightPhase.TaxiOut or FlightPhase.TakeoffRoll;

    private async Task WindCheckAsync()
    {
        try
        {
            var departure = IsDeparturePhase(_flight.CurrentPhase);
            var ids = _procedures.Resolve(departure);
            if (ids.Airport is null || ids.Runway is null)
            {
                await _arbiter.SpeakAsync(departure ? "No departure runway is set yet." : "No landing runway is set yet.", SpeechPriority.Normal).ConfigureAwait(false);
                return;
            }

            var facts = await _weather.GetAsync(ids.Airport).ConfigureAwait(false);
            if (facts.WindSpeedKt is null)
            {
                await _arbiter.SpeakAsync($"No wind observation for {ids.Airport}.", SpeechPriority.Normal).ConfigureAwait(false);
                return;
            }

            var heading = await RunwayHeadingAsync(ids.Airport, ids.Runway).ConfigureAwait(false);
            if (heading is not { } hdg)
            {
                await _arbiter.SpeakAsync("I have no heading for that runway.", SpeechPriority.Normal).ConfigureAwait(false);
                return;
            }

            var wind = WindMath.Resolve(facts.WindDirDeg, facts.WindSpeedKt, facts.WindGustKt, hdg)!;
            var condition = departure ? null : _condition.Current;
            var verdict = WindLimitCheck.Evaluate(wind, _limits.CurrentValue, departure ? WindPhase.Takeoff : WindPhase.Landing, condition);
            var runwaySpoken = Core.Speech.SpokenText.RunwayOrNull(ids.Runway) ?? ids.Runway;
            var text = $"Runway {runwaySpoken}: {WindLimitCheck.Spoken(verdict)}";
            _eventLog.Record("wind.check", new
            {
                airport = ids.Airport,
                runway = ids.Runway,
                phase = departure ? "takeoff" : "landing",
                headingDeg = hdg,
                windDir = facts.WindDirDeg,
                windKt = facts.WindSpeedKt,
                gustKt = facts.WindGustKt,
                crosswindKt = Math.Round(wind.MaxCrosswindKt),
                tailwindKt = Math.Round(wind.MaxTailwindKt),
                crosswindLimitKt = verdict.CrosswindLimitKt,
                tailwindLimitKt = verdict.TailwindLimitKt,
                limitName = verdict.CrosswindLimitName,
                tone = verdict.Tone.ToString().ToLowerInvariant(),
                text,
            });
            await _arbiter.EnqueueAsync(new SpeechRequest(text, SpeechPriority.Normal, Tag: "wind.limits")).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Wind check failed");
            _ = _arbiter.SpeakAsync("Unable to check the wind.", SpeechPriority.Normal);
        }
    }

    private async Task RunwayConditionAsync()
    {
        try
        {
            var condition = _condition.Current;
            var (limitKt, name) = WindLimitCheck.CrosswindLimit(_limits.CurrentValue, WindPhase.Landing, condition);
            var text = condition is { } c
                ? $"Runway condition {RunwayConditions.Spoken(c)} — {name} crosswind limit {limitKt} knots."
                : $"No runway condition selected — dry limits apply, crosswind {limitKt} knots. Set it on the Landing page.";
            _eventLog.Record("wind.runway-condition", new { condition = condition?.ToString(), crosswindLimitKt = limitKt, text });
            await _arbiter.EnqueueAsync(new SpeechRequest(text, SpeechPriority.Normal, Tag: "wind.limits")).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Runway condition answer failed");
        }
    }

    private async Task<double?> RunwayHeadingAsync(string airport, string runway)
    {
        try
        {
            var geometry = await _runways.FindAsync(airport, runway).ConfigureAwait(false);
            if (geometry?.TrueHeadingDeg is { } heading)
            {
                return heading;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Runway heading lookup failed for {Airport}/{Runway}", airport, runway);
        }

        return DestinationWeatherWatchCore.RunwayHeadingDeg(runway);
    }
}
