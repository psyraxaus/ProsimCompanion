using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;

namespace ProsimCompanion.Core.Flight;

/// <summary>
/// The 100 ms shell over <see cref="TurbulenceDetector"/> (issue #186): one subscription to
/// <c>aircraft.acceleration.Y</c> (the touchdown recorder's, shared by the read model), the
/// engine's snapshot for ground contact and phase, the store for the consumers, and the
/// <c>turbulence.level</c> / <c>turbulence.episode-ended</c> session events. Runs only with
/// <c>cabin.turbulenceDetection</c> on (off by default — nothing changes in the session log
/// until the pilot opts in). Read-only.
/// </summary>
public sealed class TurbulenceMonitor : Hosting.IStartupModule, IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);

    private readonly IFlightPhaseSource _flight;
    private readonly IDataRefSubscription<double> _accelerationY;
    private readonly IOptionsMonitor<CabinOptions> _options;
    private readonly TurbulenceStore _store;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<TurbulenceMonitor> _logger;
    private readonly object _gate = new();
    private TurbulenceDetector _detector;
    private (TurbulenceThresholds Thresholds, AccelerationUnit Unit) _configured;
    private Timer? _timer;
    private int _ticking;
    private bool _wasLive;
    private DateTimeOffset _lastPeakPublish;

    public TurbulenceMonitor(
        IFlightPhaseSource flight,
        IProsimDataRefs dataRefs,
        IOptionsMonitor<CabinOptions> options,
        TurbulenceStore store,
        JsonlEventLog eventLog,
        ILogger<TurbulenceMonitor> logger)
    {
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);
        _flight = flight;
        _options = options;
        _store = store;
        _eventLog = eventLog;
        _logger = logger;
        _accelerationY = dataRefs.Subscribe(ProsimDataRefNames.AccelerationYRaw);
        _configured = Configured(options.CurrentValue);
        _detector = new TurbulenceDetector(_configured.Thresholds, _configured.Unit);
    }

    public void Start() => _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(2), TickInterval);

    public void Dispose()
    {
        _timer?.Dispose();
        _accelerationY.Dispose();
    }

    /// <summary>One sample through the detector; the timer calls this, tests call it with
    /// their own clock.</summary>
    public void Process(DateTimeOffset nowUtc)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled || !options.TurbulenceDetection)
        {
            if (_store.Snapshot() != TurbulenceSnapshot.Empty)
            {
                lock (_gate)
                {
                    _detector.Reset();
                }

                _store.Update(_ => TurbulenceSnapshot.Empty);
            }

            return;
        }

        var view = _flight.Snapshot();
        if (!view.IsLive || view.Data is not { IsValid: true } data)
        {
            if (_wasLive)
            {
                _wasLive = false;
                lock (_gate)
                {
                    _detector.Reset();
                }

                _store.Update(_ => TurbulenceSnapshot.Empty);
            }

            return;
        }

        _wasLive = true;
        TurbulenceStep step;
        TurbulenceSnapshot snapshot;
        TurbulenceLevel previous;
        lock (_gate)
        {
            var configured = Configured(options);
            if (configured != _configured)
            {
                // Thresholds or the unit override changed: a fresh detector, the baseline relearned.
                _configured = configured;
                _detector = new TurbulenceDetector(configured.Thresholds, configured.Unit);
            }

            previous = _detector.Level;
            var raw = _accelerationY.RawValue is null ? (double?)null : _accelerationY.Value;
            step = _detector.Process(new TurbulenceSample(nowUtc, raw, data.OnGround, data.GroundSpeedKt, view.Phase.IsAirborne()));
            snapshot = new TurbulenceSnapshot(
                _detector.Level, Math.Round(_detector.PeakG, 2), _detector.Unit, _detector.BaselineRaw is { } b ? Math.Round(b, 3) : null,
                _detector.Calibrated, _detector.EpisodeActive, _detector.EpisodeStartedAtUtc, _detector.LastChangeAtUtc);
        }

        var current = _store.Snapshot();
        var structural = current.Level != snapshot.Level || current.EpisodeActive != snapshot.EpisodeActive
            || current.Unit != snapshot.Unit || current.Calibrated != snapshot.Calibrated;
        if (structural || nowUtc - _lastPeakPublish >= TimeSpan.FromSeconds(1))
        {
            _lastPeakPublish = nowUtc;
            if (current.Calibrated != snapshot.Calibrated && snapshot.Calibrated)
            {
                _logger.LogInformation(
                    "Turbulence baseline learned at rest: acceleration.Y {Baseline} raw → unit {Unit} (scale {Scale:F4} to G)",
                    snapshot.BaselineRaw, snapshot.Unit, TurbulenceDetector.ScaleToG(snapshot.Unit));
                _eventLog.Record("turbulence.calibrated", new { baselineRaw = snapshot.BaselineRaw, unit = snapshot.Unit.ToString() });
            }

            _store.Update(_ => snapshot);
        }

        if (step.LevelChanged)
        {
            _logger.LogInformation("Turbulence {Level} (was {Previous}): peak {PeakG:F2} g over the window, unit {Unit}",
                snapshot.Level, previous, snapshot.PeakG, snapshot.Unit);
            _eventLog.Record("turbulence.level", new
            {
                level = snapshot.Level.ToString(),
                previous = previous.ToString(),
                peakG = snapshot.PeakG,
                unit = snapshot.Unit.ToString(),
                baselineRaw = snapshot.BaselineRaw,
                calibrated = snapshot.Calibrated,
                episodeStartedUtc = snapshot.EpisodeStartedAtUtc,
            });
        }

        if (step.EpisodeEnded)
        {
            _logger.LogInformation("Turbulence episode over — two minutes below light");
            _eventLog.Record("turbulence.episode-ended", new { startedUtc = current.EpisodeStartedAtUtc, endedUtc = nowUtc });
            _store.RaiseEpisodeEnded();
        }
    }

    private static (TurbulenceThresholds Thresholds, AccelerationUnit Unit) Configured(CabinOptions options)
        => (new TurbulenceThresholds(
                Math.Max(0.01, options.TurbulenceLightG),
                Math.Max(options.TurbulenceLightG, options.TurbulenceModerateG),
                Math.Max(options.TurbulenceModerateG, options.TurbulenceSevereG)),
            ParseUnit(options.TurbulenceUnit));

    /// <summary>The <c>cabin.turbulenceUnit</c> words → the unit; anything else = auto.</summary>
    public static AccelerationUnit ParseUnit(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "g" => AccelerationUnit.G,
        "gnogravity" => AccelerationUnit.GWithoutGravity,
        "mps2" => AccelerationUnit.MetresPerSecondSquared,
        "ftps2" => AccelerationUnit.FeetPerSecondSquared,
        _ => AccelerationUnit.Unknown,
    };

    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            Process(DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            // Timer thread: anything that escapes terminates the process.
            _logger.LogWarning(ex, "Turbulence monitor tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }
}
