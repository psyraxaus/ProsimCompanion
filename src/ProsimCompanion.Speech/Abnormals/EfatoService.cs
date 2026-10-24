using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Speech.Arbiter;

namespace ProsimCompanion.Speech.Abnormals;

/// <summary>
/// The engine-failure-after-V1 sequencer (issue #187): when the <c>drill-engine-failure-after-v1</c>
/// drill fires (the monitor announced "engine failure"), this 100 ms shell paces the memory
/// items on the flight data through <see cref="EfatoCore"/> and at 400 ft AAL hands over to the
/// ENG 1/2 FAIL ECAM procedure through <see cref="FailureMonitor.TryRunProcedure"/>. Off by
/// <c>abnormals.efatoEnabled</c> (the monitor does not arm the drill either). Read-and-speak only.
/// </summary>
public sealed class EfatoService : Core.Hosting.IStartupModule, IDisposable
{
    public const string DrillId = "drill-engine-failure-after-v1";

    private readonly IOptionsMonitor<AbnormalsOptions> _options;
    private readonly IFlightDataSource _source;
    private readonly IFlightPhaseSource _flight;
    private readonly FailureMonitor _failures;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<EfatoService> _logger;
    private readonly EfatoCore _core = new();
    private readonly object _gate = new();
    private Timer? _timer;
    private int _ticking;

    public EfatoService(
        IOptionsMonitor<AbnormalsOptions> options,
        IFlightDataSource source,
        IFlightPhaseSource flight,
        FailureMonitor failures,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        ILogger<EfatoService> logger,
        Briefings.BriefingService? briefing = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(failures);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _source = source;
        _flight = flight;
        _failures = failures;
        _arbiter = arbiter;
        _eventLog = eventLog;
        _logger = logger;
        _briefing = briefing;
    }

    /// <summary>The briefed engine-out routing is spoken only when the briefing is this recent.</summary>
    public static readonly TimeSpan BriefingMaxAge = TimeSpan.FromHours(3);

    // Optional (package-1 wire): the last departure briefing's engine-out routing, spoken once
    // after gear up when it was a published EOSID. Without the briefing service (tests) nothing is added.
    private readonly Briefings.BriefingService? _briefing;

    /// <summary>The engine-out line after gear up, pure: the briefed routing when the brief is
    /// recent and the routing was published; null otherwise.</summary>
    public static string? EngineOutLine(Briefings.DepartureEmergencyBrief? brief, DateTimeOffset nowUtc)
        => brief is { Facts.EngineOutIsPublished: true } && nowUtc - brief.AtUtc <= BriefingMaxAge
            ? $"Engine-out routing: {brief.Facts.EngineOutProcedure}."
            : null;

    public void Start()
    {
        Attach();
        _timer = new Timer(_ => Tick(), null, 1000, 100);
    }

    internal void Attach()
    {
        _failures.FailureDetected += OnFailureDetected;
        _flight.SessionReset += OnSessionReset;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _failures.FailureDetected -= OnFailureDetected;
        _flight.SessionReset -= OnSessionReset;
    }

    public bool Running
    {
        get
        {
            lock (_gate)
            {
                return _core.Running;
            }
        }
    }

    private void OnSessionReset()
    {
        lock (_gate)
        {
            _core.Reset();
        }
    }

    private void OnFailureDetected(string id, string title)
    {
        if (!id.Equals(DrillId, StringComparison.OrdinalIgnoreCase) || !_options.CurrentValue.EfatoEnabled)
        {
            return;
        }

        var data = _source.Sample();
        Arm(data.Engine1N2Percent, data.Engine2N2Percent, DateTimeOffset.UtcNow);
    }

    /// <summary>Arms the sequence — public for tests.</summary>
    public void Arm(double n2Engine1, double n2Engine2, DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            if (_core.Running)
            {
                return;
            }

            _core.Arm(n2Engine1, n2Engine2, nowUtc);
        }

        _logger.LogWarning("ENGINE FAILURE AFTER V1 — engine {Engine} (N2 {N2a:F0} / {N2b:F0}); memory items follow the flight data", _core.FailedEngine, n2Engine1, n2Engine2);
        _eventLog.Record("efato.armed", new { engine = _core.FailedEngine, n2Engine1 = Math.Round(n2Engine1), n2Engine2 = Math.Round(n2Engine2) });
    }

    /// <summary>One sample through the core — public for tests.</summary>
    public void Process(EfatoSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        IReadOnlyList<EfatoItem> due;
        int engine;
        lock (_gate)
        {
            if (!_core.Running)
            {
                return;
            }

            due = _core.Process(sample);
            engine = _core.FailedEngine;
        }

        foreach (var item in due)
        {
            var text = item switch
            {
                EfatoItem.Rudder => "Rudder.",
                EfatoItem.Rotate => "Rotate.",
                EfatoItem.V2 => "V2.",
                EfatoItem.GearUp => "Positive climb — gear up.",
                _ => "ECAM actions.",
            };
            _eventLog.Record("efato.item", new { item = item.ToString(), text, iasKt = Math.Round(sample.IasKt), aglFt = Math.Round(sample.AltitudeAglFt) });
            _ = _arbiter.EnqueueAsync(new SpeechRequest(text, SpeechPriority.Critical, TimeSpan.FromSeconds(6), Tag: "efato." + item.ToString().ToLowerInvariant()));

            if (item == EfatoItem.GearUp && EngineOutLine(_briefing?.LastDepartureEmergency, sample.NowUtc) is { } eosid)
            {
                // The package-1 EOSID fact, once, right after gear up (the briefing said it; the drill repeats it).
                _eventLog.Record("efato.item", new { item = "EngineOut", text = eosid, airport = _briefing!.LastDepartureEmergency!.Airport, runway = _briefing.LastDepartureEmergency.Runway });
                _ = _arbiter.EnqueueAsync(new SpeechRequest(eosid, SpeechPriority.High, TimeSpan.FromSeconds(10), Tag: "efato.engineout"));
            }

            if (item == EfatoItem.EcamActions)
            {
                var id = engine == 2 ? "eng-2-fail" : "eng-1-fail";
                var started = _failures.TryRunProcedure(id, "efato");
                _eventLog.Record("efato.handoff", new { procedure = id, started });
                _logger.LogInformation("EFATO hand-over to {Procedure}: {Started}", id, started ? "started" : "not started (unknown, disabled or already running)");
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
            if (!Running)
            {
                return;
            }

            var data = _source.Sample();
            if (!data.IsValid || !_flight.IsLive)
            {
                return;
            }

            Process(new EfatoSample(
                data.IndicatedAirspeedKt, data.OnGround, data.VerticalSpeedFpm, data.RadioAltitudeFt, data.AltitudeAglFt,
                data.GearDown, data.VrKt, data.V2Kt, DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EFATO tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }
}
