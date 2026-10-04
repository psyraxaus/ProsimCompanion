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
    private const string ApiBase = "https://apipri.sayintentions.ai/sapi";
    private const int TickMs = 5000;

    /// <summary>SAPI has no hard rate limit but asks callers not to hammer it; a hand-off
    /// noticed 20 s late is well inside the report's own wait.</summary>
    private static readonly TimeSpan HistoryPollInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CorrectFrequencyPollInterval = TimeSpan.FromSeconds(30);

    /// <summary>With no successful history poll for this long the assignment may be stale (a
    /// hand-off we never saw) — treated as unknown.</summary>
    private static readonly TimeSpan HistoryFreshness = TimeSpan.FromSeconds(90);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

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
    private DateTimeOffset _lastHistoryPollUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastHistoryOkUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastCorrectPollUtc = DateTimeOffset.MinValue;
    private AssignedFrequency? _announcedAssignment;

    public SayIntentionsFrequencyMonitor(
        IOptionsMonitor<SayIntentionsOptions> options,
        IProsimDataRefs dataRefs,
        IFlightPhaseSource flight,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        ILogger<SayIntentionsFrequencyMonitor> logger)
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
    }

    public void Start()
    {
        _com1Active = _dataRefs.Subscribe(ProsimDataRefNames.RadioCom1Active);
        _timer = new Timer(_ => _ = TickAsync(), null, TickMs, TickMs);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _shutdown.Cancel();
        _com1Active?.Dispose();
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
            // Flight-live gate (issue #114): ProSim pushes plausible data with no MSFS session.
            if (!options.Enabled || !options.FrequencyMonitorEnabled
                || !_flight.IsLive || !IsWatchedPhase(_flight.CurrentPhase))
            {
                _watch.Reset();
                return;
            }

            var apiKey = options.ApiKeySource.Equals("manual", StringComparison.OrdinalIgnoreCase)
                ? (string.IsNullOrWhiteSpace(options.ManualApiKey) ? null : options.ManualApiKey)
                : FlightJsonFile.TryReadApiKey();
            if (apiKey is null)
            {
                _watch.Reset();
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (now - _lastHistoryPollUtc >= HistoryPollInterval)
            {
                _lastHistoryPollUtc = now;
                await PollHistoryAsync(apiKey, _shutdown.Token).ConfigureAwait(false);
            }

            if (_tracker.NeedsCorrectFrequency && now - _lastCorrectPollUtc >= CorrectFrequencyPollInterval)
            {
                _lastCorrectPollUtc = now;
                await PollCorrectFrequencyAsync(apiKey, _shutdown.Token).ConfigureAwait(false);
            }

            now = DateTimeOffset.UtcNow;
            var assigned = now - _lastHistoryOkUtc <= HistoryFreshness ? _tracker.Current : null;
            NoteAssignment(assigned);

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
            _logger.LogDebug(ex, "SayIntentions frequency watch tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    private async Task PollHistoryAsync(string apiKey, CancellationToken cancellationToken)
    {
        try
        {
            var since = _tracker.LastId > 0
                ? "&since_id=" + _tracker.LastId.ToString(CultureInfo.InvariantCulture)
                : "";
            using var response = await Http.GetAsync(
                $"{ApiBase}/getCommsHistory?api_key={Uri.EscapeDataString(apiKey)}{since}",
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("SayIntentions getCommsHistory HTTP {Status}", (int)response.StatusCode);
                return;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var (flightId, entries) = CommHistory.Parse(body);
            _tracker.ApplyHistory(flightId, entries);
            _lastHistoryOkUtc = DateTimeOffset.UtcNow;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SayIntentions getCommsHistory failed");
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
