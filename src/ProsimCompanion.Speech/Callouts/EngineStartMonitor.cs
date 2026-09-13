using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Speech.Arbiter;

namespace ProsimCompanion.Speech.Callouts;

/// <summary>One PM engine-start call decided by <see cref="EngineStartCalloutCore"/>.</summary>
public sealed record EngineStartCall(string Id, string Text);

/// <summary>
/// The pure decision core for the PM engine-start monitoring calls (issue #131), extracted
/// from the timer-driven module so the start/avail/stabilized edges are testable sample by
/// sample. Semantics carried from the flight deck: the PM watches N2 come up ("Engine two,
/// starting."), calls the ECAM AVAIL moment ("Engine two, avail.") and, once both engines sit
/// at idle, "Both engines stabilized." — after which the PF runs the after-start flow and calls
/// for the checklist; the FO never calls it unprompted (owner's Option A, 2026-09-13).
///
/// Evidence rules, because <c>aircraft.systems.engines.N.state</c> misreports transiently
/// mid-start (#59): "starting" is an UPWARD N2 crossing of <see cref="StartN2Percent"/> with the
/// running flag still false (a shutdown crosses the same line DOWNWARD and stays silent);
/// "avail" is the running flag going true while N2 is at or above <see cref="AvailN2Percent"/>
/// (idle N2 is ~58–60 % on both engine types, so a spurious running=true at 20 % is ignored
/// until N2 catches up). Every call is ground-only and once per start; an engine re-arms when
/// its running flag drops and N2 falls back below the start line. "Stabilized" fires on the
/// avail tick that leaves both engines at idle, once per episode, and the episode resets only
/// when both engines are off — so a cross-bleed second start still gets exactly one
/// "stabilized" and an in-flight relight gets nothing.
/// </summary>
public sealed class EngineStartCalloutCore
{
    /// <summary>N2 (%) whose upward crossing means the starter has engaged. Motoring N2 on a
    /// ground start sits well above this within a second or two; a resting engine reads 0.</summary>
    public const double StartN2Percent = 10;

    /// <summary>N2 (%) the running flag must be corroborated by before "avail" — comfortably
    /// below idle (~58–60 %) yet far above anything a not-yet-lit engine motors at.</summary>
    public const double AvailN2Percent = 50;

    private readonly EngineTrack _engine1 = new("one");
    private readonly EngineTrack _engine2 = new("two");
    private bool _stabilizedSpoken;
    private bool _primed;

    /// <summary>Evaluates one sample against the previous one and returns the calls to make
    /// (usually none). The first valid sample only primes the crossing memory — a reconnect
    /// with engines already turning must never fabricate a start.</summary>
    public IReadOnlyList<EngineStartCall> ProcessSample(FlightDataSnapshot s)
    {
        ArgumentNullException.ThrowIfNull(s);

        if (!s.IsValid)
        {
            // Crossing memory is dropped with the sample: the next valid one re-primes.
            _primed = false;
            return [];
        }

        var calls = new List<EngineStartCall>(3);
        var e1 = _engine1.Observe(s.Engine1Running, s.Engine1N2Percent, s.OnGround, _primed, calls);
        var e2 = _engine2.Observe(s.Engine2Running, s.Engine2N2Percent, s.OnGround, _primed, calls);
        _primed = true;

        if (!e1.AtIdle && !e2.AtIdle)
        {
            // Both off (or both spooling down): the episode is over; the next pair of
            // starts earns a fresh "stabilized".
            _stabilizedSpoken = false;
        }
        else if ((e1.AvailNow || e2.AvailNow) && e1.AtIdle && e2.AtIdle && !_stabilizedSpoken)
        {
            _stabilizedSpoken = true;
            calls.Add(new EngineStartCall("engineStart.stabilized", "Both engines stabilized."));
        }

        return calls;
    }

    private sealed class EngineTrack(string word)
    {
        private double _prevN2 = double.NaN;
        private bool _prevAtIdle;
        private bool _startingSpoken;
        private bool _availSpoken;

        public (bool AtIdle, bool AvailNow) Observe(
            bool running, double n2, bool onGround, bool primed, List<EngineStartCall> calls)
        {
            var atIdle = running && n2 >= AvailN2Percent;
            var availNow = false;

            if (primed && onGround)
            {
                // Starting: upward N2 crossing while not yet running, once per start.
                if (!_startingSpoken && !running
                    && _prevN2 < StartN2Percent && n2 >= StartN2Percent)
                {
                    _startingSpoken = true;
                    calls.Add(new EngineStartCall("engineStart.starting", $"Engine {word}, starting."));
                }

                // Avail: idle reached this tick, once per start.
                if (!_availSpoken && atIdle && !_prevAtIdle)
                {
                    _availSpoken = true;
                    availNow = true;
                    calls.Add(new EngineStartCall("engineStart.avail", $"Engine {word}, avail."));
                }
            }

            // Re-arm only once the engine is genuinely down: flag off AND N2 back under the
            // start line, so the flag flickering mid-start (#59) cannot re-arm a second
            // "starting" for the same start.
            if (!running && n2 < StartN2Percent)
            {
                _startingSpoken = false;
                _availSpoken = false;
            }

            _prevN2 = n2;
            _prevAtIdle = atIdle;
            return (atIdle, availNow);
        }
    }
}

/// <summary>
/// Timer-driven host for <see cref="EngineStartCalloutCore"/>: samples the flight data at
/// <see cref="PollMs"/>, gates on the speech option and the flight-live gate (issue #114), and
/// speaks each call through the arbiter with the callout event-log shape (<c>callout.fired</c>
/// with the call id) so the flight-verification probes read it like every other callout.
/// Advisory only — this module never writes to the aircraft.
/// </summary>
public sealed class EngineStartMonitor : Core.Hosting.IStartupModule, IDisposable
{
    /// <summary>250 ms keeps "starting" within a beat of the N2 rise without a Frequent-tier
    /// subscription — the 1 Hz flow-monitor cadence would land the call visibly late.</summary>
    private const int PollMs = 250;
    private const double CallTtlSec = 5.0;

    private readonly IOptionsMonitor<SpeechOptions> _speech;
    private readonly ISpeechArbiter _arbiter;
    private readonly IFlightDataSource _source;
    private readonly IFlightPhaseSource _flight;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<EngineStartMonitor> _logger;
    private readonly EngineStartCalloutCore _core = new();
    private readonly object _lock = new();

    private Timer? _timer;
    private int _ticking;

    public EngineStartMonitor(
        IOptionsMonitor<SpeechOptions> speech,
        ISpeechArbiter arbiter,
        IFlightDataSource source,
        IFlightPhaseSource flight,
        JsonlEventLog eventLog,
        ILogger<EngineStartMonitor> logger)
    {
        ArgumentNullException.ThrowIfNull(speech);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _speech = speech;
        _arbiter = arbiter;
        _source = source;
        _flight = flight;
        _eventLog = eventLog;
        _logger = logger;
    }

    public void Start() => _timer = new Timer(_ => Tick(), null, PollMs, PollMs);

    public void Dispose() => _timer?.Dispose();

    /// <summary>One evaluation step — public so tests drive deterministic samples; the timer
    /// calls it with a live one. The core still SEES samples while the option is off or the
    /// flight is not live, so its crossing memory stays honest; only the speech is withheld.</summary>
    public void ProcessSample(FlightDataSnapshot s)
    {
        ArgumentNullException.ThrowIfNull(s);

        lock (_lock)
        {
            var calls = _core.ProcessSample(s);
            if (calls.Count == 0)
            {
                return;
            }

            if (!_speech.CurrentValue.EngineStartCallouts)
            {
                foreach (var call in calls)
                {
                    _eventLog.Record("callout.suppressed", new { id = call.Id, reason = "disabled" });
                }

                return;
            }

            if (!_flight.IsLive)
            {
                foreach (var call in calls)
                {
                    _eventLog.Record("callout.suppressed", new { id = call.Id, reason = "not-live" });
                }

                return;
            }

            foreach (var call in calls)
            {
                _logger.LogInformation("Engine start call: {Id} \"{Text}\"", call.Id, call.Text);
                _eventLog.Record("callout.fired", new { id = call.Id, text = call.Text, priority = nameof(SpeechPriority.Normal) });
                _ = _arbiter.EnqueueAsync(new SpeechRequest(
                    call.Text, SpeechPriority.Normal, TimeSpan.FromSeconds(CallTtlSec), Tag: call.Id));
            }
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
            ProcessSample(_source.Sample());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Engine start monitor tick failed");
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }
}
