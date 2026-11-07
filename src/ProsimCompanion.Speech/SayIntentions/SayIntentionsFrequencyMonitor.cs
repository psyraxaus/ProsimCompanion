using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Speech.Arbiter;

namespace ProsimCompanion.Speech.SayIntentions;

/// <summary>
/// The FO's wrong-frequency report (owner request 2026-10-05): SayIntentions itself never
/// objects when COM1 is on the wrong station — it lets the pilot (or its own copilot) check
/// in with whoever answers — so the FO says once, "Captain, we should be on Tallinn Control,
/// one three four decimal three two five", when ProSim's COM1 active has stayed off the
/// frequency ATC last assigned for the configured wait.
/// <para>The assignment comes from <see cref="AssignedFrequencyTracker"/> (the comms
/// history's last "Contact … on …"; <c>correct_frequency</c> before the first hand-off) and
/// the timing from <see cref="FrequencyWatchCore"/>; this class only polls, reads COM1 and
/// speaks. Everything fails silent: no API key, no fresh history, an unreadable hand-off,
/// ProSim absent or stale — no report.</para>
/// <para>Quiet at the gate by design: SI names Ground as the correct station from the moment
/// the flight is filed, long before the crew has any reason to be on it.</para>
/// </summary>
public sealed class SayIntentionsFrequencyMonitor : Core.Hosting.IStartupModule, IDisposable
{
    /// <summary>One ATC → pilot line on COM1 (English text when SayIntentions gives it, else
    /// the spoken form) with the hand-off it carries, if any.</summary>
    internal sealed record AtcTransmission(long Id, string Text, AssignedFrequency? Handoff, string? Station = null);

    private const string ApiBase = "https://apipri.sayintentions.ai/sapi";
    private const int TickMs = 5000;

    /// <summary>SAPI has no hard rate limit but asks callers not to hammer it; a hand-off
    /// noticed 20 s late is well inside the report's own wait.</summary>
    private static readonly TimeSpan HistoryPollInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CorrectFrequencyPollInterval = TimeSpan.FromSeconds(30);

    /// <summary>With the FO on the radios the history is polled on the falling edge of
    /// <c>L:SIAI_COM1_RECEIVING</c> (<see cref="Com1ReceivingEdge"/>, 2026-11-07) and the clock
    /// poll drops to this safety interval — while edges keep coming. No edge for
    /// <see cref="EdgeHealth"/> (MSFS down, SayIntentions not keying the L:var) and the clock
    /// poll is back at <c>foRadios.pollSeconds</c>.</summary>
    internal static readonly TimeSpan EdgeSafetyInterval = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan EdgeHealth = TimeSpan.FromMinutes(5);

    /// <summary>With no successful history poll for this long the assignment may be stale (a
    /// hand-off we never saw) — treated as unknown.</summary>
    private static readonly TimeSpan HistoryFreshness = TimeSpan.FromSeconds(90);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private int _historyFailures;
    private int _tickFailures;

    private readonly IOptionsMonitor<SayIntentionsOptions> _options;
    private readonly IProsimDataRefs _dataRefs;
    private readonly IFlightPhaseSource _flight;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<SayIntentionsFrequencyMonitor> _logger;
    private readonly AssignedFrequencyTracker _tracker = new();
    private readonly FrequencyWatchCore _watch = new();
    private readonly CancellationTokenSource _shutdown = new();

    private IDataRefSubscription<double>? _com1Active;
    private Timer? _timer;
    private int _ticking;

    // Edge-triggered history poll (2026-11-07): optional so the monitor builds without a sim.
    private readonly ISimVars? _simVars;
    private readonly Com1ReceivingEdge _edge = new();
    private IDataRefSubscription<double>? _com1Receiving;
    private int _pollNow;
    private DateTimeOffset _lastEdgeUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastHistoryPollUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastHistoryOkUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastCorrectPollUtc = DateTimeOffset.MinValue;
    private AssignedFrequency? _announcedAssignment;
    private bool _historySeeded;

    // Radios tile (owner request 2026-10-25): optional so the monitor still builds without it.
    private readonly Core.Radios.RadioStore? _radioStore;
    private readonly Core.Aircraft.Ofp.OfpStore? _ofp;
    private static readonly TimeSpan StationsRefresh = TimeSpan.FromMinutes(15);
    private bool _voiceWanted;
    private string _stationsIcao = "";
    private DateTimeOffset _stationsFetchedUtc = DateTimeOffset.MinValue;
    private long _lastAtcLineId;

    /// <summary>One ATC → pilot transmission on COM1 from the comms history, with its hand-off
    /// when the text carries one. Raised on the monitor's tick thread once per entry as its
    /// ATC text arrives (a record created for the pilot's call and filled in by ATC a few
    /// seconds later is raised when filled, 2026-11-07) — never for the backlog of the first
    /// poll or of a new SayIntentions flight. The comms hand-over (issue #171) reads taxi
    /// clearances and announces hand-offs from it.</summary>
    internal event Action<AtcTransmission>? AtcTransmissionObserved;

    /// <summary>The frequency ATC last assigned, as the tracker knows it (null = unknown).</summary>
    internal AssignedFrequency? CurrentAssignment => _tracker.Current;

    public SayIntentionsFrequencyMonitor(
        IOptionsMonitor<SayIntentionsOptions> options,
        IProsimDataRefs dataRefs,
        IFlightPhaseSource flight,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        ILogger<SayIntentionsFrequencyMonitor> logger,
        Core.Radios.RadioStore? radioStore = null,
        Core.Aircraft.Ofp.OfpStore? ofp = null,
        ISimVars? simVars = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _dataRefs = dataRefs;
        _flight = flight;
        _arbiter = arbiter;
        _eventLog = eventLog;
        _logger = logger;
        _radioStore = radioStore;
        _ofp = ofp;
        _simVars = simVars;
    }

    public void Start()
    {
        _com1Active = _dataRefs.Subscribe(ProsimDataRefNames.RadioCom1Active);
        _timer = new Timer(_ => _ = TickAsync(), null, TickMs, TickMs);
        if (_simVars is null)
        {
            return;
        }

        try
        {
            // The same L:var the radio-clear gate reads; the sim creates an unknown L:var as 0,
            // so without SayIntentions it never moves and the clock poll carries on alone.
            _com1Receiving = _simVars.Subscribe(SayIntentionsLvarNames.Com1Receiving);
            _com1Receiving.ValueChanged += OnCom1ReceivingChanged;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SayIntentions COM1_RECEIVING edge unavailable — history polls stay on the clock");
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _shutdown.Cancel();
        _com1Active?.Dispose();
        if (_com1Receiving is not null)
        {
            _com1Receiving.ValueChanged -= OnCom1ReceivingChanged;
            _com1Receiving.Dispose();
        }
    }

    /// <summary>ATC stopped talking on COM1: its line is in the comms history by now — poll
    /// at once instead of waiting for the clock (the FO answers as ATC finishes, and the poll
    /// never lands in the seconds before the reply's text reaches the record).</summary>
    private void OnCom1ReceivingChanged(object? sender, EventArgs e)
    {
        var sub = _com1Receiving;
        if (sub is null)
        {
            return;
        }

        if (sub.IsStale)
        {
            _edge.Reset();
            return;
        }

        if (!_edge.Step(sub.Value))
        {
            return;
        }

        _lastEdgeUtc = DateTimeOffset.UtcNow;
        Interlocked.Exchange(ref _pollNow, 1);
        _ = TickAsync();
    }

    /// <summary>Pushback to taxi-in: the window in which the crew is working ATC. At the
    /// gate SI already names Ground while the crew is still on ATIS or delivery.</summary>
    internal static bool IsWatchedPhase(FlightPhase phase)
        => !phase.IsAtGate() && phase is not (FlightPhase.Unknown or FlightPhase.Shutdown);

    /// <summary>The single spoken line. The station is cleaned for speech and the frequency
    /// read digit by digit. Exposed for tests.</summary>
    internal static string ComposeAdvisory(AssignedFrequency assigned)
    {
        ArgumentNullException.ThrowIfNull(assigned);

        var station = SpeakableStation(assigned.Station);
        var frequency = Callouts.Aviation.ToDigits(
            (assigned.Khz / 1000.0).ToString("F3", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.'));
        return station.Length > 0
            ? $"Captain, we should be on {station}, {frequency}."
            : $"Captain, we should be on {frequency}.";
    }

    /// <summary>Letters, digits and spaces only; an all-caps position code from
    /// <c>correct_position</c> ("GROUND") is spoken as a word ("Ground").</summary>
    internal static string SpeakableStation(string station)
    {
        var cleaned = string.Join(' ', new string(
            [.. station.Select(c => char.IsLetterOrDigit(c) ? c : ' ')])
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return cleaned.Length > 1 && cleaned.All(c => !char.IsLetter(c) || char.IsUpper(c))
            ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(cleaned.ToLowerInvariant())
            : cleaned;
    }

    private async Task TickAsync()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            var options = _options.CurrentValue;
            var apiKey = options.Enabled ? ResolveSapiCredential(options) : null;
            if (apiKey is null)
            {
                _watch.Reset();
                _radioStore?.Update(s => s.SayIntentionsOn || s.Assigned is not null
                    ? s with { SayIntentionsOn = false, Assigned = null }
                    : s);
                return;
            }

            _radioStore?.Update(s => s.SayIntentionsOn ? s : s with { SayIntentionsOn = true });

            // Flight-live gate (issue #114): ProSim pushes plausible data with no MSFS session,
            // so the SPOKEN features run only pushback→taxi-in on a live flight. The Radios
            // tile (owner request 2026-10-25) reads the same polls at the gate and without a
            // session — that is exactly where the pilot wants to see which frequency ATC is on.
            // The history poll also serves the comms hand-over (issue #171), so it runs while
            // either voice feature is on; the advisory itself stays behind its own switch.
            var watched = _flight.IsLive && IsWatchedPhase(_flight.CurrentPhase);
            var historyWanted = options.FrequencyMonitorEnabled || options.CommsHandover.Enabled;
            // The FO working the radios (owner request 2026-10-25) needs every ATC line, fast,
            // at the gate too; the hand-over readouts stay behind the flight-live gate.
            var foRadios = options.FoRadios.Enabled;
            _voiceWanted = (watched && historyWanted) || foRadios;
            if (!_voiceWanted && _radioStore is null)
            {
                _watch.Reset();
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var edgePoll = Interlocked.Exchange(ref _pollNow, 0) == 1;
            var historyInterval = HistoryInterval(foRadios, options.FoRadios.PollSeconds, now - _lastEdgeUtc);
            if (edgePoll || now - _lastHistoryPollUtc >= historyInterval)
            {
                _lastHistoryPollUtc = now;
                await PollHistoryAsync(apiKey, edgePoll ? "edge" : "clock", _shutdown.Token).ConfigureAwait(false);
            }

            if (_tracker.NeedsCorrectFrequency && now - _lastCorrectPollUtc >= CorrectFrequencyPollInterval)
            {
                _lastCorrectPollUtc = now;
                await PollCorrectFrequencyAsync(apiKey, _shutdown.Token).ConfigureAwait(false);
            }

            if (_radioStore is not null)
            {
                await PollStationsIfDueAsync(apiKey, now, _shutdown.Token).ConfigureAwait(false);
            }

            now = DateTimeOffset.UtcNow;
            var assigned = now - _lastHistoryOkUtc <= HistoryFreshness ? _tracker.Current : null;
            NoteAssignment(assigned);
            PublishAssignment(assigned, now);

            if (!_voiceWanted || !options.FrequencyMonitorEnabled)
            {
                _watch.Reset();
                return;
            }

            // A stale subscription means ProSim is gone — never judge a held-over frequency.
            var com1Khz = _com1Active is { IsStale: false, RawValue: not null } com1
                ? (int)Math.Round(com1.Value)
                : 0;
            var wait = TimeSpan.FromSeconds(Math.Max(10, options.FrequencyMonitorWaitSeconds));
            if (_watch.Step(now, assigned, com1Khz, wait) is { } advisory)
            {
                Speak(advisory);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (_tickFailures++ % 20 == 0)
            {
                _logger.LogWarning(ex, "SayIntentions frequency watch tick failed ({Count} so far)", _tickFailures);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    internal static string? ResolveSapiCredential(SayIntentionsOptions options)
        => options.ApiKeySource.Equals("manual", StringComparison.OrdinalIgnoreCase)
            ? (string.IsNullOrWhiteSpace(options.ManualApiKey) ? null : options.ManualApiKey)
            : FlightJsonFile.TryReadApiKey();

    /// <summary>The airport whose station list the Radios tile shows: the OFP origin until
    /// the descent, the destination from then on (the hero weather cards' rule).</summary>
    internal static string LocalIcao(FlightPhase phase, string? origin, string? destination)
    {
        var orig = origin?.Trim().ToUpperInvariant() ?? "";
        var dest = destination?.Trim().ToUpperInvariant() ?? "";
        var arriving = phase is FlightPhase.Descent or FlightPhase.Approach
            or FlightPhase.LandingRollout or FlightPhase.TaxiIn or FlightPhase.Shutdown;
        return arriving ? (dest.Length > 0 ? dest : orig) : (orig.Length > 0 ? orig : dest);
    }

    /// <summary>getWX with_comms=1 for the local airport — on an airport change and every
    /// 15 min; the stamp is set before the call so a failing airport is not asked every tick.</summary>
    private async Task PollStationsIfDueAsync(string apiKey, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var ofp = _ofp?.Current;
        var icao = LocalIcao(_flight.CurrentPhase, ofp?.OriginIcao, ofp?.DestinationIcao);
        if (icao.Length == 0 || (icao == _stationsIcao && now - _stationsFetchedUtc < StationsRefresh))
        {
            return;
        }

        _stationsIcao = icao;
        _stationsFetchedUtc = now;
        try
        {
            using var response = await Http.GetAsync(
                $"{ApiBase}/getWX?api_key={Uri.EscapeDataString(apiKey)}&icao={Uri.EscapeDataString(icao)}&with_comms=1",
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("SayIntentions getWX (comms) HTTP {Status} for {Icao}", (int)response.StatusCode, icao);
                return;
            }

            var stations = SayIntentionsComms.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            _logger.LogDebug("SayIntentions station list for {Icao}: {Count} station(s)", icao, stations.Count);
            var fetched = DateTimeOffset.UtcNow;
            _radioStore?.Update(s =>
            {
                // Keep at most the two OFP airports: the other one stays, this one is replaced.
                var kept = s.Airports.Where(a => !a.Icao.Equals(icao, StringComparison.OrdinalIgnoreCase)).TakeLast(1);
                return s with { Airports = [.. kept, new Core.Radios.AirportStations(icao, stations, fetched)] };
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SayIntentions getWX (comms) failed for {Icao}", icao);
        }
    }

    /// <summary>The Radios tile's "ATC expects" line; the since-stamp survives polls that repeat
    /// the same assignment.</summary>
    private void PublishAssignment(AssignedFrequency? assigned, DateTimeOffset now)
    {
        _radioStore?.Update(s =>
        {
            if (assigned is null)
            {
                return s.Assigned is null ? s : s with { Assigned = null };
            }

            if (s.Assigned is { } current && current.Khz == assigned.Khz
                && string.Equals(current.Station, assigned.Station, StringComparison.Ordinal))
            {
                return s;
            }

            var source = assigned.Source == AssignedFrequencySource.Handoff
                ? Core.Radios.RadioAssignmentSource.Handoff
                : Core.Radios.RadioAssignmentSource.CorrectFrequency;
            return s with { Assigned = new Core.Radios.RadioAssignment(assigned.Station, assigned.Khz, source, now) };
        });
    }

    /// <summary>The clock interval between history polls: 20 s off the FO-radios switch; on it
    /// the configured seconds, relaxed to <see cref="EdgeSafetyInterval"/> while the
    /// COM1_RECEIVING edge is delivering polls of its own. Pure for tests.</summary>
    internal static TimeSpan HistoryInterval(bool foRadios, int pollSeconds, TimeSpan sinceLastEdge)
    {
        if (!foRadios)
        {
            return HistoryPollInterval;
        }

        var configured = TimeSpan.FromSeconds(Math.Clamp(pollSeconds, 3, 20));
        return sinceLastEdge <= EdgeHealth && configured < EdgeSafetyInterval ? EdgeSafetyInterval : configured;
    }

    private async Task PollHistoryAsync(string apiKey, string trigger, CancellationToken cancellationToken)
    {
        try
        {
            var sinceId = _tracker.LastId;
            var since = sinceId > 0
                ? "&since_id=" + sinceId.ToString(CultureInfo.InvariantCulture)
                : "";
            using var response = await Http.GetAsync(
                $"{ApiBase}/getCommsHistory?api_key={Uri.EscapeDataString(apiKey)}{since}",
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                NoteHistoryFailure("HTTP " + (int)response.StatusCode);
                return;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var (flightId, entries) = CommHistory.Parse(body);
            var fresh = _tracker.ApplyHistory(flightId, entries);
            _lastHistoryOkUtc = DateTimeOffset.UtcNow;
            if (_historyFailures > 0)
            {
                _logger.LogInformation("SayIntentions getCommsHistory is back after {Count} failed poll(s)", _historyFailures);
                _historyFailures = 0;
            }

            // A good poll used to leave no trace at all (EGLL 2026-11-07: two lost clearances and
            // nothing to read) — every poll is a Debug line, every eventful one a session event.
            _logger.LogDebug(
                "SayIntentions getCommsHistory ({Trigger}): {Count} entr(ies) since #{Since}, marker #{Marker}, pending #{Pending}, {Fresh} new ATC line(s)",
                trigger, entries.Count, sinceId, _tracker.LastId, _tracker.PendingId ?? 0, fresh.Count);
            if (trigger == "edge" || entries.Count > 0)
            {
                _eventLog.Record("sayintentions.history-poll", new
                {
                    trigger,
                    entries = entries.Count,
                    sinceId,
                    marker = _tracker.LastId,
                    pendingId = _tracker.PendingId,
                    newLines = fresh.Count,
                    seeded = _historySeeded,
                });
            }

            PublishLastAtcLine(entries);

            // The first poll of a session returns the flight's whole backlog — seed only.
            // A new SayIntentions flight resets the tracker, which re-seeds.
            if (_voiceWanted && _historySeeded && !_tracker.LastApplyReset && fresh.Count > 0)
            {
                RaiseTransmissions(fresh);
            }

            _historySeeded = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            NoteHistoryFailure(ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>A failing history poll is a silent FO (EHAM 2026-11-01: twelve minutes of ATC
    /// unanswered with nothing in the log): the first failure and every twentieth are warnings.</summary>
    private void NoteHistoryFailure(string detail)
    {
        if (_historyFailures++ % 20 == 0)
        {
            _logger.LogWarning("SayIntentions getCommsHistory failed: {Detail} ({Count} in a row)", detail, _historyFailures);
        }
    }

    /// <summary>The newest ATC → pilot COM1 line for the Radios tile (the seed backlog included).</summary>
    private void PublishLastAtcLine(IReadOnlyList<CommEntry> entries)
    {
        if (_radioStore is null)
        {
            return;
        }

        CommEntry? last = null;
        foreach (var entry in entries)
        {
            if (entry.Channel.Equals("COM1", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(entry.OutgoingEnglish ?? entry.Outgoing)
                && (last is null || entry.Id > last.Id))
            {
                last = entry;
            }
        }

        if (last is null || last.Id == _lastAtcLineId)
        {
            return;
        }

        _lastAtcLineId = last.Id;
        var text = (string.IsNullOrWhiteSpace(last.OutgoingEnglish) ? last.Outgoing : last.OutgoingEnglish)!.Trim();
        var seen = DateTimeOffset.UtcNow;
        _radioStore.Update(s => s with { LastAtcLine = text, LastAtcAtUtc = seen });
    }

    /// <summary>Hands every new COM1 ATC transmission (the tracker's fresh list, in id order)
    /// to <see cref="AtcTransmissionObserved"/>; a handler fault never stops the poll.</summary>
    private void RaiseTransmissions(IReadOnlyList<CommEntry> fresh)
    {
        var handler = AtcTransmissionObserved;
        if (handler is null)
        {
            return;
        }

        foreach (var entry in fresh)
        {
            if (entry.AtcText is not { } text)
            {
                continue; // pilot → ATC, or an empty line (the tracker hands out neither)
            }

            var handoff = HandoffParser.Parse(entry.Outgoing) ?? HandoffParser.Parse(entry.OutgoingEnglish);
            // Every ATC line the FO is handed is on the record (the 2026-11-01 silence had no
            // trail at all): the review reads this against SayIntentions' own transcript.
            _logger.LogInformation("ATC #{Id} ({Station}): \"{Text}\"", entry.Id, entry.StationName ?? "?", text.Trim());
            _eventLog.Record("sayintentions.atc-line", new
            {
                id = entry.Id,
                station = entry.StationName,
                text = text.Trim(),
                handoffKhz = handoff?.Khz,
            });
            try
            {
                handler(new AtcTransmission(entry.Id, text.Trim(), handoff, entry.StationName));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ATC transmission handler failed for #{Id}", entry.Id);
            }
        }
    }

    private async Task PollCorrectFrequencyAsync(string apiKey, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Http.GetAsync(
                $"{ApiBase}/getCurrentFrequencies?api_key={Uri.EscapeDataString(apiKey)}",
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("SayIntentions getCurrentFrequencies HTTP {Status}", (int)response.StatusCode);
                return;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _tracker.ApplyCorrectFrequency(CommHistory.ParseCorrectFrequency(body));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SayIntentions getCurrentFrequencies failed");
        }
    }

    /// <summary>Logs each change of the assignment once — the trail a flight review needs to
    /// judge a report (or its absence) against what ATC actually said.</summary>
    private void NoteAssignment(AssignedFrequency? assigned)
    {
        if (assigned == _announcedAssignment)
        {
            return;
        }

        _announcedAssignment = assigned;
        if (assigned is null)
        {
            _logger.LogInformation("SayIntentions assigned frequency unknown");
            _eventLog.Record("sayintentions.frequency-assigned", new { station = "", khz = 0, source = "unknown" });
            return;
        }

        _logger.LogInformation(
            "SayIntentions assigned frequency {Khz} kHz ({Station}, {Source})",
            assigned.Khz, assigned.Station, assigned.Source);
        _eventLog.Record("sayintentions.frequency-assigned", new
        {
            station = assigned.Station,
            khz = assigned.Khz,
            source = assigned.Source.ToString(),
        });
    }

    private void Speak(WrongFrequencyAdvisory advisory)
    {
        var text = ComposeAdvisory(advisory.Assigned);
        _logger.LogInformation(
            "Wrong-frequency report: COM1 on {Com1Khz} kHz, assigned {AssignedKhz} kHz ({Station}) for {WaitedSec} s",
            advisory.Com1Khz, advisory.Assigned.Khz, advisory.Assigned.Station, (int)advisory.Waited.TotalSeconds);
        _eventLog.Record("fo.wrong-frequency-advisory", new
        {
            text,
            station = advisory.Assigned.Station,
            assignedKhz = advisory.Assigned.Khz,
            com1Khz = advisory.Com1Khz,
            source = advisory.Assigned.Source.ToString(),
            waitedSec = (int)advisory.Waited.TotalSeconds,
        });

        // Advisory band with a shelf life: said late, behind a long ATC exchange, the radio
        // has usually moved on and the line would be wrong.
        _ = _arbiter.EnqueueAsync(new SpeechRequest(
            text, SpeechPriority.Normal, Ttl: TimeSpan.FromSeconds(30), Tag: "fo.wrong-frequency"));
    }
}
