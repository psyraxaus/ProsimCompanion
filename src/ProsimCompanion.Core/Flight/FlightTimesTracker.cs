using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Flight;

/// <summary>The four block/flight stamps of the current leg, from the phase engine's edges.
/// Null = not happened yet this cycle.</summary>
/// <param name="TakeoffUnknown">Issue #167: the app was started (or restarted) in flight
/// with nothing to restore, so the takeoff never had an edge to stamp. The stamp stays null
/// — before this flag the tracker seeded takeoff = "now", and the 2026-10-10 debrief read
/// "0.7 t used" for a 5.2 t leg — and the debrief, logbook and fuel log say so instead.</param>
public sealed record FlightTimesSnapshot(
    DateTimeOffset? OffBlocksUtc,
    DateTimeOffset? TakeoffUtc,
    DateTimeOffset? LandingUtc,
    DateTimeOffset? OnBlocksUtc,
    double? OffBlocksFobKg = null,
    double? TakeoffFobKg = null,
    double? LandingFobKg = null,
    double? OnBlocksFobKg = null,
    bool TakeoffUnknown = false)
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
/// <para>
/// An app started in flight (issue #167) has no takeoff edge: the first commit is
/// Unknown → Cruise. Before 0.7.0 that seeded takeoff = now with the current fuel, and the
/// 2026-10-10 restart in the climb produced a leg-1 debrief of "0.7 t used" against a real
/// 5.2 t. Now the stamps restored from the flight-state snapshot are adopted when they fit
/// the first committed phase (<see cref="Accept"/>), and without them the takeoff stays
/// unknown — recorded as such, never invented.
/// </para>
/// </summary>
public sealed class FlightTimesCore
{
    /// <summary>A restored on-blocks stamp older than this when the first commit is already
    /// Shutdown belongs to an earlier leg, not the one being closed out.</summary>
    private static readonly TimeSpan OnBlocksRecent = TimeSpan.FromHours(2);

    private FlightTimesSnapshot _times = FlightTimesSnapshot.Empty;
    private FlightTimesSnapshot? _pendingRestore;
    private FlightPhase _phase = FlightPhase.Unknown;

    public FlightTimesSnapshot Current => _times;

    public void Reset()
    {
        _times = FlightTimesSnapshot.Empty;
        _pendingRestore = null;
    }

    /// <summary>
    /// A cross-feature cycle reset (<c>GroundOpsSignals.FlightCycleReset</c>) clears the
    /// stamps — unless the leg is still being closed out. The GSX automation raises that reset
    /// on the very edge that stamps on-blocks (TaxiIn → Shutdown, ticket t-20261010-0726:
    /// 07:15:22.396Z reset, 07:15:22.398Z stamp), so honouring it then wiped off-blocks,
    /// takeoff and landing two milliseconds before the fourth stamp landed and the debrief
    /// lost its block and flight times. While the last committed phase is an arrival phase
    /// the stamps belong to the leg that just ended; the next at-gate edge clears them.
    /// </summary>
    /// <returns>True when the stamps were cleared.</returns>
    public bool ResetForNewCycle()
    {
        if (_phase is FlightPhase.LandingRollout or FlightPhase.TaxiIn or FlightPhase.Shutdown)
        {
            return false;
        }

        _times = FlightTimesSnapshot.Empty;
        _pendingRestore = null;
        return true;
    }

    /// <summary>
    /// Takes stamps back from the flight-state snapshot (issue #167). Before the first
    /// commit they wait for it, so the phase can judge them; after it they are judged
    /// against the current phase at once. Returns the stamps now in force.
    /// </summary>
    public FlightTimesSnapshot Restore(FlightTimesSnapshot stamps, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(stamps);
        if (_phase == FlightPhase.Unknown)
        {
            _pendingRestore = stamps;
            return _times;
        }

        var accepted = Accept(stamps, _phase, nowUtc);
        if (accepted != FlightTimesSnapshot.Empty)
        {
            _times = accepted;
        }

        return _times;
    }

    /// <summary>
    /// Which restored stamps fit the phase the engine committed first. The rule reads the
    /// stamps as a story and keeps only the chapters that must already have happened: in the
    /// air, off-blocks and takeoff (a landing stamp would mean an older leg); on the landing
    /// roll or taxiing in, those plus the landing; at shutdown, all four while the on-blocks
    /// stamp is recent; pushing or taxiing out, off-blocks alone; parked at the gate, nothing
    /// — the stamps of a leg that ended are never carried into the next one.
    /// </summary>
    public static FlightTimesSnapshot Accept(FlightTimesSnapshot stamps, FlightPhase phase, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(stamps);
        var takeoffKnown = stamps.TakeoffUtc is not null || stamps.TakeoffUnknown;

        if (phase.IsAirborne())
        {
            return takeoffKnown && stamps.LandingUtc is null
                ? new FlightTimesSnapshot(stamps.OffBlocksUtc, stamps.TakeoffUtc, null, null,
                    stamps.OffBlocksFobKg, stamps.TakeoffFobKg, null, null, stamps.TakeoffUnknown)
                : FlightTimesSnapshot.Empty;
        }

        if (phase is FlightPhase.LandingRollout or FlightPhase.TaxiIn)
        {
            return takeoffKnown && stamps.OnBlocksUtc is null
                ? stamps with { OnBlocksUtc = null, OnBlocksFobKg = null }
                : FlightTimesSnapshot.Empty;
        }

        if (phase == FlightPhase.Shutdown)
        {
            var onBlocksFits = stamps.OnBlocksUtc is null || nowUtc - stamps.OnBlocksUtc.Value <= OnBlocksRecent;
            return takeoffKnown && stamps.LandingUtc is not null && onBlocksFits ? stamps : FlightTimesSnapshot.Empty;
        }

        if (phase is FlightPhase.PushbackAndStart or FlightPhase.TaxiOut or FlightPhase.TakeoffRoll)
        {
            return stamps.TakeoffUtc is null && !stamps.TakeoffUnknown && stamps.OffBlocksUtc is not null
                ? new FlightTimesSnapshot(stamps.OffBlocksUtc, null, null, null, stamps.OffBlocksFobKg)
                : FlightTimesSnapshot.Empty;
        }

        return FlightTimesSnapshot.Empty;
    }

    /// <summary>Applies one committed phase transition and stamps the time — and the fuel on
    /// board at that moment (issue #155: the debrief's burn is takeoff fuel minus on-blocks
    /// fuel, never a cruise check) — for each phase edge. <paramref name="fobKg"/> is null
    /// when ProSim has no figure; the time still stamps. Returns the (possibly unchanged) stamps.</summary>
    public FlightTimesSnapshot Apply(FlightPhase previous, FlightPhase current, DateTimeOffset nowUtc, double? fobKg = null)
    {
        _phase = current;

        // The first commit of the session: restored stamps are judged against it (#167).
        if (previous == FlightPhase.Unknown && _pendingRestore is { } pending)
        {
            _pendingRestore = null;
            var accepted = Accept(pending, current, nowUtc);
            if (accepted != FlightTimesSnapshot.Empty)
            {
                _times = accepted;
            }
        }

        // The next turnaround: shutdown (or taxi-in) → back at the gate, or straight into the
        // next push/start (turnaround-start) — a new leg either way.
        if (previous is FlightPhase.Shutdown or FlightPhase.TaxiIn && (current.IsAtGate() || current == FlightPhase.PushbackAndStart))
        {
            _times = FlightTimesSnapshot.Empty;
            if (current != FlightPhase.PushbackAndStart)
            {
                return _times;
            }
        }

        switch (current)
        {
            case FlightPhase.PushbackAndStart or FlightPhase.TaxiOut when _times.OffBlocksUtc is null:
                _times = _times with { OffBlocksUtc = nowUtc, OffBlocksFobKg = fobKg };
                break;
            case FlightPhase.InitialClimb or FlightPhase.Climb or FlightPhase.Cruise or FlightPhase.Descent or FlightPhase.Approach
                when _times.TakeoffUtc is null && !_times.TakeoffUnknown:
                if (previous == FlightPhase.Unknown)
                {
                    // Started in flight with nothing restored (#167): there was no takeoff
                    // edge, so there is no takeoff time. Say so; never seed "now".
                    _times = _times with { TakeoffUnknown = true };
                    break;
                }

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
                if (previous == FlightPhase.Unknown && _times.TakeoffUtc is null)
                {
                    // Started on the landing roll: the touchdown is now-ish, the takeoff is gone.
                    _times = _times with { TakeoffUnknown = true };
                }

                _times = _times with { LandingUtc = nowUtc, LandingFobKg = fobKg };
                break;
            case FlightPhase.TaxiIn when previous == FlightPhase.Unknown && _times.TakeoffUtc is null && _times.LandingUtc is null:
                _times = _times with { TakeoffUnknown = true };
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
/// overnight sim on a real-world afternoon reads sim time, like every other timestamp. Also
/// the <c>flightTimes</c> part of the flight-state snapshot (issue #167).
/// </summary>
public sealed class FlightTimesTracker : IDisposable, IFlightStatePart
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

    // ---- flight-state snapshot part (#167) ----------------------------------------------------

    public string Key => "flightTimes";

    /// <summary>After the notification latches (5): the store publish below would otherwise
    /// raise "landed" again for a restored landing stamp.</summary>
    public int RestoreOrder => 20;

    public event Action? Changed;

    public JsonNode? Capture()
    {
        var times = _store.Snapshot();
        return times == FlightTimesSnapshot.Empty ? null : JsonSerializer.SerializeToNode(times, FlightStateSnapshotStore.JsonOptions);
    }

    public void Restore(JsonNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var stamps = node.Deserialize<FlightTimesSnapshot>(FlightStateSnapshotStore.JsonOptions);
        if (stamps is null || stamps == FlightTimesSnapshot.Empty)
        {
            return;
        }

        FlightTimesSnapshot times;
        lock (_gate)
        {
            times = _core.Restore(stamps, _clock.UtcNowOrReal);
        }

        _logger.LogInformation(
            "Flight times restored from the flight-state snapshot: off {Off:HH:mm}Z takeoff {Takeoff:HH:mm}Z landing {Landing:HH:mm}Z on {On:HH:mm}Z{Pending}",
            stamps.OffBlocksUtc, stamps.TakeoffUtc, stamps.LandingUtc, stamps.OnBlocksUtc,
            times == FlightTimesSnapshot.Empty ? " (held until the first phase commit judges them)" : "");
        Publish(times, "restored", "restored");
    }

    // -------------------------------------------------------------------------------------------

    private void OnPhaseChanged(object? sender, FlightPhaseChangedEventArgs e)
    {
        FlightTimesSnapshot times;
        lock (_gate)
        {
            times = _core.Apply(e.Previous, e.Current, _clock.UtcNowOrReal, FuelOnBoardKg());
        }

        Publish(times, e.Previous.ToString(), e.Current.ToString());
    }

    private void Publish(FlightTimesSnapshot times, string previous, string current)
    {
        if (_store.Snapshot() != times)
        {
            _logger.LogInformation(
                "Flight times: off {Off:HH:mm}Z takeoff {Takeoff:HH:mm}Z landing {Landing:HH:mm}Z on {On:HH:mm}Z ({Previous} → {Current}){Unknown}",
                times.OffBlocksUtc, times.TakeoffUtc, times.LandingUtc, times.OnBlocksUtc, previous, current,
                times.TakeoffUnknown ? " — takeoff time unknown, the app started in flight" : "");

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
                    takeoffUnknown = times.TakeoffUnknown ? true : (bool?)null,
                });
            }
        }

        _store.Update(_ => times);
        Changed?.Invoke();
    }

    private void OnFlightCycleReset()
    {
        bool cleared;
        lock (_gate)
        {
            cleared = _core.ResetForNewCycle();
        }

        if (!cleared)
        {
            _logger.LogDebug("Flight times: cycle reset ignored — the arrival is still being stamped");
            return;
        }

        _store.Update(_ => FlightTimesSnapshot.Empty);
        Changed?.Invoke();
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
