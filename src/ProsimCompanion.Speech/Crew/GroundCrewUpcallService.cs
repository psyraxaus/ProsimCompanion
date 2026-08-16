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
    private GroundUpcallCore.UpcallState _state = GroundUpcallCore.UpcallState.Initial;

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
        => _state = GroundUpcallCore.OnFlightCycleReset(_state);

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

            // Edge/window/latch policy is the pure core (campaign #78 — CabinCrewCore's
            // pattern); this shell samples datarefs and delivers the chosen call.
            var board = _diagnostics.Snapshot().ServiceBoard;
            var (nextState, call) = GroundUpcallCore.Evaluate(_state, new GroundUpcallCore.UpcallSample(
                Phase: _flight.CurrentPhase,
                GroundPower: ReadNullableBool(ProsimDataRefNames.GroundPower),
                Chocks: ReadNullableBool(ProsimDataRefNames.Chocks),
                RefuelStage: StageOf(board, GsxServiceIds.Refueling),
                CateringStage: StageOf(board, GsxServiceIds.Catering),
                CallOnGroundPower: options.CallOnGroundPower,
                CallOnChocks: options.CallOnChocks,
                CallOnRefuelComplete: options.CallOnRefuelComplete,
                CallOnCateringComplete: options.CallOnCateringComplete));
            _state = nextState;

            switch (call)
            {
                case GroundUpcallCore.UpcallKind.GroundPower:
                    Run("ground.upcall.gpu", options.GroundPowerText);
                    break;
                case GroundUpcallCore.UpcallKind.Chocks:
                    Run("ground.upcall.chocks", options.ChocksText);
                    break;
                case GroundUpcallCore.UpcallKind.RefuelComplete:
                    Run("ground.upcall.refuel", FillFuel(options.RefuelCompleteText));
                    break;
                case GroundUpcallCore.UpcallKind.CateringComplete:
                    Run("ground.upcall.catering", options.CateringCompleteText);
                    break;
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
