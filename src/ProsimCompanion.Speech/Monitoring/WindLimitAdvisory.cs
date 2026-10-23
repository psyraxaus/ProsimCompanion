using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Airports;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Weather;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Briefings;

namespace ProsimCompanion.Speech.Monitoring;

/// <summary>
/// Shell of <see cref="WindLimitAdvisoryCore"/> (issue #182): rides the hero weather cards'
/// refresh like the destination weather watch, armed by Flight live and
/// <c>sop.monitoring.windLimits.approachAdvisory</c>. The landing runway is the FMS / flight.json
/// / manual arrival runway (the briefing's procedure source), else the OFP's planned one; its
/// true heading comes from the runway locator (gateway, then DFD), else the designator × 10.
/// Advisory only, spoken at Normal with a two-minute TTL.
/// </summary>
public sealed class WindLimitAdvisory : Core.Hosting.IStartupModule, IDisposable
{
    private static readonly TimeSpan SpokenTtl = TimeSpan.FromMinutes(2);

    private readonly IOptionsMonitor<SopOptions> _sop;
    private readonly IOptionsMonitor<AircraftLimitsOptions> _limits;
    private readonly IFlightPhaseSource _flight;
    private readonly HeroWeatherStore _weather;
    private readonly OfpStore _ofp;
    private readonly ProcedureSource _procedures;
    private readonly RunwayLocator _runways;
    private readonly RunwayConditionStore _condition;
    private readonly GroundOpsSignals _signals;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<WindLimitAdvisory> _logger;
    private readonly WindLimitAdvisoryCore _core = new();
    private readonly object _gate = new();
    private IDisposable? _subscription;
    private int _running;

    public WindLimitAdvisory(
        IOptionsMonitor<SopOptions> sop,
        IOptionsMonitor<AircraftLimitsOptions> limits,
        IFlightPhaseSource flight,
        HeroWeatherStore weather,
        OfpStore ofp,
        ProcedureSource procedures,
        RunwayLocator runways,
        RunwayConditionStore condition,
        GroundOpsSignals signals,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        ILogger<WindLimitAdvisory> logger)
    {
        ArgumentNullException.ThrowIfNull(sop);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(weather);
        ArgumentNullException.ThrowIfNull(ofp);
        ArgumentNullException.ThrowIfNull(procedures);
        ArgumentNullException.ThrowIfNull(runways);
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);
        _sop = sop;
        _limits = limits;
        _flight = flight;
        _weather = weather;
        _ofp = ofp;
        _procedures = procedures;
        _runways = runways;
        _condition = condition;
        _signals = signals;
        _arbiter = arbiter;
        _eventLog = eventLog;
        _logger = logger;
        _signals.FlightCycleReset += OnFlightCycleReset;
    }

    public void Start() => _subscription = _weather.Observe(snapshot => _ = ObserveAsync(snapshot));

    public void Dispose()
    {
        _subscription?.Dispose();
        _signals.FlightCycleReset -= OnFlightCycleReset;
    }

    private void OnFlightCycleReset()
    {
        lock (_gate)
        {
            _core.Reset();
        }
    }

    /// <summary>One hero-weather refresh. Public for tests; the clock is the phase source's.</summary>
    public async Task<WindLimitVerdict?> ObserveAsync(HeroWeatherSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return null;
        }

        try
        {
            var options = _sop.CurrentValue.Monitoring.WindLimits;
            if (!options.Enabled || !options.ApproachAdvisory || !_flight.IsLive)
            {
                return null;
            }

            var phase = _flight.CurrentPhase;
            if (phase != FlightPhase.Approach)
            {
                lock (_gate)
                {
                    _core.Reset();
                }

                return null;
            }

            var ids = _procedures.Resolve(departure: false);
            var airport = ids.Airport ?? _ofp.Current?.DestinationIcao;
            var runway = ids.Runway ?? _ofp.Current?.PlannedRunwayIn;
            if (string.IsNullOrWhiteSpace(airport) || string.IsNullOrWhiteSpace(runway))
            {
                return null;
            }

            var card = string.Equals(snapshot.Local.Icao, airport, StringComparison.OrdinalIgnoreCase) ? snapshot.Local
                : string.Equals(snapshot.Second.Icao, airport, StringComparison.OrdinalIgnoreCase) ? snapshot.Second
                : null;
            if (card is not { Status: WxProbeStatus.Found })
            {
                return null;
            }

            var heading = await RunwayHeadingAsync(airport, runway).ConfigureAwait(false);
            WindLimitVerdict? verdict;
            lock (_gate)
            {
                verdict = _core.Observe(phase, card.Facts, heading, _condition.Current, _limits.CurrentValue);
            }

            if (verdict is null)
            {
                return null;
            }

            var runwaySpoken = Core.Speech.SpokenText.RunwayOrNull(runway) ?? runway;
            var text = $"Wind check for runway {runwaySpoken}: {WindLimitCheck.Spoken(verdict)}";
            _logger.LogInformation("Wind-limit advisory {Airport}/{Runway}: {Text}", airport, runway, text);
            _eventLog.Record("wind.limit-advisory", new
            {
                airport,
                runway,
                headingDeg = heading,
                condition = _condition.Current?.ToString(),
                crosswindKt = Math.Round(verdict.Wind.MaxCrosswindKt),
                tailwindKt = Math.Round(verdict.Wind.MaxTailwindKt),
                crosswindLimitKt = verdict.CrosswindLimitKt,
                tailwindLimitKt = verdict.TailwindLimitKt,
                limitName = verdict.CrosswindLimitName,
                text,
                metar = card.Facts.RawMetar,
            });
            await _arbiter.EnqueueAsync(new SpeechRequest(text, SpeechPriority.Normal, SpokenTtl, Tag: "wind.limits")).ConfigureAwait(false);
            return verdict;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Wind-limit advisory failed");
            return null;
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
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
