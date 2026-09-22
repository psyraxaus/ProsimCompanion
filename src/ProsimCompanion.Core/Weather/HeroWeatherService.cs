using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;

namespace ProsimCompanion.Core.Weather;

/// <summary>
/// Keeps the two Flight Status weather cards current: probes the composite weather chain for
/// the stations <see cref="WeatherCardPlanner"/> picks, on every OFP change, on the phase edge
/// into the arrival window (the local card moves to the destination), and every
/// <see cref="FlightStatusOptions.WeatherRefreshMinutes"/>. One fetch at a time; a refresh
/// requested while one runs re-arms it. Never throws — an empty probe renders the failure
/// line on the card.
/// </summary>
public sealed class HeroWeatherService : IDisposable
{
    private readonly IWxProvider _weather;
    private readonly OfpStore _ofp;
    private readonly IFlightPhaseSource _flight;
    private readonly IOptionsMonitor<FlightStatusOptions> _options;
    private readonly HeroWeatherStore _store;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<HeroWeatherService> _logger;
    private readonly Timer _timer;
    private readonly SemaphoreSlim _fetchGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private WeatherCardPlan _lastPlan = WeatherCardPlan.None;
    private int _rearm;

    public HeroWeatherService(
        IWxProvider weather,
        OfpStore ofp,
        IFlightPhaseSource flight,
        IOptionsMonitor<FlightStatusOptions> options,
        HeroWeatherStore store,
        JsonlEventLog eventLog,
        ILogger<HeroWeatherService> logger)
    {
        ArgumentNullException.ThrowIfNull(weather);
        ArgumentNullException.ThrowIfNull(ofp);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _weather = weather;
        _ofp = ofp;
        _flight = flight;
        _options = options;
        _store = store;
        _eventLog = eventLog;
        _logger = logger;

        _ofp.Changed += OnOfpChanged;
        _flight.PhaseChanged += OnPhaseChanged;
        _timer = new Timer(_ => Refresh("interval"), null, TimeSpan.FromSeconds(5), RefreshInterval());
    }

    /// <summary>Re-probe now (the page's refresh affordance, voice, tests).</summary>
    public void RequestRefresh(string reason) => Refresh(reason);

    private TimeSpan RefreshInterval()
        => TimeSpan.FromMinutes(Math.Clamp(_options.CurrentValue.WeatherRefreshMinutes, 1, 120));

    private void OnOfpChanged(object? sender, EventArgs e) => Refresh("OFP changed");

    private void OnPhaseChanged(object? sender, FlightPhaseChangedEventArgs e)
    {
        // Only the plan matters: re-probe when the stations change, not on every phase edge.
        if (WeatherCardPlanner.Plan(_ofp.Current, _flight.CurrentPhase) != _lastPlan)
        {
            Refresh("phase changed the local station");
        }
    }

    private void Refresh(string reason)
    {
        if (_shutdown.IsCancellationRequested)
        {
            return;
        }

        if (!_fetchGate.Wait(0))
        {
            Interlocked.Exchange(ref _rearm, 1);
            return;
        }

        _ = FetchAsync(reason);
    }

    private async Task FetchAsync(string reason)
    {
        try
        {
            do
            {
                Interlocked.Exchange(ref _rearm, 0);
                await FetchOnceAsync(reason, _shutdown.Token).ConfigureAwait(false);
                reason = "re-armed";
            }
            while (Interlocked.CompareExchange(ref _rearm, 0, 1) == 1 && !_shutdown.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hero weather refresh failed");
            _store.Update(s => s with { IsRefreshing = false });
        }
        finally
        {
            _fetchGate.Release();
        }
    }

    private async Task FetchOnceAsync(string reason, CancellationToken cancellationToken)
    {
        var plan = WeatherCardPlanner.Plan(_ofp.Current, _flight.CurrentPhase);
        _lastPlan = plan;
        if (plan.LocalIcao.Length == 0)
        {
            _store.Update(_ => HeroWeatherSnapshot.Empty);
            return;
        }

        _store.Update(s => s with { IsRefreshing = true });
        _logger.LogDebug("Hero weather: probing {Local} and {Second} ({Reason})", plan.LocalIcao, plan.SecondIcao, reason);

        var local = await ProbeCardAsync(WeatherCardRole.Local, plan.LocalIcao, plan.LocalName, cancellationToken).ConfigureAwait(false);
        // Same station twice = one probe; the chain is a network round-trip per tier.
        var second = plan.SecondIcao.Length == 0
            ? WeatherCard.Empty(plan.SecondRole)
            : string.Equals(plan.SecondIcao, plan.LocalIcao, StringComparison.OrdinalIgnoreCase)
                ? local with { Role = plan.SecondRole }
                : await ProbeCardAsync(plan.SecondRole, plan.SecondIcao, plan.SecondName, cancellationToken).ConfigureAwait(false);

        _store.Update(_ => new HeroWeatherSnapshot(local, second, DateTimeOffset.UtcNow, false));
        _eventLog.Record("hero-weather-refreshed", new
        {
            reason,
            local = new { icao = local.Icao, status = local.Status.ToString(), sky = local.Sky.ToString() },
            second = new { role = second.Role.ToString(), icao = second.Icao, status = second.Status.ToString(), sky = second.Sky.ToString() },
        });
    }

    private async Task<WeatherCard> ProbeCardAsync(WeatherCardRole role, string icao, string name, CancellationToken cancellationToken)
    {
        var probe = await _weather.ProbeAsync(icao, cancellationToken).ConfigureAwait(false);
        if (probe.Status != WxProbeStatus.Found)
        {
            _logger.LogInformation("Hero weather: {Icao} has no observation ({Status}: {Detail})", icao, probe.Status, probe.Detail);
        }

        return WeatherCard.From(role, icao, name, probe);
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _timer.Dispose();
        _ofp.Changed -= OnOfpChanged;
        _flight.PhaseChanged -= OnPhaseChanged;
        _shutdown.Dispose();
    }
}
