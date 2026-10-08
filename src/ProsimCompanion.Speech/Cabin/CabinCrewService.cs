using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Playback;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Cabin;

/// <summary>
/// Cabin-crew simulation shell: 1 Hz tick over <see cref="CabinCrewCore"/>, then the
/// interphone flow — ring the ding-dong, publish "cabin calling" (the real ACP CAB-CALL light
/// I_ASP_CAB_CALL is read-only in the SDK, so the web banner is the flash substitute), wait for
/// a CAB receive latch on ANY of the three audio panels (the predecessor only watched the
/// captain's) or the grace, then speak the purser report with an optional FO acknowledgement.
/// Reports are tagged cabin.* so the sterile rule exempts them. Detect-and-report only, with
/// ONE opt-in exception: the auto-answer (issue #11, <see cref="CabinAutoAnswer"/>) selects
/// CAB reception on the FO's audio panel after a delay so the report proceeds without the
/// pilot touching the ACP, and deselects it again once the report is done.
/// <para>
/// The cabin-secure wait (issue #134) is scaled by the passengers on board: GSX's boarded
/// count when it has one, else the OFP figure, else 0 (minimum wait only). The arm/report
/// pair is published on <see cref="SpeechStatusStore"/> so the Flight Status page shows
/// "securing" and the purser answers a hail with "still securing" meanwhile.
/// </para>
/// <para>
/// The purser cruise query (2026-10-09) rides the same tick: once per flight in the cruise,
/// after the core's jittered delay, the chime-and-CAB flow runs and then
/// <see cref="CabinCruiseQueryDialogue"/> borrows the mic for the captain's reply. A mic that
/// is busy (hail, tech log, ECAM) hands the latch back for a retry a minute later.
/// </para>
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
    private readonly GsxDiagnosticsStore _gsx;
    private readonly OfpStore _ofp;
    private readonly ILogger<CabinCrewService> _logger;
    private readonly GroundOpsSignals? _signals;
    private readonly IOptionsMonitor<SpeechOptions>? _speechOptions;
    private readonly IMicOwnership? _mic;
    private readonly CabinCruiseQueryDialogue? _cruiseQuery;
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
        GsxDiagnosticsStore gsx,
        OfpStore ofp,
        ILogger<CabinCrewService> logger,
        GroundOpsSignals? signals = null,
        IOptionsMonitor<SpeechOptions>? speechOptions = null,
        IMicOwnership? mic = null,
        CabinCruiseQueryDialogue? cruiseQuery = null)
    {
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(flightData);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(gsx);
        ArgumentNullException.ThrowIfNull(ofp);
        ArgumentNullException.ThrowIfNull(logger);

        _dataRefs = dataRefs;
        _flightData = flightData;
        _flight = flight;
        _arbiter = arbiter;
        _playback = playback;
        _options = options;
        _eventLog = eventLog;
        _store = store;
        _gsx = gsx;
        _ofp = ofp;
        _logger = logger;
        _signals = signals;
        _speechOptions = speechOptions;
        _mic = mic;
        _cruiseQuery = cruiseQuery;
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
    {
        _core.OnPhaseChanged(e.Previous, e.Current);
        // The core's re-arm (cold-and-dark / turnaround Preflight) is the same edge that
        // clears the published state — a stale "secure" must not carry into the next leg.
        if (_core.SecureDueAtUtc is null && _core.SecureDelaySeconds == 0)
        {
            SetSecureState(CabinSecureState.None);
        }
    }

    /// <summary>Passengers on board for the cabin-secure wait: GSX's boarded count (the truth
    /// once boarding ran), else the OFP figure (no GSX, or GSX never counted), else 0.</summary>
    private int PaxOnBoard()
    {
        if (_gsx.Snapshot().BoardingCounters?.PaxBoarded is > 0 and var boarded)
        {
            return boarded;
        }

        return _ofp.Current?.PaxCount is > 0 and var planned ? planned : 0;
    }

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
                HasBeenAirborne: _flight.Snapshot().HasBeenAirborneThisSession,
                PaxOnBoard: PaxOnBoard(),
                NowUtc: DateTimeOffset.UtcNow,
                VoicePaused: _store.Snapshot().ListeningPaused);

            var action = _core.Evaluate(sample, options, () => Random.Shared.NextDouble());
            if (action == CabinAction.None)
            {
                return;
            }

            if (action == CabinAction.CruiseQuery)
            {
                if (_cruiseQuery is null)
                {
                    // No dialogue in this build (the mic seam is absent) — the latch stays
                    // spent; a call nobody can answer is worse than none.
                    return;
                }

                _busy = true;
                _ = RunCruiseQueryAsync();
                return;
            }

            if (action == CabinAction.SecureArmed)
            {
                // Not a report — nothing plays. The pill and the hail reply switch to
                // "securing"; the event carries the draw so a flight probe can check the
                // report waited at least this long (issue #134).
                _logger.LogInformation(
                    "Cabin securing: report in {DelaySeconds} s ({Pax} pax on board)",
                    _core.SecureDelaySeconds, sample.PaxOnBoard);
                _eventLog.Record("cabin.secure-armed", new
                {
                    delaySeconds = _core.SecureDelaySeconds,
                    pax = sample.PaxOnBoard,
                    dueUtc = _core.SecureDueAtUtc,
                });
                SetSecureState(CabinSecureState.Securing);
                return;
            }

            if (action == CabinAction.SecureReport)
            {
                SetSecureState(CabinSecureState.Secure);
                _signals?.RaiseCabinSecured(); // the outbound "cabin secure" (#151)
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
        var options = _options.CurrentValue;
        // The window is judged at the moment of the call, never re-judged mid-wait: a call
        // placed in taxi-out is answered even if the roll starts during the delay.
        var plan = CabinAutoAnswer.Plan(
            _flight.CurrentPhase, options,
            _speechOptions is not null && PilotSeatMap.HumanIsRightSeat(_speechOptions.CurrentValue));
        var answered = false;
        try
        {
            SetCalling(true);
            _eventLog.Record("cabin.calling", new { report = tag, autoAnswer = plan?.Gate.ToString() });
            answered = await RingAndWaitAsync(options, plan).ConfigureAwait(false);

            // Awaited to the terminal outcome so the FO acknowledgement can never overtake
            // the report it acknowledges. The report is the purser speaking; the ack below
            // stays role-less (that's the FO). An auto-answered call always gets the FO's
            // acknowledgement: the FO took the call, so the FO closes it.
            await _arbiter.EnqueueAsync(new SpeechRequest(
                purserText, SpeechPriority.Normal, Ttl: ttl, Tag: tag,
                Role: SpeechRole.Purser)).ConfigureAwait(false);
            var ack = foAckText ?? (answered ? AckTextFor(tag, options) : null);
            if (!string.IsNullOrWhiteSpace(ack))
            {
                await _arbiter.EnqueueAsync(new SpeechRequest(
                    ack, SpeechPriority.Normal, Tag: tag + ".ack")).ConfigureAwait(false);
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
            if (answered && plan is not null)
            {
                await ReleaseAnswerAsync(plan).ConfigureAwait(false);
            }

            _busy = false;
        }
    }

    /// <summary>The FO acknowledgement for a report whose call-site passed none (the
    /// FoAcknowledge option is off): used only when the FO answered the call itself.</summary>
    private static string? AckTextFor(string tag, CabinOptions options) => tag switch
    {
        "cabin.secure" => options.CabinSecureAckText,
        "cabin.ready" => options.CabinReadyAckText,
        _ => null,
    };

    private async Task RunCruiseQueryAsync()
    {
        try
        {
            var result = await _cruiseQuery!.RunAsync(async _ =>
            {
                SetCalling(true);
                _eventLog.Record("cabin.calling", new { report = CabinCruiseQueryDialogue.Tag });
                // The cruise query is the purser's own call in the cruise: never auto-answered
                // (#11 answers only the secure/ready calls on the ground and on approach).
                await RingAndWaitAsync(_options.CurrentValue, plan: null).ConfigureAwait(false);
            }, _shutdown.Token).ConfigureAwait(false);

            if (result.Outcome == CruiseQueryOutcome.MicBusy)
            {
                _core.ReopenCruiseQuery(DateTimeOffset.UtcNow);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Purser cruise query failed");
        }
        finally
        {
            SetCalling(false);
            _busy = false;
        }
    }

    /// <summary>Rings the interphone chime, then waits (per config) for a CAB receive channel
    /// — a pilot selecting CAB, or, with a plan, the FO's own answer after the delay. The
    /// chime rings even if CAB is never selected — a report is never lost. Returns true when
    /// the FO's answer write went out (and must be released after the report).</summary>
    private async Task<bool> RingAndWaitAsync(CabinOptions options, CabinAutoAnswerPlan? plan)
    {
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

        var now = Environment.TickCount64;
        var graceDeadline = options.RequireCabChannel
            ? now + Math.Max(0, options.CabChannelGraceSeconds) * 1000L
            : now;
        var answerDueAt = plan is null ? (long?)null : now + plan.DelayMs;
        // With a plan the wait outlives the grace when needed: the answer must get its echo
        // window even when CAB is not required at all (then the report simply follows the
        // answer, as a pilot's own CAB press would be followed by the purser speaking).
        var deadline = answerDueAt is { } due ? Math.Max(graceDeadline, due + CabinAutoAnswer.EchoTimeoutMs) : graceDeadline;
        var answered = false;

        while (true)
        {
            // The latch descriptors fall back to 1 (unmuted / fail-audible, #83), so "no data
            // yet" is decided on the liveness probe (RawValue) — a dead subscription must wait
            // out the grace exactly as the old fallback-0 read did.
            var cabSelected = _cabLatches.Any(latch => latch.RawValue is not null && latch.Value == 1);
            // A pilot answer during the delay cancels the plan: the FO never writes over the
            // pilot's own selection (verdict pilot-answered).
            if (cabSelected && plan is not null && !answered)
            {
                RecordAutoAnswer(plan, "pilot-answered");
            }

            switch (CabinAutoAnswer.Step(cabSelected, Environment.TickCount64, deadline, answerDueAt, answered))
            {
                case CabinCallWaitStep.Proceed:
                    if (!cabSelected && answered)
                    {
                        RecordAutoAnswer(plan!, "written-no-echo");
                    }

                    if (!cabSelected && options.RequireCabChannel)
                    {
                        _logger.LogInformation("CAB channel not selected within grace — playing the cabin call anyway");
                    }

                    return answered;

                case CabinCallWaitStep.Answer:
                    answered = true;
                    if (!await TryAnswerAsync(plan!).ConfigureAwait(false))
                    {
                        // A refused / failed write ends the plan; the normal grace still applies.
                        answered = false;
                        answerDueAt = null;
                        deadline = graceDeadline;
                    }

                    break;
            }

            await Task.Delay(250, _shutdown.Token).ConfigureAwait(false);
        }
    }

    /// <summary>The answer itself: CAB reception latched ON on the FO's panel. Refused while
    /// a guided dialogue holds the microphone (the FO is mid-conversation with the pilot).</summary>
    private async Task<bool> TryAnswerAsync(CabinAutoAnswerPlan plan)
    {
        if (_mic?.IsBorrowed == true)
        {
            RecordAutoAnswer(plan, "mic-borrowed");
            return false;
        }

        try
        {
            await _dataRefs.WriteAsync(plan.DataRef, 1, _shutdown.Token).ConfigureAwait(false);
            RecordAutoAnswer(plan, "answered");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cabin call auto-answer: writing {DataRef} failed", plan.DataRef);
            RecordAutoAnswer(plan, "write-failed");
            return false;
        }
    }

    /// <summary>After the report the FO deselects CAB again — the panel is left as found. Only
    /// when the latch still reads selected: a pilot who deselected meanwhile is not fought.</summary>
    private async Task ReleaseAnswerAsync(CabinAutoAnswerPlan plan)
    {
        try
        {
            var latch = _cabLatches.FirstOrDefault(l => l.Name == plan.DataRef);
            if (latch is { RawValue: not null } && latch.Value != 1)
            {
                return;
            }

            await _dataRefs.WriteAsync(plan.DataRef, 0, _shutdown.Token).ConfigureAwait(false);
            RecordAutoAnswer(plan, "released");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cabin call auto-answer: releasing {DataRef} failed", plan.DataRef);
        }
    }

    private void RecordAutoAnswer(CabinAutoAnswerPlan plan, string verdict)
    {
        _logger.LogInformation(
            "Cabin call auto-answer ({Gate}, {Phase}, after {DelayMs} ms, {DataRef}): {Verdict}",
            plan.Gate, _flight.CurrentPhase, plan.DelayMs, plan.DataRef, verdict);
        _eventLog.Record("cabin.auto-answer", new
        {
            phase = _flight.CurrentPhase.ToString(),
            gate = plan.Gate.ToString(),
            delayMs = plan.DelayMs,
            dataref = plan.DataRef,
            verdict,
        });
    }

    private void SetCalling(bool value)
        => _store.Update(s => s with { CabinCalling = value });

    private void SetSecureState(CabinSecureState state)
        => _store.Update(s => s.CabinSecure == state ? s : s with { CabinSecure = state });
}
