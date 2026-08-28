using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Playback;

namespace ProsimCompanion.Speech.Cabin;

/// <summary>
/// Cabin-crew simulation shell: 1 Hz tick over <see cref="CabinCrewCore"/>, then the
/// interphone flow — ring the ding-dong, publish "cabin calling" (the real ACP CAB-CALL light
/// I_ASP_CAB_CALL is read-only in the SDK, so the web banner is the flash substitute), wait for
/// a CAB receive latch on ANY of the three audio panels (the predecessor only watched the
/// captain's) or the grace, then speak the purser report with an optional FO acknowledgement.
/// Reports are tagged cabin.* so the sterile rule exempts them. Detect-and-report only — never
/// writes the sim.
/// </summary>
public sealed class CabinCrewService : Core.Hosting.IStartupModule, IDisposable
{
    private readonly IProsimDataRefs _dataRefs;
    private readonly IFlightDataSource _flightData;
    private readonly IFlightPhaseSource _flight;
    private readonly ISpeechArbiter _arbiter;
    private readonly ISpeechPlayback _playback;
    private readonly IOptionsMonitor<CabinOptions> _options;
    private readonly JsonlEventLog _eventLog;
    private readonly SpeechStatusStore _store;
    private readonly ILogger<CabinCrewService> _logger;
    private readonly CabinCrewCore _core = new();
    private readonly CancellationTokenSource _shutdown = new();

    private IDataRefSubscription<bool>? _doorLF;
    private IDataRefSubscription<bool>? _doorLA;
    private IDataRefSubscription<bool>? _doorRF;
    private IDataRefSubscription<bool>? _doorRA;
    private IDataRefSubscription<int>? _beacon;
    private IDataRefSubscription<int>? _signs;
    private IDataRefSubscription<int>[] _cabLatches = [];
    private IDataRefSubscription[] _reads = [];
    private Timer? _timer;
    private volatile bool _busy;

    public CabinCrewService(
        IProsimDataRefs dataRefs,
        IFlightDataSource flightData,
        IFlightPhaseSource flight,
        ISpeechArbiter arbiter,
        ISpeechPlayback playback,
        IOptionsMonitor<CabinOptions> options,
        JsonlEventLog eventLog,
        SpeechStatusStore store,
        ILogger<CabinCrewService> logger)
    {
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(flightData);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        _dataRefs = dataRefs;
        _flightData = flightData;
        _flight = flight;
        _arbiter = arbiter;
        _playback = playback;
        _options = options;
        _eventLog = eventLog;
        _store = store;
        _logger = logger;
    }

    public void Start()
    {
        _doorLF = _dataRefs.Subscribe(ProsimDataRefNames.Door1L);
        _doorLA = _dataRefs.Subscribe(ProsimDataRefNames.Door4L);
        _doorRF = _dataRefs.Subscribe(ProsimDataRefNames.Door1R);
        _doorRA = _dataRefs.Subscribe(ProsimDataRefNames.Door4R);
        _beacon = _dataRefs.Subscribe(ProsimDataRefNames.OhExtLtBeacon);
        _signs = _dataRefs.Subscribe(ProsimDataRefNames.OhSigns);
        _cabLatches =
        [
            _dataRefs.Subscribe(ProsimDataRefNames.Acp1CabLatch),
            _dataRefs.Subscribe(ProsimDataRefNames.Acp2CabLatch),
            _dataRefs.Subscribe(ProsimDataRefNames.Acp3CabLatch),
        ];
        _reads = [_doorLF, _doorLA, _doorRF, _doorRA, _beacon, _signs, .. _cabLatches];

        _flight.PhaseChanged += OnPhaseChanged;
        _timer = new Timer(_ => Tick(), null, 1000, 1000);
    }

    public void Dispose()
    {
        _flight.PhaseChanged -= OnPhaseChanged;
        _timer?.Dispose();
        _shutdown.Cancel();
        foreach (var read in _reads)
        {
            read.Dispose();
        }
    }

    private void OnPhaseChanged(object? sender, FlightPhaseChangedEventArgs e)
        => _core.OnPhaseChanged(e.Previous, e.Current);

    private void Tick()
    {
        try
        {
            var options = _options.CurrentValue;
            // Flight-live gate (issue #114): ProSim pushes plausible data with no MSFS session.
            if (!options.Enabled || _busy || _reads.Length == 0 || !_flight.IsLive)
            {
                return;
            }

            // Stale subscriptions mean ProSim is gone — a disconnected sim must never trigger
            // a report off held-over values.
            if (_reads.Any(r => r.IsStale) || _beacon!.RawValue is null)
            {
                return;
            }

            var flightData = _flightData.Sample();
            var sample = new CabinTickSample(
                _flight.CurrentPhase,
                DoorsClosed: !_doorLF!.Value && !_doorLA!.Value
                    && !_doorRF!.Value && !_doorRA!.Value,
                BeaconOn: _beacon.Value == 1,
                SeatbeltSignsMode: _signs!.Value,
                AltitudeFt: flightData.AltitudeFt,
                VerticalSpeedFpm: flightData.VerticalSpeedFpm,
                HasBeenAirborne: _flight.Snapshot().HasBeenAirborneThisSession);

            var action = _core.Evaluate(sample, options, () => Random.Shared.NextDouble());
            if (action == CabinAction.None)
            {
                return;
            }

            _busy = true;
            _ = action switch
            {
                CabinAction.SecureReport => RunReportAsync(
                    "cabin.secure", options.CabinSecureText,
                    options.FoAcknowledge ? options.CabinSecureAckText : null),
                CabinAction.ReadyReport => RunReportAsync(
                    "cabin.ready", options.CabinReadyText,
                    options.FoAcknowledge ? options.CabinReadyAckText : null),
                CabinAction.BoardingDelay => RunReportAsync(
                    "cabin.boarding", options.BoardingDelayText, foAckText: null,
                    ttl: TimeSpan.FromSeconds(30)),
                _ => Task.CompletedTask,
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cabin tick failed");
        }
    }

    private async Task RunReportAsync(string tag, string purserText, string? foAckText, TimeSpan? ttl = null)
    {
        try
        {
            SetCalling(true);
            _eventLog.Record("cabin.calling", new { report = tag });
            await RingAndWaitAsync().ConfigureAwait(false);

            // Awaited to the terminal outcome so the FO acknowledgement can never overtake
            // the report it acknowledges. The report is the purser speaking; the ack below
            // stays role-less (that's the FO).
            await _arbiter.EnqueueAsync(new SpeechRequest(
                purserText, SpeechPriority.Normal, Ttl: ttl, Tag: tag,
                Role: SpeechRole.Purser)).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(foAckText))
            {
                await _arbiter.EnqueueAsync(new SpeechRequest(
                    foAckText, SpeechPriority.Normal, Tag: tag + ".ack")).ConfigureAwait(false);
            }

            _eventLog.Record("cabin.report", new { report = tag, text = purserText });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cabin report {Tag} failed", tag);
        }
        finally
        {
            SetCalling(false);
            _busy = false;
        }
    }

    /// <summary>Rings the interphone chime, then waits (per config) for a CAB receive channel.
    /// The chime rings even if CAB is never selected — a report is never lost.</summary>
    private async Task RingAndWaitAsync()
    {
        var options = _options.CurrentValue;
        if (options.Chime)
        {
            try
            {
                await _playback.PlayChimeAsync("cabin", _shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Cabin chime failed");
            }
        }

        if (!options.RequireCabChannel)
        {
            return;
        }

        var deadline = Environment.TickCount64 + Math.Max(0, options.CabChannelGraceSeconds) * 1000L;
        while (Environment.TickCount64 < deadline)
        {
            // The latch descriptors fall back to 1 (unmuted / fail-audible, #83), so "no data
            // yet" is decided on the liveness probe (RawValue) — a dead subscription must wait
            // out the grace exactly as the old fallback-0 read did.
            if (_cabLatches.Any(latch => latch.RawValue is not null && latch.Value == 1))
            {
                return;
            }

            await Task.Delay(250, _shutdown.Token).ConfigureAwait(false);
        }

        _logger.LogInformation("CAB channel not selected within grace — playing the cabin call anyway");
    }

    private void SetCalling(bool value)
        => _store.Update(s => s with { CabinCalling = value });
}
