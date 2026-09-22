using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Flight;

/// <summary>The four block/flight stamps of the current leg, from the phase engine's edges.
/// Null = not happened yet this cycle.</summary>
public sealed record FlightTimesSnapshot(
    DateTimeOffset? OffBlocksUtc,
    DateTimeOffset? TakeoffUtc,
    DateTimeOffset? LandingUtc,
    DateTimeOffset? OnBlocksUtc)
{
    public static FlightTimesSnapshot Empty { get; } = new(null, null, null, null);

    /// <summary>Block time so far (off-blocks → on-blocks, or → now while still on the leg).</summary>
    public TimeSpan? BlockTime(DateTimeOffset nowUtc)
        => OffBlocksUtc is { } off ? (OnBlocksUtc ?? nowUtc) - off : null;

    /// <summary>Airborne time so far (takeoff → landing, or → now while airborne).</summary>
    public TimeSpan? FlightTime(DateTimeOffset nowUtc)
        => TakeoffUtc is { } off ? (LandingUtc ?? nowUtc) - off : null;
}

/// <summary>
/// Live block/flight stamps for the current leg — the Flight Monitor board's progress line,
/// ETA and block-time figures read these. Written by <see cref="FlightTimesTracker"/>.
/// </summary>
public sealed class FlightTimesStore : SnapshotStore<FlightTimesSnapshot>
{
    public FlightTimesStore()
        : base(FlightTimesSnapshot.Empty)
    {
    }
}

/// <summary>
/// Pure stamping rule (testable without the engine): the first edge INTO pushback/taxi-out
/// is off-blocks, the first edge into the airborne phases is takeoff, the first edge into
/// the landing roll is landing, the first edge into shutdown is on-blocks. A new leg (back
/// at the gate after shutdown, or an explicit cycle reset) clears everything. Stamps never
/// move once set, so a phase-engine wobble (cruise → climb → cruise) cannot rewrite them.
/// </summary>
public sealed class FlightTimesCore
{
    private FlightTimesSnapshot _times = FlightTimesSnapshot.Empty;

    public FlightTimesSnapshot Current => _times;

    public void Reset() => _times = FlightTimesSnapshot.Empty;

    /// <summary>Applies one committed phase transition; returns the (possibly unchanged) stamps.</summary>
    public FlightTimesSnapshot Apply(FlightPhase previous, FlightPhase current, DateTimeOffset nowUtc)
    {
        // The next turnaround: shutdown (or taxi-in) → back at the gate.
        if (previous is FlightPhase.Shutdown or FlightPhase.TaxiIn && current.IsAtGate())
        {
            _times = FlightTimesSnapshot.Empty;
            return _times;
        }

        switch (current)
        {
            case FlightPhase.PushbackAndStart or FlightPhase.TaxiOut when _times.OffBlocksUtc is null:
                _times = _times with { OffBlocksUtc = nowUtc };
                break;
            case FlightPhase.InitialClimb or FlightPhase.Climb or FlightPhase.Cruise or FlightPhase.Descent or FlightPhase.Approach
                when _times.TakeoffUtc is null:
                // Off-blocks may be missing when the app started on the runway: backfill so
                // block time is never shorter than flight time.
                _times = _times with { TakeoffUtc = nowUtc, OffBlocksUtc = _times.OffBlocksUtc ?? nowUtc };
                break;
            case FlightPhase.LandingRollout when _times.LandingUtc is null:
                _times = _times with { LandingUtc = nowUtc };
                break;
            case FlightPhase.Shutdown when _times.OnBlocksUtc is null:
                _times = _times with { OnBlocksUtc = nowUtc };
                break;
        }

        return _times;
    }
}

/// <summary>
/// Feeds <see cref="FlightTimesCore"/> from the phase engine and publishes to
/// <see cref="FlightTimesStore"/>. Startup module; the sim clock supplies the stamps so an
/// overnight sim on a real-world afternoon reads sim time, like every other timestamp.
/// </summary>
public sealed class FlightTimesTracker : IDisposable
{
    private readonly IFlightPhaseSource _flight;
    private readonly GroundOpsSignals _signals;
    private readonly ISimClock _clock;
    private readonly FlightTimesStore _store;
    private readonly ILogger<FlightTimesTracker> _logger;
    private readonly FlightTimesCore _core = new();
    private readonly object _gate = new();

    public FlightTimesTracker(
        IFlightPhaseSource flight,
        GroundOpsSignals signals,
        ISimClock clock,
        FlightTimesStore store,
        ILogger<FlightTimesTracker> logger)
    {
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        _flight = flight;
        _signals = signals;
        _clock = clock;
        _store = store;
        _logger = logger;

        _flight.PhaseChanged += OnPhaseChanged;
        _signals.FlightCycleReset += OnFlightCycleReset;
    }

    private void OnPhaseChanged(object? sender, FlightPhaseChangedEventArgs e)
    {
        FlightTimesSnapshot times;
        lock (_gate)
        {
            times = _core.Apply(e.Previous, e.Current, _clock.UtcNowOrReal);
        }

        if (_store.Snapshot() != times)
        {
            _logger.LogInformation(
                "Flight times: off {Off:HH:mm}Z takeoff {Takeoff:HH:mm}Z landing {Landing:HH:mm}Z on {On:HH:mm}Z ({Previous} → {Current})",
                times.OffBlocksUtc, times.TakeoffUtc, times.LandingUtc, times.OnBlocksUtc, e.Previous, e.Current);
        }

        _store.Update(_ => times);
    }

    private void OnFlightCycleReset()
    {
        lock (_gate)
        {
            _core.Reset();
        }

        _store.Update(_ => FlightTimesSnapshot.Empty);
    }

    public void Dispose()
    {
        _flight.PhaseChanged -= OnPhaseChanged;
        _signals.FlightCycleReset -= OnFlightCycleReset;
    }
}
