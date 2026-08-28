using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Flight;

/// <summary>Point-in-time view of the flight state: the committed phase, the most recent data
/// sample (null before the first), the airborne-this-session latch, and the flight-live gate
/// (<see cref="IFlightPhaseSource.IsLive"/>).</summary>
public sealed record FlightStateView(
    FlightPhase Phase,
    FlightDataSnapshot? Data,
    bool HasBeenAirborneThisSession,
    bool IsLive = false);

/// <summary>Read-only view of the committed flight state — the seam every consumer depends on
/// so tests can drive phases and snapshots directly. Widened with <see cref="Snapshot"/>
/// (campaign #80): half the consumers needed the last data sample or the airborne latch and
/// were forced onto the concrete engine, which made them unmockable; the planned JSONL replay
/// source becomes this seam's second adapter.</summary>
public interface IFlightPhaseSource
{
    /// <summary>The committed phase.</summary>
    FlightPhase CurrentPhase { get; }

    /// <summary>The full current view — phase, last data sample, airborne latch.</summary>
    FlightStateView Snapshot();

    /// <summary>The flight-live gate (CONTEXT.md): true only while the sim session is live
    /// (the session gate's "data is meaningful"), every phase-critical dataref is registered
    /// and fresh, AND the sample is physically plausible. This is the single arming signal for
    /// the voice First Officer (issue #114): every tick-driven FO module holds while it is
    /// false, because ProSim pushes plausible cold-and-dark data — including live ECAM fault
    /// indications — with MSFS still on the main menu or not running at all.</summary>
    bool IsLive { get; }

    /// <summary>Raised after a committed transition, on the engine's timer thread.</summary>
    event EventHandler<FlightPhaseChangedEventArgs>? PhaseChanged;

    /// <summary>Raised when <see cref="IsLive"/> changes, with the new value, on the engine's
    /// timer thread. Modules that hold state across a flight (fault latches, dialogues) reset
    /// on the false edge so the next session starts clean.</summary>
    event Action<bool>? LiveChanged;
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
    /// step descent do not flip-flop (four flips in 23 min on the 2026-08-16 flight).
    /// Unknown→airborne commits need sustained evidence too (issue #59, 2026-08-17
    /// recurrence): the very first classification of a session leaping straight to a flight
    /// phase is either a mid-flight app restart (5 s costs nothing) or connection warm-up
    /// garbage (5 s outlives it).</summary>
    private static readonly Dictionary<(FlightPhase From, FlightPhase To), TimeSpan> TransitionDebounceOverrides = new()
    {
        [(FlightPhase.PushbackAndStart, FlightPhase.Preflight)] = TimeSpan.FromSeconds(5),
        [(FlightPhase.TaxiOut, FlightPhase.Preflight)] = TimeSpan.FromSeconds(5),
        [(FlightPhase.TakeoffRoll, FlightPhase.Preflight)] = TimeSpan.FromSeconds(5),
        [(FlightPhase.Descent, FlightPhase.Cruise)] = TimeSpan.FromSeconds(15),
        // A VS blip at cruise (turbulence, altimetry) must not flip to Climb; a real step
        // climb sustains its rate far past 10 s (issue #105).
        [(FlightPhase.Cruise, FlightPhase.Climb)] = TimeSpan.FromSeconds(10),
        // Approach→Climb is a go-around: real ones sustain their climb for far longer than
        // 5 s, while the 2026-08-22 level-off blips (#99) lasted seconds. Second layer of
        // defence behind the evaluator's go-around VS gate.
        [(FlightPhase.Approach, FlightPhase.Climb)] = TimeSpan.FromSeconds(5),
        [(FlightPhase.Unknown, FlightPhase.Approach)] = TimeSpan.FromSeconds(5),
        [(FlightPhase.Unknown, FlightPhase.Climb)] = TimeSpan.FromSeconds(5),
        [(FlightPhase.Unknown, FlightPhase.Descent)] = TimeSpan.FromSeconds(5),
        [(FlightPhase.Unknown, FlightPhase.Cruise)] = TimeSpan.FromSeconds(5),
    };

    /// <summary>Below both of these, a not-on-ground sample is physically impossible — an
    /// A322 cannot be airborne at 30 kt IAS AND 30 kt ground speed. ProSim pushes exactly
    /// this shape while it warms up after an SDK connect (issue #59 recurrence 2026-08-17:
    /// every phase-critical ref had a first value, so <see cref="FlightDataSnapshot.IsReady"/>
    /// passed, but the values were boot defaults — onGround=false with ias=0/gs=0 — and the
    /// bogus Approach latched airborne history and restored 4.6 t of fuel over a 9.5 t load).</summary>
    private const double PlausibleAirborneMinSpeedKt = 30;

    /// <summary>Airborne-latch evidence floor: a committed flight phase only proves the
    /// session has flown when the sample itself is convincingly airborne. Below flying speed
    /// AND below this radio altitude, the commit may still be a data artifact — the phase can
    /// stand (it self-corrects) but the write-safety latch must not.</summary>
    private const double AirborneLatchMinIasKt = 80;
    private const double AirborneLatchMinRaFt = 200;

    private readonly IFlightDataSource _source;
    private readonly SimSessionStore _session;
    private readonly ILogger<FlightStateEngine> _logger;
    private readonly Timer _timer;
    private FlightPhase _pendingTarget = FlightPhase.Unknown;
    private DateTimeOffset _pendingSince;
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

    /// <inheritdoc />
    public bool IsLive { get; private set; }

    /// <inheritdoc />
    public event Action<bool>? LiveChanged;

    /// <inheritdoc />
    public FlightStateView Snapshot() => new(CurrentPhase, LastSnapshot, HasBeenAirborneThisSession, IsLive);

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
        // registered and fresh AND the sample is physically possible (2026-08-17 recurrence:
        // value-arrival checks pass while ProSim's own boot still serves airborne-at-zero-speed
        // defaults — IsReady proves the values arrived, not that they are sane). While gated,
        // hold the current phase quietly.
        var plausible = IsPhysicallyPlausible(snapshot);
        var ready = session.DataIsMeaningful && snapshot.IsReady && plausible;
        if (ready != IsLive)
        {
            // The same three-way verdict is the FO's arming gate (issue #114) — publish it
            // before classifying so a module reacting to the edge sees a consistent view.
            IsLive = ready;
            _logger.LogInformation(
                "Flight live {State} — phase classification {Classification} (session {SessionPhase}, flight data ready: {DataReady}, plausible: {Plausible})",
                ready ? "true" : "false", ready ? "enabled" : "suspended", session.Phase, snapshot.IsReady, plausible);
            try
            {
                LiveChanged?.Invoke(ready);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A LiveChanged subscriber threw");
            }
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
            // The latch opens write-safety gates (FOB restore, cabin landing report), so it
            // demands more than a committed phase name: the sample itself must be convincingly
            // airborne. A commit that later proves to be a data artifact self-corrects; a
            // latched artifact overwrote 9.5 t of fuel on 2026-08-17 (issue #59).
            if (!snapshot.OnGround
                && (snapshot.IndicatedAirspeedKt >= AirborneLatchMinIasKt
                    || snapshot.RadioAltitudeFt >= AirborneLatchMinRaFt))
            {
                HasBeenAirborneThisSession = true;
            }
            else if (!HasBeenAirborneThisSession)
            {
                _logger.LogInformation(
                    "Airborne phase {Phase} committed without convincing airborne evidence "
                    + "(onGround={OnGround} ias={IndicatedAirspeedKt:F1}kt ra={RadioAltitudeFt:F0}ft) — "
                    + "airborne-this-session latch withheld",
                    target, snapshot.OnGround, snapshot.IndicatedAirspeedKt, snapshot.RadioAltitudeFt);
            }
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

        // Commits are rare (a handful per flight) so the full evidence set is cheap — and it
        // turns a "why did it think we were on approach?" investigation into a one-look
        // diagnosis (issue #93: the 2026-08-17 bogus-Approach recurrence of #59 had to be
        // reconstructed from evaluator code paths because only ias/gs were recorded).
        _logger.LogInformation(
            "Flight phase {Previous} -> {Current}: onGround={OnGround} ias={IndicatedAirspeedKt:F1}kt "
            + "gs={GroundSpeedKt:F1}kt alt={AltitudeFt:F0}ft ra={RadioAltitudeFt:F0}ft vs={VerticalSpeedFpm:F0}fpm "
            + "powered={AircraftPowered} enginesRunning={EnginesRunning} engineStarting={EngineStarting} "
            + "pushback={PushbackActive} parkBrake={ParkBrakeSet} gearDown={GearDown} takeoffThrust={TakeoffThrustSet}",
            previous, target, snapshot.OnGround, snapshot.IndicatedAirspeedKt, snapshot.GroundSpeedKt,
            snapshot.AltitudeFt, snapshot.RadioAltitudeFt, snapshot.VerticalSpeedFpm, snapshot.AircraftPowered,
            snapshot.AnyEngineRunning, snapshot.EngineStarting, snapshot.PushbackActive, snapshot.ParkBrakeSet,
            snapshot.GearDown, snapshot.TakeoffThrustSet);
        PhaseChanged?.Invoke(this, new FlightPhaseChangedEventArgs(previous, target));
    }

    /// <summary>False for sample shapes that cannot describe a real aircraft: not on the
    /// ground yet below flying speed on BOTH speed sources. Exposed internal for tests.</summary>
    internal static bool IsPhysicallyPlausible(FlightDataSnapshot snapshot)
        => snapshot.OnGround
            || snapshot.IndicatedAirspeedKt >= PlausibleAirborneMinSpeedKt
            || snapshot.GroundSpeedKt >= PlausibleAirborneMinSpeedKt;

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
