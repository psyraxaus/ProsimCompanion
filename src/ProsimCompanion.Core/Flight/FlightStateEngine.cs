using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Flight;

/// <summary>Point-in-time view of the flight state: the committed phase, the most recent data
/// sample (null before the first), the airborne-this-session latch, the flight-live gate
/// (<see cref="IFlightPhaseSource.IsLive"/>), the reason the current phase was committed and
/// whether a manual override has frozen automatic evaluation.</summary>
public sealed record FlightStateView(
    FlightPhase Phase,
    FlightDataSnapshot? Data,
    bool HasBeenAirborneThisSession,
    bool IsLive = false,
    string? LastTransitionReason = null,
    bool Frozen = false);

/// <summary>Read-only view of the committed flight state — the seam every consumer depends on
/// so tests can drive phases and snapshots directly. Widened with <see cref="Snapshot"/>
/// (campaign #80): half the consumers needed the last data sample or the airborne latch and
/// were forced onto the concrete engine, which made them unmockable; the JSONL replay
/// harness (<see cref="FlightReplay"/>) is this seam's second driver.</summary>
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
/// The pilot's escape hatch when the phase is wrong (Prosim2FO <c>ForcePhase</c> parity):
/// force a phase, optionally freezing automatic evaluation until resumed. Narrow so the web
/// Status page and a voice command can share it without seeing the engine.
/// </summary>
public interface IFlightPhaseControl
{
    /// <summary>True while a manual override has suspended automatic transitions.</summary>
    bool IsFrozen { get; }

    /// <summary>Commits <paramref name="phase"/> now with reason "manual override (source)".
    /// With <paramref name="freeze"/> the rule table stays suspended until
    /// <see cref="ResumeAutomatic"/>; without it the next contradicting evidence moves on as
    /// usual. Never opens the airborne write-safety latch.</summary>
    void ForcePhase(FlightPhase phase, bool freeze, string source);

    /// <summary>Lifts a freeze; evaluation continues from the current (forced) phase.</summary>
    void ResumeAutomatic(string source);
}

/// <summary>
/// Central flight-phase state machine: samples the <see cref="IFlightDataSource"/> every
/// 250 ms, evaluates the <see cref="FlightPhaseRules"/> table via
/// <see cref="FlightPhaseEvaluator"/>, and commits a transition only after the matched rule's
/// evidence has persisted for that rule's debounce. Classification is gated on the MSFS
/// session being live AND the data source reporting its dataref set fully registered
/// (<see cref="FlightDataSnapshot.IsReady"/>) AND the sample being physically plausible —
/// without all three, the engine holds its phase (Unknown at startup) rather than
/// classifying garbage. If ProSim or MSFS never appear the engine simply stays Unknown
/// (degrade, not fail). <see cref="PhaseChanged"/> fires on the timer thread — consumers
/// marshal themselves.
/// </summary>
public sealed class FlightStateEngine : IFlightPhaseSource, IFlightPhaseControl, IDisposable
{
    /// <summary>Sampling cadence.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Rule id recorded on a manual override commit.</summary>
    public const string ManualOverrideRuleId = "manual-override";

    /// <summary>Rule id recorded when the phase resets because the sim session ended.</summary>
    public const string SessionEndedRuleId = "session-ended";

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
    private readonly IOptionsMonitor<FlightStateOptions> _options;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private FlightPhase _pendingTarget = FlightPhase.Unknown;
    private DateTimeOffset _pendingSince;
    private int _ticking;

    // Ground-contact agreement filter (Prosim2GSX GroundTicks parity): the committed
    // on-ground state and how many consecutive raw samples have disagreed with it.
    private bool? _committedOnGround;
    private int _groundDisagreeSamples;

    private bool _frozen;
    private DateTimeOffset? _lastHeartbeat;

    /// <summary>Defaults-only construction — tests and the replay harness.</summary>
    public FlightStateEngine(IFlightDataSource source, SimSessionStore session, ILogger<FlightStateEngine> logger)
        : this(source, session, logger, new FixedOptionsMonitor<FlightStateOptions>(FlightStateOptions.Default))
    {
    }

    public FlightStateEngine(
        IFlightDataSource source,
        SimSessionStore session,
        ILogger<FlightStateEngine> logger,
        IOptionsMonitor<FlightStateOptions> options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);

        _source = source;
        _session = session;
        _logger = logger;
        _options = options;
        _timer = new Timer(_ => Tick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The committed phase.</summary>
    public FlightPhase CurrentPhase { get; private set; } = FlightPhase.Unknown;

    /// <summary>The most recent sample — diagnostics surface for the Status page.</summary>
    public FlightDataSnapshot? LastSnapshot { get; private set; }

    /// <summary>Why the current phase was committed (rule reason text); null before the
    /// first commit.</summary>
    public string? LastTransitionReason { get; private set; }

    /// <summary>Id of the rule that committed the current phase; null before the first commit.</summary>
    public string? LastTransitionRuleId { get; private set; }

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
    public bool IsFrozen => _frozen;

    /// <inheritdoc />
    public FlightStateView Snapshot()
        => new(CurrentPhase, LastSnapshot, HasBeenAirborneThisSession, IsLive, LastTransitionReason, _frozen);

    /// <summary>Starts sampling.</summary>
    public void Start() => _timer.Change(TimeSpan.Zero, TickInterval);

    /// <inheritdoc />
    public void ForcePhase(FlightPhase phase, bool freeze, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        lock (_gate)
        {
            _frozen = freeze;
            _pendingTarget = phase;
            _logger.LogWarning(
                "Manual phase override by {Source}: {Previous} -> {Phase}{Freeze}",
                source, CurrentPhase, phase, freeze ? " (frozen — automatic transitions suspended)" : "");
            if (phase != CurrentPhase)
            {
                // Never opens the write-safety latch: a forced phase is an assertion, not
                // evidence the aircraft flew.
                Commit(phase, ManualOverrideRuleId, $"manual override ({source})", LastSnapshot);
            }
        }
    }

    /// <inheritdoc />
    public void ResumeAutomatic(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        lock (_gate)
        {
            if (!_frozen)
            {
                return;
            }

            _frozen = false;
            _pendingTarget = CurrentPhase;
            _logger.LogInformation(
                "Manual phase override released by {Source} — automatic evaluation resumes from {Phase}",
                source, CurrentPhase);
        }
    }

    /// <summary>One evaluation step — exposed for tests and replay; the timer calls this every tick.</summary>
    public void ProcessTick(FlightDataSnapshot snapshot, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_gate)
        {
            ProcessTickLocked(snapshot, nowUtc);
        }
    }

    private void ProcessTickLocked(FlightDataSnapshot snapshot, DateTimeOffset nowUtc)
    {
        LastSnapshot = snapshot;
        var options = _options.CurrentValue;

        var session = _session.Snapshot();
        if (session.Phase == SimSessionPhase.NotInSession)
        {
            OnSessionLeft(snapshot);
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
            _committedOnGround = null;
            _groundDisagreeSamples = 0;
            return;
        }

        var classified = ApplyGroundContactFilter(snapshot, options);
        Heartbeat(classified, nowUtc, options);

        if (_frozen)
        {
            return;
        }

        var decision = FlightPhaseEvaluator.Decide(classified, CurrentPhase, options);
        if (decision is null)
        {
            _pendingTarget = CurrentPhase;
            return;
        }

        if (decision.Target != _pendingTarget)
        {
            _pendingTarget = decision.Target;
            _pendingSince = nowUtc;
        }

        if (nowUtc - _pendingSince < decision.Debounce)
        {
            return;
        }

        var previous = CurrentPhase;
        if (IsAirbornePhase(decision.Target))
        {
            // The latch opens write-safety gates (FOB restore, cabin landing report), so it
            // demands more than a committed phase name: the sample itself must be convincingly
            // airborne. A commit that later proves to be a data artifact self-corrects; a
            // latched artifact overwrote 9.5 t of fuel on 2026-08-17 (issue #59).
            if (!classified.OnGround
                && (classified.IndicatedAirspeedKt >= AirborneLatchMinIasKt
                    || classified.RadioAltitudeFt >= AirborneLatchMinRaFt))
            {
                HasBeenAirborneThisSession = true;
            }
            else if (!HasBeenAirborneThisSession)
            {
                _logger.LogInformation(
                    "Airborne phase {Phase} committed without convincing airborne evidence "
                    + "(onGround={OnGround} ias={IndicatedAirspeedKt:F1}kt ra={RadioAltitudeFt:F0}ft) — "
                    + "airborne-this-session latch withheld",
                    decision.Target, classified.OnGround, classified.IndicatedAirspeedKt, classified.RadioAltitudeFt);
            }
        }

        if (IsDepartureRegression(previous, decision.Target))
        {
            // A deliberate, debounced walk-back — record the evidence that justified it so a
            // post-flight log review can distinguish a correction from a data fault.
            _logger.LogInformation(
                "Departure regression {Previous} -> {Current} after {DebounceSeconds:F0}s of contradictory evidence: "
                + "enginesRunning={EnginesRunning} engineStarting={EngineStarting} pushback={PushbackActive} "
                + "parkBrake={ParkBrakeSet} gs={GroundSpeedKt:F1}kt ias={IndicatedAirspeedKt:F1}kt",
                previous, decision.Target, decision.Debounce.TotalSeconds, classified.AnyEngineRunning,
                classified.EngineStarting, classified.PushbackActive, classified.ParkBrakeSet,
                classified.GroundSpeedKt, classified.IndicatedAirspeedKt);
        }

        Commit(decision.Target, decision.RuleId, decision.Reason, classified);
    }

    /// <summary>Commits a phase and publishes it. Commits are rare (a handful per flight) so
    /// the full evidence set is cheap — and it turns a "why did it think we were on approach?"
    /// investigation into a one-look diagnosis (issue #93).</summary>
    private void Commit(FlightPhase target, string ruleId, string reason, FlightDataSnapshot? snapshot)
    {
        var previous = CurrentPhase;
        CurrentPhase = target;
        LastTransitionReason = reason;
        LastTransitionRuleId = ruleId;
        _pendingTarget = target;

        var s = snapshot ?? new FlightDataSnapshot();
        _logger.LogInformation(
            "Flight phase {Previous} -> {Current} [{Rule}: {Reason}]: onGround={OnGround} ias={IndicatedAirspeedKt:F1}kt "
            + "gs={GroundSpeedKt:F1}kt alt={AltitudeFt:F0}ft ra={RadioAltitudeFt:F0}ft vs={VerticalSpeedFpm:F0}fpm "
            + "powered={AircraftPowered} enginesRunning={EnginesRunning} engineStarting={EngineStarting} "
            + "pushback={PushbackActive} parkBrake={ParkBrakeSet} gearDown={GearDown} takeoffThrust={TakeoffThrustSet} "
            + "beacon={BeaconOn} apu={ApuRunning}",
            previous, target, ruleId, reason, s.OnGround, s.IndicatedAirspeedKt, s.GroundSpeedKt,
            s.AltitudeFt, s.RadioAltitudeFt, s.VerticalSpeedFpm, s.AircraftPowered,
            s.AnyEngineRunning, s.EngineStarting, s.PushbackActive, s.ParkBrakeSet,
            s.GearDown, s.TakeoffThrustSet, s.BeaconOn, s.ApuRunning);

        try
        {
            PhaseChanged?.Invoke(this, new FlightPhaseChangedEventArgs(previous, target, reason, ruleId));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A PhaseChanged subscriber threw on {Previous} -> {Current}", previous, target);
        }
    }

    /// <summary>The pilot left the flight (menu/world map): the next session must not inherit
    /// this one's airborne history (its write-safety gates would open at spawn), nor its
    /// phase — a Shutdown carried into a new flight that spawns powered at the gate would
    /// read the first engine start as a taxi-in (review 2026-08-29). A frozen override is
    /// released too: it belonged to the flight that just ended.</summary>
    private void OnSessionLeft(FlightDataSnapshot snapshot)
    {
        if (HasBeenAirborneThisSession)
        {
            HasBeenAirborneThisSession = false;
            _logger.LogInformation("MSFS flight session ended — airborne-this-session latch reset");
        }

        if (_frozen)
        {
            _frozen = false;
            _logger.LogInformation("MSFS flight session ended — manual phase override released");
        }

        if (CurrentPhase != FlightPhase.Unknown)
        {
            Commit(FlightPhase.Unknown, SessionEndedRuleId, "MSFS flight session ended", snapshot);
        }
    }

    /// <summary>The raw on-ground flag must agree for <see cref="FlightStateOptions.GroundContactAgreeSamples"/>
    /// consecutive samples before the committed ground/air state flips; until then the
    /// sample is classified with the committed value. A one-sample flicker of the contact
    /// dataref (touchdown bounce, SimConnect hiccup) therefore never commits a zero-debounce
    /// runway transition or fires its callouts (Prosim2GSX <c>GroundTicks</c> parity).</summary>
    private FlightDataSnapshot ApplyGroundContactFilter(FlightDataSnapshot snapshot, FlightStateOptions options)
    {
        var required = Math.Max(1, options.GroundContactAgreeSamples);
        if (_committedOnGround is not { } committed)
        {
            _committedOnGround = snapshot.OnGround;
            _groundDisagreeSamples = 0;
            return snapshot;
        }

        if (snapshot.OnGround == committed)
        {
            _groundDisagreeSamples = 0;
            return snapshot;
        }

        _groundDisagreeSamples++;
        if (_groundDisagreeSamples >= required)
        {
            _committedOnGround = snapshot.OnGround;
            _groundDisagreeSamples = 0;
            _logger.LogDebug(
                "Ground contact committed {State} after {Samples} agreeing sample(s) (ias={IndicatedAirspeedKt:F0}kt ra={RadioAltitudeFt:F0}ft)",
                snapshot.OnGround ? "on ground" : "airborne", required, snapshot.IndicatedAirspeedKt, snapshot.RadioAltitudeFt);
            return snapshot;
        }

        return snapshot with { OnGround = committed };
    }

    /// <summary>A periodic "still thinking" line (Prosim2GSX <c>FlightPhaseLogIntervalSec</c>
    /// parity): with commits a handful per flight, a silent hour in the log gave no way to
    /// tell a stuck engine from a quiet cruise.</summary>
    private void Heartbeat(FlightDataSnapshot s, DateTimeOffset nowUtc, FlightStateOptions options)
    {
        if (options.HeartbeatSeconds <= 0)
        {
            return;
        }

        if (_lastHeartbeat is { } last && nowUtc - last < TimeSpan.FromSeconds(options.HeartbeatSeconds))
        {
            return;
        }

        _lastHeartbeat = nowUtc;
        var pending = _pendingTarget != CurrentPhase
            ? $"{_pendingTarget} pending {(nowUtc - _pendingSince).TotalSeconds:F0}s"
            : "none";
        _logger.LogInformation(
            "Flight phase heartbeat: {Phase}{Frozen} (pending: {Pending}) onGround={OnGround} ias={IndicatedAirspeedKt:F0}kt "
            + "gs={GroundSpeedKt:F0}kt alt={AltitudeFt:F0}ft ra={RadioAltitudeFt:F0}ft vs={VerticalSpeedFpm:F0}fpm "
            + "engines={EnginesRunning} beacon={BeaconOn} parkBrake={ParkBrakeSet}",
            CurrentPhase, _frozen ? " [frozen]" : "", pending, s.OnGround, s.IndicatedAirspeedKt,
            s.GroundSpeedKt, s.AltitudeFt, s.RadioAltitudeFt, s.VerticalSpeedFpm,
            s.AnyEngineRunning, s.BeaconOn, s.ParkBrakeSet);
    }

    /// <summary>False for sample shapes that cannot describe a real aircraft: not on the
    /// ground yet below flying speed on BOTH speed sources. Exposed internal for tests.</summary>
    internal static bool IsPhysicallyPlausible(FlightDataSnapshot snapshot)
        => snapshot.OnGround
            || snapshot.IndicatedAirspeedKt >= PlausibleAirborneMinSpeedKt
            || snapshot.GroundSpeedKt >= PlausibleAirborneMinSpeedKt;

    private static bool IsAirbornePhase(FlightPhase phase)
        => phase is FlightPhase.InitialClimb or FlightPhase.Climb or FlightPhase.Cruise
            or FlightPhase.Descent or FlightPhase.Approach;

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
    public FlightPhaseChangedEventArgs(FlightPhase previous, FlightPhase current, string reason = "", string ruleId = "")
    {
        Previous = previous;
        Current = current;
        Reason = reason ?? "";
        RuleId = ruleId ?? "";
    }

    public FlightPhase Previous { get; }
    public FlightPhase Current { get; }

    /// <summary>Human-readable justification from the rule that fired (empty on test fakes).</summary>
    public string Reason { get; }

    /// <summary>Id of the <see cref="PhaseRule"/> that fired, or a well-known engine id
    /// (<see cref="FlightStateEngine.ManualOverrideRuleId"/>, <see cref="FlightStateEngine.SessionEndedRuleId"/>).</summary>
    public string RuleId { get; }
}
