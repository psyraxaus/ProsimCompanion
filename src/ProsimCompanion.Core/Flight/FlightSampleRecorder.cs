using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;

namespace ProsimCompanion.Core.Flight;

/// <summary>
/// Pure decision core of the recorder: given the engine's view and the clock, the sample to
/// write or null. Unchanged samples are suppressed until the keep-alive elapses, so a parked
/// hour costs a few hundred bytes while a replay still knows the state held.
/// </summary>
public sealed class FlightSampleRecorderCore
{
    private FlightSample? _last;
    private DateTimeOffset _lastAt;

    /// <summary>Returns the sample to record for this tick, or null to skip it.</summary>
    public FlightSample? Next(FlightStateView view, DateTimeOffset now, FlightStateOptions options)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.RecordSamples || !view.IsLive || view.Data is null)
        {
            // Not live = nothing worth replaying; forget the last sample so the first live
            // one is always written.
            _last = null;
            return null;
        }

        var sample = FlightSample.From(view);
        if (_last is not null && sample == _last
            && now - _lastAt < TimeSpan.FromSeconds(Math.Max(1, options.SampleKeepAliveSeconds)))
        {
            return null;
        }

        _last = sample;
        _lastAt = now;
        return sample;
    }
}

/// <summary>
/// Writes the <c>flight-sample</c> event to the session log at
/// <see cref="FlightStateOptions.SampleIntervalSeconds"/> while the flight is live, so any
/// flight can be replayed through the phase engine offline (<see cref="FlightReplay"/>).
/// Every phase fix so far had to be reconstructed from commit lines; the recording makes
/// the flight itself the regression test.
/// </summary>
public sealed class FlightSampleRecorder : IHostedService, IDisposable
{
    private readonly IFlightPhaseSource _flight;
    private readonly JsonlEventLog _eventLog;
    private readonly IOptionsMonitor<FlightStateOptions> _options;
    private readonly ILogger<FlightSampleRecorder> _logger;
    private readonly FlightSampleRecorderCore _core = new();
    private readonly Timer _timer;
    private TimeSpan _period = Timeout.InfiniteTimeSpan;
    private int _ticking;

    public FlightSampleRecorder(
        IFlightPhaseSource flight,
        JsonlEventLog eventLog,
        IOptionsMonitor<FlightStateOptions> options,
        ILogger<FlightSampleRecorder> logger)
    {
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _flight = flight;
        _eventLog = eventLog;
        _options = options;
        _logger = logger;
        _timer = new Timer(_ => Tick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Rearm();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        return Task.CompletedTask;
    }

    public void Dispose() => _timer.Dispose();

    private void Rearm()
    {
        var seconds = Math.Clamp(_options.CurrentValue.SampleIntervalSeconds, 0.25, 60);
        var period = TimeSpan.FromSeconds(seconds);
        if (period == _period)
        {
            return;
        }

        _period = period;
        _timer.Change(period, period);
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            var sample = _core.Next(_flight.Snapshot(), DateTimeOffset.UtcNow, _options.CurrentValue);
            if (sample is not null)
            {
                _eventLog.Record("flight-sample", sample);
            }

            Rearm(); // the interval is hot-reloadable
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Flight sample recorder tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }
}
