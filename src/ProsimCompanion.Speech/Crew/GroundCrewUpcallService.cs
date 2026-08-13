using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Arbiter;

namespace ProsimCompanion.Speech.Crew;

/// <summary>
/// Ground-crew upcalls (ADR-0006 / issue #51): crew-initiated interphone calls to the flight
/// deck — ground power connected, chocks in place, refueling complete (with the fuel figure),
/// catering complete. Delivery mirrors the purser's CAB flow on INT: optional MECH call press
/// (the ACP lamp is the flight-deck cue), wait for an INT receive latch or the grace, then
/// speak in the GroundCrew role. Each call fires once per ground-ops cycle; edges are
/// baselined on first observation so a mid-turnaround app restart (startup resync) never
/// replays calls for state that already held. Detect-and-report plus the allow-listed MECH
/// call press — nothing else is written.
/// </summary>
public sealed class GroundCrewUpcallService : IDisposable
{
    private readonly IProsimDataRefs _dataRefs;
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly GroundOpsSignals _signals;
    private readonly FlightStateEngine _flight;
    private readonly ISpeechArbiter _arbiter;
    private readonly IOptionsMonitor<GroundCrewOptions> _options;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<GroundCrewUpcallService> _logger;
    private readonly Dictionary<string, IDataRefSubscription> _reads = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();

    private Timer? _timer;
    private volatile bool _busy;
    private bool? _lastGroundPower;
    private bool? _lastChocks;
    private GsxServiceStage? _lastRefuelStage;
    private GsxServiceStage? _lastCateringStage;
    private bool _groundPowerCalled;
    private bool _chocksCalled;
    private bool _refuelCalled;
    private bool _cateringCalled;

    private static readonly string[] IntLatches =
        [ProsimDataRefNames.Acp1IntLatch, ProsimDataRefNames.Acp2IntLatch, ProsimDataRefNames.Acp3IntLatch];

    public GroundCrewUpcallService(
        IProsimDataRefs dataRefs,
        GsxDiagnosticsStore diagnostics,
        GroundOpsSignals signals,
        FlightStateEngine flight,
        ISpeechArbiter arbiter,
        IOptionsMonitor<GroundCrewOptions> options,
        JsonlEventLog eventLog,
        ILogger<GroundCrewUpcallService> logger)
    {
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _dataRefs = dataRefs;
        _diagnostics = diagnostics;
        _signals = signals;
        _flight = flight;
        _arbiter = arbiter;
        _options = options;
        _eventLog = eventLog;
        _logger = logger;
    }

    public void Start()
    {
        foreach (var name in new[]
                 {
                     ProsimDataRefNames.GroundPower,
                     ProsimDataRefNames.Chocks,
                     ProsimDataRefNames.FuelTotal,
                 }.Concat(IntLatches))
        {
            _reads[name] = _dataRefs.Subscribe(name, DataRefTier.Normal);
        }

        _signals.FlightCycleReset += OnFlightCycleReset;
        _timer = new Timer(_ => Tick(), null, 1000, 1000);
    }

    public void Dispose()
    {
        _signals.FlightCycleReset -= OnFlightCycleReset;
        _timer?.Dispose();
        _shutdown.Cancel();
        _shutdown.Dispose();
        foreach (var read in _reads.Values)
        {
            read.Dispose();
        }
    }

    private void OnFlightCycleReset()
    {
        _groundPowerCalled = false;
        _chocksCalled = false;
        _refuelCalled = false;
        _cateringCalled = false;
        // Edge baselines survive deliberately: the equipment states themselves carry over
        // into the new cycle and a reset must not turn "still true" into a rising edge.
    }

    private void Tick()
    {
        try
        {
            var options = _options.CurrentValue;
            if (!options.Enabled || _busy || _reads.Count == 0)
            {
                return;
            }

            // Disconnected ProSim must never trigger calls off held-over values.
            if (_reads.Values.Any(r => r.IsStale))
            {
                return;
            }

            var phase = _flight.CurrentPhase;
            var departureWindow = phase is FlightPhase.ColdAndDark or FlightPhase.Preflight;
            var groundWindow = departureWindow || phase is FlightPhase.TaxiIn or FlightPhase.Shutdown;

            var groundPower = ReadNullableBool(ProsimDataRefNames.GroundPower);
            var chocks = ReadNullableBool(ProsimDataRefNames.Chocks);
            var board = _diagnostics.Snapshot().ServiceBoard;
            var refuelStage = StageOf(board, GsxServiceIds.Refueling);
            var cateringStage = StageOf(board, GsxServiceIds.Catering);

            // Baseline on first observation — never announce state recovered at startup.
            var groundPowerRose = _lastGroundPower is false && groundPower is true;
            var chocksRose = _lastChocks is false && chocks is true;
            var refuelCompleted = _lastRefuelStage is not null and not GsxServiceStage.Completed
                && refuelStage == GsxServiceStage.Completed;
            var cateringCompleted = _lastCateringStage is not null and not GsxServiceStage.Completed
                && cateringStage == GsxServiceStage.Completed;

            _lastGroundPower = groundPower ?? _lastGroundPower;
            _lastChocks = chocks ?? _lastChocks;
            _lastRefuelStage = refuelStage ?? _lastRefuelStage;
            _lastCateringStage = cateringStage ?? _lastCateringStage;

            if (groundPowerRose && groundWindow && options.CallOnGroundPower && !_groundPowerCalled)
            {
                _groundPowerCalled = true;
                Run("ground.upcall.gpu", options.GroundPowerText);
            }
            else if (chocksRose && groundWindow && options.CallOnChocks && !_chocksCalled)
            {
                _chocksCalled = true;
                Run("ground.upcall.chocks", options.ChocksText);
            }
            else if (refuelCompleted && departureWindow && options.CallOnRefuelComplete && !_refuelCalled)
            {
                _refuelCalled = true;
                Run("ground.upcall.refuel", FillFuel(options.RefuelCompleteText));
            }
            else if (cateringCompleted && departureWindow && options.CallOnCateringComplete && !_cateringCalled)
            {
                _cateringCalled = true;
                Run("ground.upcall.catering", options.CateringCompleteText);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ground upcall tick failed");
        }
    }

    private void Run(string tag, string text)
    {
        _busy = true;
        _ = RunUpcallAsync(tag, text);
    }

    private async Task RunUpcallAsync(string tag, string text)
    {
        try
        {
            _eventLog.Record("ground.calling", new { call = tag });

            // The MECH call press flashes the ACP lamp — the flight deck's "ground wants to
            // talk" cue (ProSim's native pipeline handles lamp + audio).
            if (_options.CurrentValue.MechCall)
            {
                try
                {
                    await _dataRefs.PressMomentaryAsync(ProsimDataRefNames.OhCallsMech, _shutdown.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "MECH call press failed — speaking the upcall anyway");
                }
            }

            await WaitForIntChannelAsync().ConfigureAwait(false);

            await _arbiter.EnqueueAsync(new SpeechRequest(
                text, SpeechPriority.Normal, Ttl: TimeSpan.FromMinutes(2), Tag: tag,
                Role: SpeechRole.GroundCrew)).ConfigureAwait(false);
            _eventLog.Record("ground.upcall", new { call = tag, text });
        }
        catch (OperationCanceledException)
        {
            // shutdown — fine
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ground upcall {Tag} failed", tag);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Waits for any ACP INT receive latch or the grace — the purser's CAB pattern
    /// on the ground channel. A call is never lost to an unmonitored panel.</summary>
    private async Task WaitForIntChannelAsync()
    {
        var options = _options.CurrentValue;
        if (!options.RequireIntChannel)
        {
            return;
        }

        var deadline = Environment.TickCount64 + Math.Max(0, options.IntChannelGraceSeconds) * 1000L;
        while (Environment.TickCount64 < deadline)
        {
            if (IntLatches.Any(latch => _reads[latch].GetValue(0) == 1))
            {
                return;
            }

            await Task.Delay(250, _shutdown.Token).ConfigureAwait(false);
        }

        _logger.LogInformation("INT channel not selected within grace — playing the ground call anyway");
    }

    private bool? ReadNullableBool(string name)
    {
        var read = _reads[name];
        return read.RawValue is null ? null : read.GetValue(false);
    }

    private static GsxServiceStage? StageOf(IReadOnlyList<GsxServiceBoardRow> board, string serviceId)
    {
        foreach (var row in board)
        {
            if (string.Equals(row.ServiceId, serviceId, StringComparison.OrdinalIgnoreCase))
            {
                return row.Stage;
            }
        }

        return null;
    }

    private string FillFuel(string template)
    {
        var kg = _reads[ProsimDataRefNames.FuelTotal].GetValue(0.0);
        var tonnes = (kg / 1000.0).ToString("0.0", CultureInfo.InvariantCulture);
        return template.Replace("{fuel}", tonnes, StringComparison.Ordinal);
    }
}
