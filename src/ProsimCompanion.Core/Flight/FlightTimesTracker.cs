using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Flight;

/// <summary>The four block/flight stamps of the current leg, from the phase engine's edges.
/// Null = not happened yet this cycle.</summary>
public sealed record FlightTimesSnapshot(
    DateTimeOffset? OffBlocksUtc,
    DateTimeOffset? TakeoffUtc,
    DateTimeOffset? LandingUtc,
    DateTimeOffset? OnBlocksUtc,
    double? OffBlocksFobKg = null,
    double? TakeoffFobKg = null,
    double? LandingFobKg = null,
    double? OnBlocksFobKg = null)
{
    public static FlightTimesSnapshot Empty { get; } = new(null, null, null, null);

    /// <summary>Takeoff fuel minus on-blocks fuel (landing fuel while still taxiing in) —
    /// the debrief's "fuel used" (issue #155). Null until both ends exist.</summary>
    public double? FuelUsedKg
        => TakeoffFobKg is { } start && (OnBlocksFobKg ?? LandingFobKg) is { } end && start >= end ? start - end : null;

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
    /// <summary>Stamps the time — and the fuel on board at that moment (issue #155: the
    /// debrief's burn is takeoff fuel minus on-blocks fuel, never a cruise check) — for each
    /// phase edge. <paramref name="fobKg"/> is null when ProSim has no figure; the time still stamps.</summary>
    public FlightTimesSnapshot Apply(FlightPhase previous, FlightPhase current, DateTimeOffset nowUtc, double? fobKg = null)
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
                _times = _times with { OffBlocksUtc = nowUtc, OffBlocksFobKg = fobKg };
                break;
            case FlightPhase.InitialClimb or FlightPhase.Climb or FlightPhase.Cruise or FlightPhase.Descent or FlightPhase.Approach
                when _times.TakeoffUtc is null:
                // Off-blocks may be missing when the app started on the runway: backfill so
                // block time is never shorter than flight time.
                _times = _times with
                {
                    TakeoffUtc = nowUtc,
                    TakeoffFobKg = fobKg,
                    OffBlocksUtc = _times.OffBlocksUtc ?? nowUtc,
                    OffBlocksFobKg = _times.OffBlocksUtc is null ? fobKg : _times.OffBlocksFobKg,
                };
                break;
            case FlightPhase.LandingRollout when _times.LandingUtc is null:
                _times = _times with { LandingUtc = nowUtc, LandingFobKg = fobKg };
                break;
            case FlightPhase.Shutdown when _times.OnBlocksUtc is null:
                _times = _times with { OnBlocksUtc = nowUtc, OnBlocksFobKg = fobKg };
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
    /// <summary>Session event carrying the four stamps (sim-clock UTC) on every change.</summary>
    public const string EventType = "flight-times";

    private readonly IFlightPhaseSource _flight;
    private readonly GroundOpsSignals _signals;
    private readonly ISimClock _clock;
    private readonly FlightTimesStore _store;
    private readonly ILogger<FlightTimesTracker> _logger;
    private readonly JsonlEventLog? _eventLog;
    private readonly FlightTimesCore _core = new();
    private readonly object _gate = new();
    // Fuel on board at each stamp (issue #155). Optional so a host without ProSim still tracks times.
    private readonly IDataRefSubscription<double>? _fuelTotal;

    public FlightTimesTracker(
        IFlightPhaseSource flight,
        GroundOpsSignals signals,
        ISimClock clock,
        FlightTimesStore store,
        ILogger<FlightTimesTracker> logger,
        JsonlEventLog? eventLog = null,
        IProsimDataRefs? dataRefs = null)
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
        _eventLog = eventLog;
        _fuelTotal = dataRefs?.Subscribe(ProsimDataRefNames.FuelTotal);

        _flight.PhaseChanged += OnPhaseChanged;
        _signals.FlightCycleReset += OnFlightCycleReset;
    }

    private void OnPhaseChanged(object? sender, FlightPhaseChangedEventArgs e)
    {
        FlightTimesSnapshot times;
        lock (_gate)
        {
            times = _core.Apply(e.Previous, e.Current, _clock.UtcNowOrReal, FuelOnBoardKg());
        }

        if (_store.Snapshot() != times)
        {
            _logger.LogInformation(
                "Flight times: off {Off:HH:mm}Z takeoff {Takeoff:HH:mm}Z landing {Landing:HH:mm}Z on {On:HH:mm}Z ({Previous} → {Current})",
                times.OffBlocksUtc, times.TakeoffUtc, times.LandingUtc, times.OnBlocksUtc, e.Previous, e.Current);

            // The stamps ride in the session log (issue #146) so the logbook can carry them
            // from any folded or backfilled session. A reset to "all empty" is not recorded:
            // the extractor keeps the first stamp of each kind, so it would say nothing.
            if (times != FlightTimesSnapshot.Empty)
            {
                _eventLog?.Record(EventType, new
                {
                    offBlocksUtc = times.OffBlocksUtc,
                    takeoffUtc = times.TakeoffUtc,
                    landingUtc = times.LandingUtc,
                    onBlocksUtc = times.OnBlocksUtc,
                    offBlocksFobKg = Round(times.OffBlocksFobKg),
                    takeoffFobKg = Round(times.TakeoffFobKg),
                    landingFobKg = Round(times.LandingFobKg),
                    onBlocksFobKg = Round(times.OnBlocksFobKg),
                });
            }
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

    private double? FuelOnBoardKg()
        => _fuelTotal is { RawValue: not null, IsStale: false } sub && sub.Value > 0 ? sub.Value : null;

    private static double? Round(double? kg) => kg is { } v ? Math.Round(v) : null;

    public void Dispose()
    {
        _flight.PhaseChanged -= OnPhaseChanged;
        _signals.FlightCycleReset -= OnFlightCycleReset;
        _fuelTotal?.Dispose();
    }
}
