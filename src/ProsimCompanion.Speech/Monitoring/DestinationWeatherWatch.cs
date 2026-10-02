using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Weather;
using ProsimCompanion.Speech.Arbiter;

namespace ProsimCompanion.Speech.Monitoring;

/// <summary>
/// Destination weather watch (issue #148): rides the hero weather cards' refresh cadence —
/// no polling of its own — and from the cruise onward speaks what
/// <see cref="DestinationWeatherWatchCore"/> finds changed at the destination, with the
/// alternate's figures once the destination is below a limit. Arms on Flight live; resets
/// its baseline with the flight cycle and when the destination changes. Advisory only.
/// </summary>
public sealed class DestinationWeatherWatch : Core.Hosting.IStartupModule, IDisposable
{
    private static readonly TimeSpan SpokenTtl = TimeSpan.FromMinutes(2);

    private readonly IOptionsMonitor<SopOptions> _sop;
    private readonly IFlightPhaseSource _flight;
    private readonly HeroWeatherStore _weather;
    private readonly OfpStore _ofp;
    private readonly GroundOpsSignals _signals;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<DestinationWeatherWatch> _logger;
    private readonly Core.Speech.ISpokenText _spokenText;
    private readonly DestinationWeatherWatchCore _core = new();
    private readonly object _gate = new();
    private IDisposable? _subscription;
    private string _destination = "";

    public DestinationWeatherWatch(
        IOptionsMonitor<SopOptions> sop,
        IFlightPhaseSource flight,
        HeroWeatherStore weather,
        OfpStore ofp,
        GroundOpsSignals signals,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        ILogger<DestinationWeatherWatch> logger,
        Core.Speech.ISpokenText spokenText)
    {
        ArgumentNullException.ThrowIfNull(sop);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(weather);
        ArgumentNullException.ThrowIfNull(ofp);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(spokenText);

        _sop = sop;
        _flight = flight;
        _weather = weather;
        _ofp = ofp;
        _signals = signals;
        _arbiter = arbiter;
        _eventLog = eventLog;
        _logger = logger;
        _spokenText = spokenText;
        _signals.FlightCycleReset += OnFlightCycleReset;
    }

    public void Start() => _subscription = _weather.Observe(snapshot => Observe(snapshot, DateTimeOffset.UtcNow));

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

    /// <summary>One hero-weather refresh, clock supplied. Returns what was spoken (for
    /// tests); empty when gated off (disabled, not live, before the cruise, no destination
    /// card, no observation) or when nothing changed.</summary>
    public IReadOnlyList<WeatherAnnouncement> Observe(HeroWeatherSnapshot snapshot, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        try
        {
            lock (_gate)
            {
                var options = _sop.CurrentValue.Monitoring.DestinationWeather;
                var ofp = _ofp.Current;
                var destination = ofp?.DestinationIcao ?? "";
                if (!string.Equals(destination, _destination, StringComparison.OrdinalIgnoreCase))
                {
                    // A new destination: the old baseline would read as a dozen "changes".
                    _destination = destination;
                    _core.Reset();
                }

                if (!options.Enabled || !_flight.IsLive || destination.Length == 0
                    || _flight.CurrentPhase is not (FlightPhase.Cruise or FlightPhase.Descent or FlightPhase.Approach))
                {
                    return [];
                }

                var card = Card(snapshot, destination);
                if (card is not { Status: WxProbeStatus.Found })
                {
                    return [];
                }

                var announcements = _core.Observe(card.Facts, ofp?.PlannedRunwayIn, options, nowUtc);
                if (announcements.Count == 0)
                {
                    return [];
                }

                var alternate = snapshot.Local.Role == WeatherCardRole.Alternate ? snapshot.Local
                    : snapshot.Second.Role == WeatherCardRole.Alternate ? snapshot.Second : null;
                var alternateLine = alternate is { Status: WxProbeStatus.Found } && announcements.Any(a => a.BelowThreshold)
                    ? DestinationWeatherWatchCore.AlternateLine(_spokenText.Airport(alternate.Icao), alternate.Facts)
                    : null;

                var text = string.Join(" ", announcements.Select(a => a.Text).Append(alternateLine).Where(s => s is not null));
                _logger.LogInformation("Destination weather watch {Icao}: {Text}", destination, text);
                _eventLog.Record("fo.destination-weather", new
                {
                    icao = destination,
                    triggers = announcements.Select(a => a.Trigger.ToString().ToLowerInvariant()).ToArray(),
                    belowThreshold = announcements.Any(a => a.BelowThreshold),
                    alternate = alternate?.Icao,
                    text,
                    metar = card.Facts.RawMetar,
                });
                _ = _arbiter.EnqueueAsync(new SpeechRequest(text, SpeechPriority.Normal, SpokenTtl, Tag: "fo.destination-weather"));
                return announcements;
            }
        }
        catch (Exception ex)
        {
            // An observer must never fail the weather store's notification.
            _logger.LogWarning(ex, "Destination weather watch failed");
            return [];
        }
    }

    /// <summary>The card showing the destination — the second card in the cruise, the local
    /// one from the descent.</summary>
    private static WeatherCard? Card(HeroWeatherSnapshot snapshot, string destination)
        => string.Equals(snapshot.Local.Icao, destination, StringComparison.OrdinalIgnoreCase) ? snapshot.Local
            : string.Equals(snapshot.Second.Icao, destination, StringComparison.OrdinalIgnoreCase) ? snapshot.Second
            : null;
}
