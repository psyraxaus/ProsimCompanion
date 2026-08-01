using Microsoft.Extensions.Logging;

namespace ProsimCompanion.Core.Flight;

/// <summary>
/// Central flight-phase state machine: samples the <see cref="IFlightDataSource"/> every
/// 250 ms, derives the target phase via <see cref="FlightPhaseEvaluator"/>, and commits a
/// transition only after the target has persisted for that transition's debounce interval.
/// <see cref="PhaseChanged"/> fires on the timer thread — consumers marshal themselves.
/// </summary>
public sealed class FlightStateEngine : IDisposable
{
    /// <summary>Sampling cadence.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(1000);

    /// <summary>Per-target-phase debounce overrides. Runway/rollout transitions commit fast —
    /// callouts hang off them; slow-moving phases tolerate more smoothing.</summary>
    private static readonly Dictionary<FlightPhase, TimeSpan> DebounceOverrides = new()
    {
        [FlightPhase.TakeoffRoll] = TimeSpan.Zero,
        [FlightPhase.InitialClimb] = TimeSpan.Zero,
        [FlightPhase.LandingRollout] = TimeSpan.Zero,
        [FlightPhase.Cruise] = TimeSpan.FromMilliseconds(5000),
    };

    private readonly IFlightDataSource _source;
    private readonly ILogger<FlightStateEngine> _logger;
    private readonly Timer _timer;
    private FlightPhase _pendingTarget = FlightPhase.Unknown;
    private DateTimeOffset _pendingSince;
    private int _ticking;

    public FlightStateEngine(IFlightDataSource source, ILogger<FlightStateEngine> logger)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(logger);

        _source = source;
        _logger = logger;
        _timer = new Timer(_ => Tick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The committed phase.</summary>
    public FlightPhase CurrentPhase { get; private set; } = FlightPhase.Unknown;

    /// <summary>Raised after a committed transition, on the timer thread.</summary>
    public event EventHandler<FlightPhaseChangedEventArgs>? PhaseChanged;

    /// <summary>Starts sampling.</summary>
    public void Start() => _timer.Change(TimeSpan.Zero, TickInterval);

    /// <summary>One evaluation step — exposed for tests; the timer calls this every tick.</summary>
    public void ProcessTick(FlightDataSnapshot snapshot, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var target = FlightPhaseEvaluator.Evaluate(snapshot, CurrentPhase);
        if (target == CurrentPhase)
        {
            _pendingTarget = CurrentPhase;
            return;
        }

        if (target != _pendingTarget)
        {
            _pendingTarget = target;
            _pendingSince = nowUtc;
        }

        var debounce = DebounceOverrides.GetValueOrDefault(target, DefaultDebounce);
        if (nowUtc - _pendingSince < debounce)
        {
            return;
        }

        var previous = CurrentPhase;
        CurrentPhase = target;
        _logger.LogInformation("Flight phase {Previous} -> {Current}", previous, target);
        PhaseChanged?.Invoke(this, new FlightPhaseChangedEventArgs(previous, target));
    }

    public void Dispose() => _timer.Dispose();

    private void Tick()
    {
        // Reentrancy guard: a slow tick must never overlap the next one.
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            ProcessTick(_source.Sample(), DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Flight state tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }
}

/// <summary>Payload of <see cref="FlightStateEngine.PhaseChanged"/>.</summary>
public sealed class FlightPhaseChangedEventArgs : EventArgs
{
    public FlightPhaseChangedEventArgs(FlightPhase previous, FlightPhase current)
    {
        Previous = previous;
        Current = current;
    }

    public FlightPhase Previous { get; }
    public FlightPhase Current { get; }
}
