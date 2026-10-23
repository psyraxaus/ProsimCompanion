using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Weather;
using ProsimCompanion.Speech.Arbiter;

namespace ProsimCompanion.Speech.Cabin;

/// <summary>
/// "Captain, your PA" (issue #186): a 1 Hz shell over <see cref="PaPromptCore"/>. At each
/// moment the FO says the line once and the Flight Status PA card fills in the live facts for
/// the captain to read over the cabin PA. Nothing is read aloud for the captain — the captain
/// is a human. Off by <c>cabin.paPrompts</c>.
/// </summary>
public sealed class PaPromptService : Core.Hosting.IStartupModule, IDisposable
{
    public const string PromptText = "Captain, your PA.";

    private readonly IOptionsMonitor<CabinOptions> _options;
    private readonly FlightTimesStore _times;
    private readonly IFlightPhaseSource _flight;
    private readonly IFlightDataSource _flightData;
    private readonly TurbulenceStore _turbulence;
    private readonly PaPromptStore _store;
    private readonly OfpStore _ofp;
    private readonly FlightProgressStore _progress;
    private readonly HeroWeatherStore _weather;
    private readonly GroundOpsSignals _signals;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<PaPromptService> _logger;
    private readonly PaPromptCore _core = new();
    private readonly object _gate = new();
    private Timer? _timer;
    private int _ticking;
    private bool _todSeen;

    public PaPromptService(
        IOptionsMonitor<CabinOptions> options,
        FlightTimesStore times,
        IFlightPhaseSource flight,
        IFlightDataSource flightData,
        TurbulenceStore turbulence,
        PaPromptStore store,
        OfpStore ofp,
        FlightProgressStore progress,
        HeroWeatherStore weather,
        GroundOpsSignals signals,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        ILogger<PaPromptService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(times);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(flightData);
        ArgumentNullException.ThrowIfNull(turbulence);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(ofp);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(weather);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _times = times;
        _flight = flight;
        _flightData = flightData;
        _turbulence = turbulence;
        _store = store;
        _ofp = ofp;
        _progress = progress;
        _weather = weather;
        _signals = signals;
        _arbiter = arbiter;
        _eventLog = eventLog;
        _logger = logger;
    }

    public void Start()
    {
        Attach();
        _timer = new Timer(_ => Tick(), null, 1000, 1000);
    }

    internal void Attach()
    {
        _signals.TodApproaching += OnTodApproaching;
        _signals.FlightCycleReset += OnCycleReset;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _signals.TodApproaching -= OnTodApproaching;
        _signals.FlightCycleReset -= OnCycleReset;
    }

    private void OnTodApproaching(TodApproaching _)
    {
        lock (_gate)
        {
            _todSeen = true;
        }
    }

    private void OnCycleReset()
    {
        lock (_gate)
        {
            _core.Reset();
            _todSeen = false;
        }
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            Tick(DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PA prompt tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    /// <summary>One evaluation with an explicit clock.</summary>
    public void Tick(DateTimeOffset nowUtc)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled || !_flight.IsLive)
        {
            return;
        }

        var times = _times.Snapshot();
        if (times == FlightTimesSnapshot.Empty && _store.Snapshot().Done.Count > 0 && _flight.CurrentPhase.IsBeforeTaxiOut())
        {
            // The tracker cleared the stamps for the next leg: the prompts start over.
            _store.Clear();
            lock (_gate)
            {
                _core.Reset();
                _todSeen = false;
            }
        }

        PaPromptKind? kind;
        lock (_gate)
        {
            kind = _core.Evaluate(new PaPromptSample(
                times.TakeoffUtc, _flight.CurrentPhase, _todSeen, _turbulence.Snapshot().Level,
                _flightData.Sample().SeatbeltSignsMode == 1, nowUtc), options, _store.Snapshot().Done);
        }

        if (kind is not { } due)
        {
            return;
        }

        var weather = _weather.Snapshot();
        var destination = weather.Second.Role == WeatherCardRole.Destination ? weather.Second
            : weather.Local.Role == WeatherCardRole.Destination ? weather.Local : null;
        var facts = PaScriptFacts.From(_ofp.Current, _progress.Snapshot(), destination);
        if (!_store.Prompt(due, facts, nowUtc))
        {
            return;
        }

        _logger.LogInformation("PA prompt {Kind}: {Text}", due, PromptText);
        _eventLog.Record("pa.prompt", new
        {
            kind = due.ToString(),
            flightNumber = facts.FlightNumber,
            destination = facts.DestinationIcao,
            flightMinutes = facts.FlightMinutes,
            cruiseFl = facts.CruiseFlightLevel,
            etaUtc = facts.EtaUtc,
            weather = facts.WeatherSkyLabel,
            temperatureC = facts.WeatherTemperatureC,
        });
        _ = _arbiter.EnqueueAsync(new SpeechRequest(PromptText, SpeechPriority.Normal, Tag: "cabin.pa-prompt", Ttl: TimeSpan.FromSeconds(60)));
    }
}
