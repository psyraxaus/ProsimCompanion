using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Speech.Arbiter;

namespace ProsimCompanion.Speech.Monitoring;

/// <summary>
/// Stabilized-approach gates (Prosim2FO semantics): descending through each configured radio
/// altitude, checks IAS against a VLS band (there is no VAPP dataref), sink rate, gear, flap
/// handle, and optionally thrust. "unstable, go around" is Critical; "stabilized" (High) only
/// on gates that opt in. Advisory only — it never commands. A missing VLS makes the gate
/// indeterminate (silent) unless another criterion outright fails; every evaluation is
/// event-logged either way. <see cref="ProcessSample"/> is public for deterministic
/// two-sample crossing tests.
/// </summary>
public sealed class StabilizedApproachMonitor : Core.Hosting.IStartupModule, IDisposable
{
    private const int PollMs = 250;

    private enum CriterionResult
    {
        Pass,
        Fail,
        Unknown,
    }

    private readonly IOptionsMonitor<SopOptions> _options;
    private readonly ISpeechArbiter _arbiter;
    private readonly IFlightDataSource _source;
    private readonly IFlightPhaseSource _flight;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<StabilizedApproachMonitor> _logger;
    private readonly object _lock = new();
    private readonly HashSet<string> _firedGates = new(StringComparer.OrdinalIgnoreCase);

    private Timer? _timer;
    private int _ticking;
    private bool _unstableAnnounced;
    private bool _stableAnnounced;
    private double _prevRadio = double.NaN;

    public StabilizedApproachMonitor(
        IOptionsMonitor<SopOptions> options,
        ISpeechArbiter arbiter,
        IFlightDataSource source,
        IFlightPhaseSource flight,
        JsonlEventLog eventLog,
        ILogger<StabilizedApproachMonitor> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _arbiter = arbiter;
        _source = source;
        _flight = flight;
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

    /// <summary>One evaluation step — public for tests; the timer feeds it live samples.</summary>
    public void ProcessSample(FlightDataSnapshot s)
    {
        ArgumentNullException.ThrowIfNull(s);

        lock (_lock)
        {
            if (!s.IsValid)
            {
                return;
            }

            var sop = _options.CurrentValue;
            if (!sop.Stabilized.Enabled)
            {
                return;
            }

            var phase = _flight.CurrentPhase;
            if (phase is not (FlightPhase.Approach or FlightPhase.Descent))
            {
                return;
            }

            var radio = s.RadioAltitudeFt;
            if (!double.IsNaN(_prevRadio))
            {
                // Gates arm top-down: evaluate highest AGL first so dropping past both in one
                // step announces in the right order.
                foreach (var gate in _options.CurrentValue.ApproachGates
                    .Where(g => g.AglFt > 0)
                    .OrderByDescending(g => g.AglFt))
                {
                    if (!gate.Enabled || _firedGates.Contains(gate.Name))
                    {
                        continue;
                    }

                    if (_prevRadio > gate.AglFt && radio <= gate.AglFt)
                    {
                        _firedGates.Add(gate.Name);
                        EvaluateAndAnnounce(gate, sop, s);
                    }
                }
            }

            _prevRadio = radio;
        }
    }

    private void OnPhaseChanged(object? sender, FlightPhaseChangedEventArgs e)
    {
        lock (_lock)
        {
            if (e.Current is FlightPhase.Approach
                || (e.Current is FlightPhase.InitialClimb
                    && e.Previous is FlightPhase.Approach or FlightPhase.LandingRollout)
                || e.Current is FlightPhase.ColdAndDark or FlightPhase.Preflight or FlightPhase.TakeoffRoll)
            {
                _firedGates.Clear();
                _unstableAnnounced = _stableAnnounced = false;
                _prevRadio = double.NaN;
            }
        }
    }

    private void EvaluateAndAnnounce(ApproachGate gate, SopOptions sop, FlightDataSnapshot s)
    {
        var criteria = new List<(string Name, string Value, string Limit, CriterionResult Result)>();

        // Speed vs VLS band.
        var ias = s.IndicatedAirspeedKt;
        var vls = s.VlsKt;
        if (vls <= 0)
        {
            criteria.Add(("speed", $"{ias:F0} kt", "no VLS", CriterionResult.Unknown));
        }
        else
        {
            var lo = vls - gate.SpeedBandBelowVls;
            var hi = vls + gate.SpeedBandAboveVls;
            criteria.Add(("speed", $"{ias:F0} kt", $"{lo:F0}-{hi:F0} kt",
                ias >= lo && ias <= hi ? CriterionResult.Pass : CriterionResult.Fail));
        }

        // Sink rate (positive = descending; a climb passes).
        var sink = -s.VerticalSpeedFpm;
        criteria.Add(("sink", $"{sink:F0} fpm", $"<= {gate.MaxSinkRateFpm:F0} fpm",
            sink <= gate.MaxSinkRateFpm ? CriterionResult.Pass : CriterionResult.Fail));

        // Landing configuration. Gear is omitted entirely when not required (not a free pass).
        if (gate.RequireGearDown)
        {
            criteria.Add(("gear", s.GearDown ? "down" : "up", "down",
                s.GearDown ? CriterionResult.Pass : CriterionResult.Fail));
        }

        criteria.Add(("flaps", $"handle {s.FlapHandle}", $">= {gate.RequireFlapHandleAtLeast}",
            s.FlapHandle >= gate.RequireFlapHandleAtLeast ? CriterionResult.Pass : CriterionResult.Fail));

        if (sop.Stabilized.RequireThrustStabilized)
        {
            criteria.Add(("thrust", $"N1 {s.AverageN1Percent:F0}%", $">= {sop.Stabilized.MinStabilizedN1:F0}%",
                s.AverageN1Percent >= sop.Stabilized.MinStabilizedN1 ? CriterionResult.Pass : CriterionResult.Fail));
        }

        // Fail wins over unknown; unknown alone stays silent (indeterminate).
        var anyFail = criteria.Any(c => c.Result == CriterionResult.Fail);
        var anyUnknown = criteria.Any(c => c.Result == CriterionResult.Unknown);
        var overall = anyFail ? "unstable" : anyUnknown ? "indeterminate" : "stable";

        _eventLog.Record("approach.gate", new
        {
            gate = gate.Name,
            aglFt = gate.AglFt,
            result = overall,
            criteria = criteria.Select(c => new
            {
                name = c.Name,
                value = c.Value,
                limit = c.Limit,
                result = c.Result.ToString(),
            }).ToArray(),
        });
        _logger.LogInformation("Approach gate {Gate} ({AglFt} ft): {Result}", gate.Name, gate.AglFt, overall);

        if (overall == "unstable" && !_unstableAnnounced)
        {
            _unstableAnnounced = true;
            Speak(sop.Stabilized.UnstableText, SpeechPriority.Critical, 5, "stabilized:unstable");
        }
        else if (overall == "stable" && gate.AnnounceStabilized && !_stableAnnounced)
        {
            _stableAnnounced = true;
            Speak(sop.Stabilized.StableText, SpeechPriority.High, 4, "stabilized:stable");
        }
    }

    private void Speak(string text, SpeechPriority priority, double ttlSec, string tag)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _ = _arbiter.EnqueueAsync(new SpeechRequest(text, priority, TimeSpan.FromSeconds(ttlSec), Tag: tag));
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
            _logger.LogError(ex, "Stabilized-approach tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }
}
