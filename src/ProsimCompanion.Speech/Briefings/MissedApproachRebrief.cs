using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Speech.Arbiter;

namespace ProsimCompanion.Speech.Briefings;

/// <summary>
/// Speaks the published missed-approach legs after a go-around (Prosim2FO parity). Arms on
/// the committed Approach/LandingRollout → InitialClimb transition, then waits until BOTH the
/// configured delay has elapsed AND the gear is up — the crew is flying the aircraft first —
/// with a hard time ceiling as backstop if gear-up is never seen. One automatic re-brief per
/// go-around; re-arms whenever a new Approach phase begins. Safety content: delivered at High
/// (queues behind Critical callouts only), never persona-styled, numbers locked to the DFD
/// legs. Degrades to a prompt when no published legs can be resolved.
/// <see cref="ProcessSample"/> takes its clock as a parameter so tests step past the gate
/// without real-time waits.
/// </summary>
public sealed class MissedApproachRebrief : Core.Hosting.IStartupModule, IDisposable
{
    private const int PollMs = 1000;
    private const string Prefix = "Missed approach.";
    private const string DegradePrompt = "Going around — call for the missed approach when ready.";

    private readonly IOptionsMonitor<BriefingOptions> _options;
    private readonly ProcedureSource _procedures;
    private readonly DfdNavDataProvider _navData;
    private readonly IFlightPhaseSource _flight;
    private readonly IFlightDataSource _source;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<MissedApproachRebrief> _logger;
    private readonly object _gate = new();

    private Timer? _timer;
    private bool _pending;
    private bool _done;
    private long _armedAtMs;

    public MissedApproachRebrief(
        IOptionsMonitor<BriefingOptions> options,
        ProcedureSource procedures,
        DfdNavDataProvider navData,
        IFlightPhaseSource flight,
        IFlightDataSource source,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        ILogger<MissedApproachRebrief> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(procedures);
        ArgumentNullException.ThrowIfNull(navData);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _procedures = procedures;
        _navData = navData;
        _flight = flight;
        _source = source;
        _arbiter = arbiter;
        _eventLog = eventLog;
        _logger = logger;
    }

    public void Start()
    {
        _flight.PhaseChanged += OnPhaseChanged;
        _timer = new Timer(_ => Tick(), null, PollMs, PollMs);
    }

    public void Dispose()
    {
        _flight.PhaseChanged -= OnPhaseChanged;
        _timer?.Dispose();
    }

    /// <summary>"missed approach brief" voice phrase — speaks immediately, no gate.</summary>
    public void SpeakOnDemand() => Speak(onDemand: true);

    /// <summary>One gate evaluation — public for deterministic tests (clock injected).</summary>
    public void ProcessSample(FlightDataSnapshot snapshot, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        bool fire;
        lock (_gate)
        {
            // Flight-live gate (issue #114): hold with no MSFS session.
            if (!_pending || _done || !_flight.IsLive)
            {
                return;
            }

            var options = _options.CurrentValue;
            var elapsedS = (nowMs - _armedAtMs) / 1000.0;
            // Delay + gear-up: the crew flies the go-around first. The hard ceiling speaks
            // anyway if gear-up is never seen (a gear problem is exactly when the published
            // missed approach matters).
            fire = (elapsedS >= options.MissedApproachDelaySeconds && snapshot.IsValid && !snapshot.GearDown)
                || elapsedS >= options.MissedApproachHardCeilingSeconds;
            if (fire)
            {
                _pending = false;
                _done = true;
            }
        }

        if (fire)
        {
            Speak(onDemand: false);
        }
    }

    private void Tick()
    {
        try
        {
            ProcessSample(_source.Sample(), Environment.TickCount64);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Missed-approach gate tick failed");
        }
    }

    private void OnPhaseChanged(object? sender, FlightPhaseChangedEventArgs e)
    {
        lock (_gate)
        {
            if (e.Current is FlightPhase.InitialClimb
                && e.Previous is FlightPhase.Approach or FlightPhase.LandingRollout)
            {
                if (!_options.CurrentValue.MissedApproachRebriefEnabled || _done)
                {
                    return;
                }

                _pending = true;
                _armedAtMs = Environment.TickCount64;
                _eventLog.Record("goaround.detected", new { previous = e.Previous.ToString() });
                _logger.LogInformation("Go-around detected ({Previous} → InitialClimb) — re-brief armed", e.Previous);
            }
            else if (e.Current is FlightPhase.Approach)
            {
                // A fresh approach re-arms the one-shot for the next potential go-around.
                _pending = false;
                _done = false;
            }
        }
    }

    private void Speak(bool onDemand)
    {
        try
        {
            var ids = _procedures.Resolve(departure: false);
            var procedure = _navData.MissedApproach(ids.Airport, ids.Approach);
            var degraded = procedure.Legs.Count == 0;
            var text = degraded
                ? DegradePrompt
                : Prefix + " " + Capitalize(string.Join("; ", procedure.Legs.Select(l => l.Phrase))) + ".";

            _eventLog.Record("goaround.rebrief", new
            {
                airport = ids.Airport,
                approach = ids.Approach,
                degraded,
                legs = procedure.Legs.Count,
                onDemand,
            });
            _ = _arbiter.EnqueueAsync(new SpeechRequest(
                text, SpeechPriority.High, Ttl: TimeSpan.FromMinutes(2), Tag: "missedApproach"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Missed-approach re-brief failed");
        }
    }

    private static string Capitalize(string text)
        => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
