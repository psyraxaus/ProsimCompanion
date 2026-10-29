using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Radios;
using ProsimCompanion.Speech.Arbiter;

namespace ProsimCompanion.Speech.SayIntentions;

/// <summary>
/// The FO works the radios (owner request 2026-10-25, <c>sayIntentions.foRadios.enabled</c>):
/// the SayIntentions copilot is held off (<c>SIAI_COPILOT</c> = 0, re-asserted every two
/// minutes because SI resets it per flight) and every ATC COM1 line the frequency monitor
/// raises is answered the copilot's way — <see cref="ReadbackComposer"/> for the read-back,
/// a COM1 tune through the Core <see cref="IRadioTuner"/> on a hand-off, then the
/// <see cref="CheckInComposer"/> call on the new frequency. Each transmission waits for a quiet
/// COM1 (<see cref="RadioClearGate"/>), is spoken locally in the FO voice when
/// <see cref="SayIntentionsOptions.FoSpeaksTransmission"/> is on (sayAs itself is silent on the
/// SI side — live check 2026-10-25) and goes to ATC through sayAs. "I have the radios" parks the
/// FO (<see cref="SetCaptainOwns"/>); the pilot then answers ATC himself. Transmissions are
/// serialised so a quick ATC pair never produces two overlapping calls.
/// </summary>
public sealed class FoRadiosService : Core.Hosting.IStartupModule, IDisposable
{
    private static readonly TimeSpan CopilotReassert = TimeSpan.FromMinutes(2);
    private const int TickMs = 15_000;

    private readonly IOptionsMonitor<SayIntentionsOptions> _options;
    private readonly SayIntentionsFrequencyMonitor _monitor;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<FoRadiosService> _logger;
    private readonly IRadioTuner? _tuner;
    private readonly IFlightPhaseSource? _phase;
    private readonly IFlightDataSource? _flightData;
    private readonly ISimLocalTime? _localTime;
    private readonly Core.Speech.ISpokenText? _spokenText;
    private readonly RadioClearGate _radioGate;
    private readonly SemaphoreSlim _transmitGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _state = new();

    private Timer? _timer;
    private bool _captainOwns;
    private string? _clearedLevel;
    private long _handoffSerial;
    private DateTimeOffset _copilotAssertedUtc = DateTimeOffset.MinValue;
    private FlightRadioContext _flight = new(false, null, null, null, null, null, null);

    public FoRadiosService(
        IOptionsMonitor<SayIntentionsOptions> options,
        SayIntentionsFrequencyMonitor monitor,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        ILogger<FoRadiosService> logger,
        IRadioTuner? tuner = null,
        IFlightPhaseSource? phase = null,
        IFlightDataSource? flightData = null,
        ISimLocalTime? localTime = null,
        ISimVars? simVars = null,
        Core.Speech.ISpokenText? spokenText = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _monitor = monitor;
        _arbiter = arbiter;
        _eventLog = eventLog;
        _logger = logger;
        _tuner = tuner;
        _phase = phase;
        _flightData = flightData;
        _localTime = localTime;
        _spokenText = spokenText;
        _radioGate = new RadioClearGate(simVars, logger);
    }

    /// <summary>True while the switch is on and the pillar is on.</summary>
    public bool IsActive
    {
        get
        {
            var o = _options.CurrentValue;
            return o.Enabled && o.FoRadios.Enabled;
        }
    }

    /// <summary>Who answers ATC: false = the FO (default), true = the captain ("I have the radios").</summary>
    public bool CaptainOwns
    {
        get
        {
            lock (_state)
            {
                return _captainOwns;
            }
        }
    }

    /// <summary>The pilot's voice override. The SI copilot stays off either way — with the
    /// captain owning, nobody reads back but the captain.</summary>
    public void SetCaptainOwns(bool captainOwns)
    {
        lock (_state)
        {
            _captainOwns = captainOwns;
        }

        _logger.LogInformation("FO radios: {Owner} answers ATC", captainOwns ? "the captain" : "the FO");
        _eventLog.Record("sayintentions.fo-radios", new { owner = captainOwns ? "captain" : "fo" });
    }

    public void Start()
    {
        _monitor.AtcTransmissionObserved += OnAtcTransmission;
        _timer = new Timer(_ => Tick(), null, 2000, TickMs);
    }

    public void Dispose()
    {
        _monitor.AtcTransmissionObserved -= OnAtcTransmission;
        _timer?.Dispose();
        _shutdown.Cancel();
        _radioGate.Dispose();
        _transmitGate.Dispose();
        _shutdown.Dispose();
    }

    /// <summary>Re-reads flight.json and keeps the SI copilot off while the switch is on.</summary>
    private void Tick()
    {
        try
        {
            if (!IsActive)
            {
                return;
            }

            var context = FlightJsonFile.TryReadRadioContext();
            if (context is not null)
            {
                lock (_state)
                {
                    if (!_flight.IsActive && context.IsActive)
                    {
                        _clearedLevel = null; // a new flight: the last clearance was the old one's
                    }

                    _flight = context;
                }
            }

            var apiKey = SayIntentionsFrequencyMonitor.ResolveSapiCredential(_options.CurrentValue);
            if (apiKey is null || context is not { IsActive: true })
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (now - _copilotAssertedUtc >= CopilotReassert)
            {
                _copilotAssertedUtc = now;
                _ = SapiClient.SetVarAsync(apiKey, "SIAI_COPILOT", "0", _logger, _shutdown.Token);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FO radios tick failed");
        }
    }

    private void OnAtcTransmission(SayIntentionsFrequencyMonitor.AtcTransmission tx)
    {
        try
        {
            if (!IsActive)
            {
                return;
            }

            FlightRadioContext flight;
            bool captainOwns;
            lock (_state)
            {
                flight = _flight;
                captainOwns = _captainOwns;
                if (ClearedLevelParser.Parse(tx.Text) is { } cleared)
                {
                    _clearedLevel = cleared;
                }
            }

            if (captainOwns)
            {
                _logger.LogDebug("FO radios: captain has the radios — \"{Atc}\" left to him", tx.Text);
                return;
            }

            var readback = ReadbackComposer.Compose(tx.Text, flight.Callsign, tx.Station);
            if (readback is null)
            {
                _eventLog.Record("sayintentions.readback", new { atc = tx.Text, kind = "none", transmit = "" });
                return;
            }

            _ = RespondAsync(tx, readback, flight);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FO radios: ATC line handling failed");
        }
    }

    private async Task RespondAsync(SayIntentionsFrequencyMonitor.AtcTransmission tx, Readback readback, FlightRadioContext flight)
    {
        var options = _options.CurrentValue;
        var apiKey = SayIntentionsFrequencyMonitor.ResolveSapiCredential(options);
        if (apiKey is null)
        {
            return;
        }

        long serial;
        lock (_state)
        {
            serial = readback.Kind == ReadbackKind.Handoff ? ++_handoffSerial : _handoffSerial;
        }

        await _transmitGate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
        try
        {
            await TransmitAsync(apiKey, readback, options, "sayintentions.readback", new
            {
                atc = tx.Text,
                kind = readback.Kind.ToString().ToLowerInvariant(),
                transmit = readback.Transmit,
            }).ConfigureAwait(false);

            if (readback.Kind != ReadbackKind.Handoff || readback.HandoffKhz is not { } khz)
            {
                return;
            }

            if (options.FoRadios.TuneOnHandoff && _tuner is not null)
            {
                var outcome = await _tuner.TuneCom1Async(khz, "handoff", _shutdown.Token).ConfigureAwait(false);
                if (outcome is not RadioTuneOutcome.Tuned)
                {
                    await _arbiter.EnqueueAsync(new SpeechRequest(
                        $"Could not tune {Callouts.Aviation.ToDigits(RadioFrequency.Format(khz))}, Captain — the radios are yours.",
                        SpeechPriority.Normal, Ttl: TimeSpan.FromSeconds(20), Tag: "sayintentions.tx")).ConfigureAwait(false);
                    return;
                }
            }

            if (!options.FoRadios.CheckIn)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(options.FoRadios.CheckInDelaySeconds, 3, 30)), _shutdown.Token).ConfigureAwait(false);
            lock (_state)
            {
                if (_captainOwns || serial != _handoffSerial)
                {
                    return; // the pilot took the radios, or ATC handed us on again meanwhile
                }
            }

            var checkIn = CheckInComposer.Compose(BuildCheckInContext(flight));
            if (checkIn is null)
            {
                return;
            }

            await TransmitAsync(apiKey, checkIn, options, "sayintentions.checkin", new
            {
                station = readback.HandoffStation,
                khz,
                transmit = checkIn.Transmit,
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FO radios: transmission failed");
        }
        finally
        {
            _transmitGate.Release();
        }
    }

    /// <summary>Quiet COM1 → the FO voice (awaited, so ATC reacts after the words) → sayAs.</summary>
    private async Task TransmitAsync(string apiKey, Readback line, SayIntentionsOptions options, string eventType, object payload)
    {
        var (waited, busy) = await _radioGate.WaitForClearAsync(_shutdown.Token).ConfigureAwait(false);
        if (options.FoSpeaksTransmission)
        {
            await _arbiter.EnqueueAsync(new SpeechRequest(
                line.Spoken, SpeechPriority.Normal, Ttl: TimeSpan.FromSeconds(30), Tag: "sayintentions.tx")).ConfigureAwait(false);
        }

        var sent = await SapiClient.SayAsAsync(apiKey, line.Transmit, _logger, _shutdown.Token).ConfigureAwait(false);
        _logger.LogInformation("FO radios {Kind}: \"{Transmit}\" (radio wait {WaitMs} ms, busy {Busy}, sent {Sent})",
            eventType, line.Transmit, (int)waited.TotalMilliseconds, busy, sent);
        _eventLog.Record(eventType, new
        {
            payload,
            spoken = line.Spoken,
            radioWaitMs = (int)waited.TotalMilliseconds,
            radioBusyAtTx = busy,
            sent,
        });
    }

    private CheckInContext BuildCheckInContext(FlightRadioContext flight)
    {
        var view = _phase?.Snapshot();
        var data = _flightData?.Sample();
        var phase = view?.Phase ?? FlightPhase.Unknown;
        var onGround = data?.OnGround ?? !phase.IsAirborne();
        string? cleared;
        lock (_state)
        {
            cleared = _clearedLevel;
        }

        return new CheckInContext(
            Greeting: Core.Aircraft.TimeOfDayGreeting.Greeting(_localTime?.LocalTimeOfDay),
            Callsign: flight.Callsign ?? "",
            Phase: phase,
            OnGround: onGround,
            AltitudeFt: data?.AltitudeFt ?? 0,
            ClearedLevel: cleared,
            Sid: flight.Sid,
            Star: flight.Star,
            DepartureRunway: flight.DepartureRunway,
            ArrivalRunway: flight.ArrivalRunway,
            Gate: flight.Gate,
            // The crew's names for the procedures (2026-10-29) — flight.json names no airport,
            // so the OFP navlog tier answers and the DFD tier only when the ident is unique.
            SidSpoken: flight.Sid is { Length: > 0 } ? _spokenText?.Procedure(flight.Sid) : null,
            StarSpoken: flight.Star is { Length: > 0 } ? _spokenText?.Procedure(flight.Star) : null);
    }
}
