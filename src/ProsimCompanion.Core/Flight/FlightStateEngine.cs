using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Flight;

/// <summary>Read-only view of the committed flight phase — the narrow seam consumers (callout
/// engines, monitors) depend on so tests can drive phases directly.</summary>
public interface IFlightPhaseSource
{
    /// <summary>The committed phase.</summary>
    FlightPhase CurrentPhase { get; }

    /// <summary>Raised after a committed transition, on the engine's timer thread.</summary>
    event EventHandler<FlightPhaseChangedEventArgs>? PhaseChanged;
}

/// <summary>
/// Central flight-phase state machine: samples the <see cref="IFlightDataSource"/> every
/// 250 ms, derives the target phase via <see cref="FlightPhaseEvaluator"/>, and commits a
/// transition only after the target has persisted for that transition's debounce interval.
/// Classification is gated on the MSFS session being live AND the data source reporting its
/// dataref set fully registered (<see cref="FlightDataSnapshot.IsReady"/>) — without both,
/// the engine holds its phase (Unknown at startup) rather than classifying garbage. If ProSim
/// or MSFS never appear the engine simply stays Unknown (degrade, not fail).
/// <see cref="PhaseChanged"/> fires on the timer thread — consumers marshal themselves.
/// </summary>
public sealed class FlightStateEngine : IFlightPhaseSource, IDisposable
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

    /// <summary>Per-(from, to) debounce overrides — these win over the per-target map.
    /// Departure regressions to Preflight must be deliberate, not a data blip: they require
    /// multi-second contradictory evidence (issue #59 — a transient engines-off read mid-taxi
    /// must not walk the ground automation back to Preflight, yet a genuine correction, like
    /// the 2026-08-16 PushbackAndStart→Preflight recovery from a bogus startup phase, still
    /// commits after the hold). Descent→Cruise gets a long settle so the level segments of a
    /// step descent do not flip-flop (four flips in 23 min on the 2026-08-16 flight).</summary>
    private static readonly Dictionary<(FlightPhase From, FlightPhase To), TimeSpan> TransitionDebounceOverrides = new()
    {
        [(FlightPhase.PushbackAndStart, FlightPhase.Preflight)] = TimeSpan.FromSeconds(5),
        [(FlightPhase.TaxiOut, FlightPhase.Preflight)] = TimeSpan.FromSeconds(5),
        [(FlightPhase.TakeoffRoll, FlightPhase.Preflight)] = TimeSpan.FromSeconds(5),
        [(FlightPhase.Descent, FlightPhase.Cruise)] = TimeSpan.FromSeconds(15),
    };

    private readonly IFlightDataSource _source;
    private readonly SimSessionStore _session;
    private readonly ILogger<FlightStateEngine> _logger;
    private readonly Timer _timer;
    private FlightPhase _pendingTarget = FlightPhase.Unknown;
    private DateTimeOffset _pendingSince;
    private bool _classifying;
    private int _ticking;

    public FlightStateEngine(IFlightDataSource source, SimSessionStore session, ILogger<FlightStateEngine> logger)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(logger);

        _source = source;
        _session = session;
        _logger = logger;
        _timer = new Timer(_ => Tick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The committed phase.</summary>
    public FlightPhase CurrentPhase { get; private set; } = FlightPhase.Unknown;

    /// <summary>The most recent sample — diagnostics surface for the Status page.</summary>
    public FlightDataSnapshot? LastSnapshot { get; private set; }

    /// <summary>True once a committed phase has been airborne in THIS sim session; reset when
    /// the pilot leaves the flight (session goes NotInSession). Write-safety consumers key off
    /// it (issue #59): the GSX FOB restore must never run at startup, and the cabin "ready for
    /// landing" report must never fire without a flight having actually happened.</summary>
    public bool HasBeenAirborneThisSession { get; private set; }

    /// <summary>Raised after a committed transition, on the timer thread.</summary>
    public event EventHandler<FlightPhaseChangedEventArgs>? PhaseChanged;

    /// <summary>Starts sampling.</summary>
    public void Start() => _timer.Change(TimeSpan.Zero, TickInterval);

    /// <summary>One evaluation step — exposed for tests; the timer calls this every tick.</summary>
    public void ProcessTick(FlightDataSnapshot snapshot, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        LastSnapshot = snapshot;

        var session = _session.Snapshot();
        if (session.Phase == SimSessionPhase.NotInSession && HasBeenAirborneThisSession)
        {
            // The pilot left the flight (menu/world map) — the next session must not inherit
            // this one's airborne history, or its write-safety gates would open at spawn.
            HasBeenAirborneThisSession = false;
            _logger.LogInformation("MSFS flight session ended — airborne-this-session latch reset");
        }

        // Classification gate (issue #59, flight test 2026-08-16): at app startup the engine
        // classified Unknown->Approach with ias=0/gs=0 while MSFS was still loading and the
        // dataref set was still registering — GSX automation followed to "Flight", both cabin
        // reports fired back-to-back and the FOB restore clobbered loaded fuel. Never evaluate
        // until a flight session exists (ProSim pushes plausible data while MSFS sits on the
        // main menu — see SimSessionStore) AND the source reports every phase-critical dataref
        // registered and fresh. While gated, hold the current phase quietly.
        var ready = session.DataIsMeaningful && snapshot.IsReady;
        if (ready != _classifying)
        {
            _classifying = ready;
            _logger.LogInformation(
                "Flight phase classification {State} (session {SessionPhase}, flight data ready: {DataReady})",
                ready ? "enabled" : "suspended", session.Phase, snapshot.IsReady);
        }

        if (!ready)
        {
            _pendingTarget = CurrentPhase;
            return;
        }

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

        var debounce = TransitionDebounceOverrides.TryGetValue((CurrentPhase, target), out var pairDebounce)
            ? pairDebounce
            : DebounceOverrides.GetValueOrDefault(target, DefaultDebounce);
        if (nowUtc - _pendingSince < debounce)
        {
            return;
        }

        var previous = CurrentPhase;
        CurrentPhase = target;
        if (target is FlightPhase.InitialClimb or FlightPhase.Climb or FlightPhase.Cruise
            or FlightPhase.Descent or FlightPhase.Approach)
        {
            HasBeenAirborneThisSession = true;
        }

        if (IsDepartureRegression(previous, target))
        {
            // A deliberate, debounced walk-back — record the evidence that justified it so a
            // post-flight log review can distinguish a correction from a data fault.
            _logger.LogInformation(
                "Departure regression {Previous} -> {Current} after {DebounceSeconds:F0}s of contradictory evidence: "
                + "enginesRunning={EnginesRunning} engineStarting={EngineStarting} pushback={PushbackActive} "
                + "parkBrake={ParkBrakeSet} gs={GroundSpeedKt:F1}kt ias={IndicatedAirspeedKt:F1}kt",
                previous, target, debounce.TotalSeconds, snapshot.AnyEngineRunning, snapshot.EngineStarting,
                snapshot.PushbackActive, snapshot.ParkBrakeSet, snapshot.GroundSpeedKt, snapshot.IndicatedAirspeedKt);
        }

        _logger.LogInformation("Flight phase {Previous} -> {Current}", previous, target);
        PhaseChanged?.Invoke(this, new FlightPhaseChangedEventArgs(previous, target));
    }

    private static bool IsDepartureRegression(FlightPhase from, FlightPhase to)
        => to == FlightPhase.Preflight
            && from is FlightPhase.PushbackAndStart or FlightPhase.TaxiOut or FlightPhase.TakeoffRoll;

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
