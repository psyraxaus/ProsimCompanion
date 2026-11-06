using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Airports;
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
    private readonly RunwayLocator? _runways;
    private readonly IDepartureRunwaySource? _departureRunway;
    private readonly Core.Aircraft.Ofp.OfpStore? _ofp;
    private readonly Core.Weather.HeroWeatherStore? _weather;
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
    private string? _departureGeometryFor;
    private RunwayGeometry? _departureGeometry;
    private int _geometryLookupBusy;
    private DateTimeOffset _lastAtcLineUtc = DateTimeOffset.MinValue;

    /// <summary>Floor between the hand-off read-back and the retune: the OLD station must hear
    /// "Contacting X on Y" (EHAM 2026-11-01: sayAs and retune in the same millisecond, the new
    /// station answered the read-back as our check-in, the FO then checked in again).</summary>
    internal static readonly TimeSpan HandoffTuneGrace = TimeSpan.FromSeconds(3);

    /// <summary>Longest the check-in waits for COM1 to read the new frequency.</summary>
    internal static readonly TimeSpan FrequencyWaitMax = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan FrequencyPoll = TimeSpan.FromMilliseconds(500);

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
        Core.Speech.ISpokenText? spokenText = null,
        RunwayLocator? runways = null,
        IDepartureRunwaySource? departureRunway = null,
        Core.Aircraft.Ofp.OfpStore? ofp = null,
        Core.Weather.HeroWeatherStore? weather = null)
    {
        _ofp = ofp;
        _weather = weather;
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
        _runways = runways;
        _departureRunway = departureRunway;
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

            PrimeDepartureRunway(context);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FO radios tick failed");
        }
    }

    /// <summary>Resolves the departure runway's geometry once per (airport, runway) while on
    /// the ground, so the Tower check-in can say "at the holding point" (owner report
    /// 2026-11-01). The FMS runway source names the airport; flight.json names only the
    /// runway, so without the FMS the distance stays unknown and the old wording holds.</summary>
    private void PrimeDepartureRunway(FlightRadioContext flight)
    {
        if (_runways is null || _phase?.Snapshot().Phase.IsAirborne() != false)
        {
            return;
        }

        string? icao = null, runway = null;
        if (_departureRunway?.Current() is { } live)
        {
            (icao, runway) = live;
        }
        else if (RunwayLocator.NormalizeIdent(flight.DepartureRunway) is { } ident)
        {
            runway = ident;
        }

        if (string.IsNullOrWhiteSpace(icao) || string.IsNullOrWhiteSpace(runway))
        {
            return;
        }

        var key = icao + "/" + runway;
        lock (_state)
        {
            if (string.Equals(_departureGeometryFor, key, StringComparison.Ordinal))
            {
                return;
            }
        }

        if (Interlocked.Exchange(ref _geometryLookupBusy, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var geometry = await _runways.FindAsync(icao, runway, _shutdown.Token).ConfigureAwait(false);
                lock (_state)
                {
                    _departureGeometryFor = key;
                    _departureGeometry = geometry;
                }

                _logger.LogInformation("FO radios: departure runway {Airport}/{Runway} geometry {Outcome} (for the holding-point check-in)",
                    icao, runway, geometry is null ? "unknown" : "from " + geometry.Source);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "FO radios: departure runway lookup failed");
            }
            finally
            {
                Interlocked.Exchange(ref _geometryLookupBusy, 0);
            }
        });
    }

    /// <summary>Distance from the aircraft to the departure runway strip, null while the
    /// geometry or the position is unknown.</summary>
    private double? DistanceToDepartureRunwayNm()
    {
        RunwayGeometry? geometry;
        lock (_state)
        {
            geometry = _departureGeometry;
        }

        var position = _phase?.Snapshot().Data?.Position;
        return geometry is not null && position is { } p ? geometry.DistanceToRunwayNm(p) : null;
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
                if (!ReadbackComposer.IsBroadcast(tx.Text))
                {
                    _lastAtcLineUtc = DateTimeOffset.UtcNow; // ATC spoke to us (the check-in rule reads it)
                }

                if (ClearedLevelParser.Parse(tx.Text) is { } cleared)
                {
                    _clearedLevel = cleared;
                }
            }

            if (captainOwns)
            {
                _logger.LogInformation("FO radios: the captain has the radios — \"{Atc}\" left to them", tx.Text);
                RecordDrop(tx.Text, "the captain has the radios");
                return;
            }

            var readback = ReadbackComposer.Compose(tx.Text, flight.Callsign, tx.Station, OurAtisLetter());
            if (readback is null)
            {
                // Same shape as a transmitted read-back (probe si-fo-radios reads payload.* and
                // spoken/sent on every event; the flat form failed it on 2026-10-31).
                _eventLog.Record("sayintentions.readback", new
                {
                    payload = new { atc = tx.Text, kind = "none", transmit = "" },
                    spoken = "",
                    radioWaitMs = 0,
                    radioBusyAtTx = false,
                    sent = false,
                });
                return;
            }

            _ = RespondAsync(tx, readback, flight);
        }
        catch (Exception ex)
        {
            // Every silent path here cost a whole departure's read-backs once (EHAM 2026-11-01,
            // 15:13–15:25 local: push, taxi, tower, takeoff all unanswered, nothing logged).
            _logger.LogWarning(ex, "FO radios: ATC line handling failed for \"{Atc}\"", tx.Text);
            RecordDrop(tx.Text, "handler error: " + ex.GetType().Name);
        }
    }

    /// <summary>A COM1 ATC line the FO did not answer, and why — same shape as a read-back
    /// event so the probe's required-fields block holds (kind "dropped").</summary>
    private void RecordDrop(string atc, string reason)
    {
        _eventLog.Record("sayintentions.readback", new
        {
            payload = new { atc, kind = "dropped", transmit = "" },
            spoken = "",
            radioWaitMs = 0,
            radioBusyAtTx = false,
            sent = false,
            reason,
        });
    }

    private async Task RespondAsync(SayIntentionsFrequencyMonitor.AtcTransmission tx, Readback readback, FlightRadioContext flight)
    {
        var options = _options.CurrentValue;
        var apiKey = SayIntentionsFrequencyMonitor.ResolveSapiCredential(options);
        if (apiKey is null)
        {
            _logger.LogWarning("FO radios: no SayIntentions API key at hand — \"{Atc}\" not read back", tx.Text);
            RecordDrop(tx.Text, "no API key");
            return;
        }

        long serial;
        lock (_state)
        {
            serial = readback.Kind == ReadbackKind.Handoff ? ++_handoffSerial : _handoffSerial;
        }

        await _transmitGate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
        var holdsGate = true;
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

            // The gate serialises TRANSMISSIONS, not the hand-off's waits: holding it through
            // the frequency-clear wait, the tune and the COM1 wait queued every later read-back
            // behind one hand-off for up to ~64 s (review 2026-11-05). Released here; taken
            // again only around the check-in's own transmission.
            _transmitGate.Release();
            holdsGate = false;

            // The OLD station must hear "Contacting X on Y" before COM1 changes (EHAM 2026-11-01:
            // the sayAs and the retune were logged in the same millisecond; SayIntentions
            // processed the text after the change and the NEW station answered the read-back as
            // our first call — "Schiphol Departure, climb altitude 6,000" two seconds after
            // "Contacting Schiphol Departure…" — and the FO then checked in a second time). Wait
            // for the frequency to go quiet (our own words, ATC's goodbye), never less than the grace.
            var readBackAt = DateTimeOffset.UtcNow;
            await _radioGate.WaitForClearAsync(_shutdown.Token).ConfigureAwait(false);
            var grace = HandoffTuneGrace - (DateTimeOffset.UtcNow - readBackAt);
            if (grace > TimeSpan.Zero)
            {
                await Task.Delay(grace, _shutdown.Token).ConfigureAwait(false);
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

            var tunedAt = DateTimeOffset.UtcNow;
            if (!options.FoRadios.CheckIn)
            {
                return;
            }

            // The check-in goes out on the new frequency only: COM1 must read it first (the
            // captain tunes by hand when the auto-tune is off), then the configured pause.
            if (!await WaitForCom1Async(khz, _shutdown.Token).ConfigureAwait(false))
            {
                SkipCheckIn(readback, khz, $"COM1 is not on {RadioFrequency.Format(khz)} after {FrequencyWaitMax.TotalSeconds:0} s");
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(options.FoRadios.CheckInDelaySeconds, 3, 30)), _shutdown.Token).ConfigureAwait(false);
            DateTimeOffset lastAtcLine;
            lock (_state)
            {
                if (_captainOwns || serial != _handoffSerial)
                {
                    return; // the pilot took the radios, or ATC handed us on again meanwhile
                }

                lastAtcLine = _lastAtcLineUtc;
            }

            // The new station already spoke to us (it took an instruction, or the read-back, as
            // our first call): a second "good afternoon" would only repeat ourselves.
            if (lastAtcLine > tunedAt)
            {
                SkipCheckIn(readback, khz, "ATC already answered us on the new frequency");
                return;
            }

            var checkIn = CheckInComposer.Compose(BuildCheckInContext(flight));
            if (checkIn is null)
            {
                return;
            }

            await _transmitGate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            holdsGate = true;
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
            if (holdsGate)
            {
                _transmitGate.Release();
            }
        }
    }

    /// <summary>True once COM1 active reads <paramref name="khz"/> (within the channel
    /// tolerance), false after <see cref="FrequencyWaitMax"/>. Without a readable COM1 (no
    /// ProSim) the answer is true at once — nothing to wait for, the old timing holds.</summary>
    private async Task<bool> WaitForCom1Async(int khz, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        while (true)
        {
            var active = _tuner?.ActiveKhz;
            if (active is null || Math.Abs(active.Value - khz) <= FrequencyWatchCore.ToleranceKhz)
            {
                return true;
            }

            if (DateTimeOffset.UtcNow - started >= FrequencyWaitMax)
            {
                return false;
            }

            await Task.Delay(FrequencyPoll, cancellationToken).ConfigureAwait(false);
        }
    }

    private void SkipCheckIn(Readback readback, int khz, string reason)
    {
        _logger.LogInformation("FO radios: check-in on {Station} skipped — {Reason}", readback.HandoffStation, reason);
        _eventLog.Record("sayintentions.checkin", new
        {
            payload = new { station = readback.HandoffStation, khz, transmit = "" },
            spoken = "",
            radioWaitMs = 0,
            radioBusyAtTx = false,
            sent = false,
            reason,
        });
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
            AltitudeFt: data?.SpokenAltitudeFt ?? 0, // the pilot's PFD (issue #198), not the true altitude
            ClearedLevel: cleared,
            Sid: flight.Sid,
            Star: flight.Star,
            DepartureRunway: flight.DepartureRunway,
            ArrivalRunway: flight.ArrivalRunway,
            Gate: flight.Gate,
            DistanceToDepartureRunwayNm: onGround ? DistanceToDepartureRunwayNm() : null,
            GroundSpeedKt: data?.GroundSpeedKt ?? 0,
            VerticalSpeedFpm: data is null || onGround ? null : data.VerticalSpeedFpm,
            HoldingPointDistanceNm: Math.Max(0.05, _options.CurrentValue.CommsHandover.HoldingPointDistanceNm),
            // The crew's names for the procedures (2026-10-29) — flight.json names no airport,
            // so the OFP navlog tier answers and the DFD tier only when the ident is unique.
            // The airport pins the fix-name lookup to its region (issue #202: without it the
            // DFD named the EHAM STAR REDF1A "Redfern Railway Station" from an Australian row).
            SidSpoken: flight.Sid is { Length: > 0 } ? _spokenText?.Procedure(flight.Sid, _ofp?.Current?.OriginIcao) : null,
            StarSpoken: flight.Star is { Length: > 0 } ? _spokenText?.Procedure(flight.Star, _ofp?.Current?.DestinationIcao) : null);
    }

    /// <summary>The ATIS letter we hold for the airport ATC is asking about: the destination's
    /// card once airborne, the local card on the ground. Null when no card has one.</summary>
    private string? OurAtisLetter()
    {
        if (_weather is null)
        {
            return null;
        }

        var snapshot = _weather.Snapshot();
        var airborne = _flightData?.Sample()?.OnGround == false;
        var card = airborne && snapshot.Second.Role == Core.Weather.WeatherCardRole.Destination ? snapshot.Second : snapshot.Local;
        return card.Status == Core.Weather.WxProbeStatus.Found ? card.Facts.AtisLetter : null;
    }
}
