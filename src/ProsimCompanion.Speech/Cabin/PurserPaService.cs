using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Arbiter;

namespace ProsimCompanion.Speech.Cabin;

/// <summary>
/// The purser's cabin PAs heard from the flight deck (issue #186): a 1 Hz shell over
/// <see cref="PurserPaCore"/>, spoken in the purser voice through the cabin filter at Low
/// priority (the PA is background to the flight deck). Off by <c>cabin.purserPaAudible</c>.
/// Read-and-speak only.
/// </summary>
public sealed class PurserPaService : Core.Hosting.IStartupModule, IDisposable
{
    private readonly IOptionsMonitor<CabinOptions> _options;
    private readonly IFlightPhaseSource _flight;
    private readonly IFlightDataSource _flightData;
    private readonly IGroundFactsSource _groundFacts;
    private readonly GroundOpsSignals _signals;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<PurserPaService> _logger;
    private readonly PurserPaCore _core = new();
    private readonly object _gate = new();
    private Timer? _timer;
    private int _ticking;

    public PurserPaService(
        IOptionsMonitor<CabinOptions> options,
        IFlightPhaseSource flight,
        IFlightDataSource flightData,
        IGroundFactsSource groundFacts,
        GroundOpsSignals signals,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        ILogger<PurserPaService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(flightData);
        ArgumentNullException.ThrowIfNull(groundFacts);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _flight = flight;
        _flightData = flightData;
        _groundFacts = groundFacts;
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
        _signals.FlightCycleReset += OnReset;
        _flight.SessionReset += OnReset;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _signals.FlightCycleReset -= OnReset;
        _flight.SessionReset -= OnReset;
    }

    private void OnReset()
    {
        lock (_gate)
        {
            _core.Reset();
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
            Tick(_options.CurrentValue);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Purser PA tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    /// <summary>One evaluation; the edges are primed even while the switch is off so turning
    /// it on mid-flight never fires a stale edge.</summary>
    public void Tick(CabinOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!_flight.IsLive)
        {
            return;
        }

        var view = _flight.Snapshot();
        if (view.Data is not { IsValid: true } data)
        {
            return;
        }

        var facts = _groundFacts.Facts;
        PurserPaKind? kind;
        lock (_gate)
        {
            kind = _core.Evaluate(new PurserPaSample(
                view.Phase, facts.BeaconOn, facts.EnginesOff, facts.Parked, view.HasBeenAirborneThisSession, data.AltitudeFt));
        }

        if (kind is not { } due || !options.Enabled || !options.PurserPaAudible)
        {
            return;
        }

        var text = due switch
        {
            PurserPaKind.ArmDoors => options.PaArmDoorsText,
            PurserPaKind.DoorsManual => options.PaDoorsManualText,
            _ => options.PaPrepareLandingText,
        };
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _logger.LogInformation("Purser PA {Kind}: {Text}", due, text);
        _eventLog.Record("cabin.pa", new { kind = due.ToString(), text, phase = view.Phase.ToString() });
        _ = _arbiter.EnqueueAsync(new SpeechRequest(
            text, SpeechPriority.Low, Tag: "cabin.pa." + due.ToString().ToLowerInvariant(),
            Ttl: TimeSpan.FromSeconds(45), Role: SpeechRole.Purser));
    }
}
