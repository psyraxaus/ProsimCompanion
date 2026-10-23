using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft.Acars;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Company;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Arbiter;

namespace ProsimCompanion.Speech.Company;

/// <summary>
/// The movement messages and the delay code (issue #185). Rides the flight-times store the OOOI
/// tracker already writes: at the OUT edge it judges punctuality against the OFP's STD and —
/// when late beyond the tolerance and <c>company.delayCodes</c> is on — asks the captain for the
/// IATA delay code (a pick-one captain's question over <c>delay-codes.json</c>; the TTL falls to
/// 99 "other"); at the OFF edge it composes the MVT AD message with the EA, at the IN edge the
/// AA message, each uplinked to the cockpit (slots 06 / 07, never the loadsheets' 01 / 02) when
/// ProSim is there and spoken only when <c>company.movementMessagesSpoken</c> asks. Read-only.
/// </summary>
public sealed class MovementMessageService : Core.Hosting.IStartupModule, IDisposable
{
    /// <summary>ACARS slots beside the loadsheets (01/02) and the other November packages (03 LMC, 05 diversion).</summary>
    public const string OffSlot = "06";
    public const string InSlot = "07";

    private readonly IOptionsMonitor<CompanyOptions> _options;
    private readonly FlightTimesStore _times;
    private readonly OfpStore _ofp;
    private readonly FlightProgressStore _progress;
    private readonly DelayCodeStore _delay;
    private readonly DelayCodeCatalog _codes;
    private readonly GroundOpsSignals _signals;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<MovementMessageService> _logger;
    private readonly IAcarsUplink? _acars;
    private readonly ICompanyChannel? _company;
    private readonly object _gate = new();
    private IDisposable? _watch;
    private FlightTimesSnapshot _last = FlightTimesSnapshot.Empty;
    private bool _outJudged;
    private bool _offSent;
    private bool _inSent;
    private bool _questionAsked;

    public MovementMessageService(
        IOptionsMonitor<CompanyOptions> options,
        FlightTimesStore times,
        OfpStore ofp,
        FlightProgressStore progress,
        DelayCodeStore delay,
        DelayCodeCatalog codes,
        GroundOpsSignals signals,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        ILogger<MovementMessageService> logger,
        IAcarsUplink? acars = null,
        ICompanyChannel? company = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(times);
        ArgumentNullException.ThrowIfNull(ofp);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(delay);
        ArgumentNullException.ThrowIfNull(codes);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _times = times;
        _ofp = ofp;
        _progress = progress;
        _delay = delay;
        _codes = codes;
        _signals = signals;
        _arbiter = arbiter;
        _eventLog = eventLog;
        _logger = logger;
        _acars = acars;
        _company = company;
    }

    public void Start()
    {
        LoadCodes();
        _signals.FlightCycleReset += OnFlightCycleReset;
        _delay.Changed += OnDelayStoreChanged;
        _delay.Answered += OnDelayAnswered;
        _watch = _times.Observe(snapshot => _ = OnTimesAsync(snapshot));
    }

    public void Dispose()
    {
        _watch?.Dispose();
        _signals.FlightCycleReset -= OnFlightCycleReset;
        _delay.Changed -= OnDelayStoreChanged;
        _delay.Answered -= OnDelayAnswered;
    }

    private void OnFlightCycleReset()
    {
        lock (_gate)
        {
            _outJudged = false;
            _offSent = false;
            _inSent = false;
            _questionAsked = false;
        }
    }

    /// <summary>One flight-times change. Public for tests: the stamps carry their own clock.</summary>
    public async Task OnTimesAsync(FlightTimesSnapshot times)
    {
        ArgumentNullException.ThrowIfNull(times);
        try
        {
            FlightTimesSnapshot previous;
            lock (_gate)
            {
                previous = _last;
                _last = times;
                if (times == FlightTimesSnapshot.Empty)
                {
                    // A new leg: the tracker cleared the stamps.
                    _outJudged = false;
                    _offSent = false;
                    _inSent = false;
                    _questionAsked = false;
                    return;
                }
            }

            var options = _options.CurrentValue;
            if (!options.Enabled)
            {
                return;
            }

            if (times.OffBlocksUtc is { } outUtc && previous.OffBlocksUtc is null && !_outJudged)
            {
                JudgeDeparture(outUtc, options);
            }

            if (options.MovementMessages && times.TakeoffUtc is { } offUtc && previous.TakeoffUtc is null && !_offSent)
            {
                _offSent = true;
                await SendOffAsync(times.OffBlocksUtc ?? offUtc, offUtc, options).ConfigureAwait(false);
            }

            if (options.MovementMessages && times.OnBlocksUtc is { } inUtc && previous.OnBlocksUtc is null && !_inSent)
            {
                _inSent = true;
                await SendInAsync(times.LandingUtc ?? inUtc, inUtc, options).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Movement message handling failed");
        }
    }

    /// <summary>The OUT edge: punctuality against the STD, the delay-code question when late.</summary>
    private void JudgeDeparture(DateTimeOffset outUtc, CompanyOptions options)
    {
        _outJudged = true;
        var ofp = _ofp.Current;
        var delay = MovementMessageComposer.DelayMinutes(ofp?.ScheduledOutUtc, outUtc);
        var onTime = MovementMessageComposer.OnTime(delay, options.DelayToleranceMinutes);
        _eventLog.Record("flight.punctuality", new
        {
            outUtc,
            scheduledOutUtc = ofp?.ScheduledOutUtc,
            delayMinutes = delay,
            toleranceMinutes = options.DelayToleranceMinutes,
            onTime,
        });
        if (delay is { } late)
        {
            _logger.LogInformation("Off blocks {Out:HH:mm}Z against STD {Std:HH:mm}Z: {Delay} min, {Verdict}",
                outUtc, ofp!.ScheduledOutUtc, late, onTime == true ? "on time" : "late");
        }

        if (!options.DelayCodes || onTime != false || delay is not { } minutes || _questionAsked)
        {
            return;
        }

        _questionAsked = true;
        var choices = _codes.Entries.Select(e => new QuestionChoice($"{e.Code} {e.Title}", e.Code, e.Title)).ToList();
        if (!_delay.Ask(QuestionPrompt(minutes), choices, minutes))
        {
            _logger.LogDebug("Delay-code question not raised — one stands or the cycle is answered");
        }
    }

    /// <summary>"We're 22 minutes late off blocks — delay code?"</summary>
    public static string QuestionPrompt(int delayMinutes)
        => $"We're {delayMinutes} minute{(delayMinutes == 1 ? "" : "s")} late off blocks — delay code?";

    private void OnDelayAnswered(object? sender, (bool Accepted, string Source) answer)
    {
        if (!answer.Accepted || _delay.Snapshot().Asking.Chosen is not { } chosen)
        {
            return;
        }

        RecordCode(chosen.Token, chosen.Detail ?? chosen.Label, answer.Source, note: null);
    }

    /// <summary>The TTL withdrew an unanswered question: code 99 "other", with a note. A cycle
    /// reset also empties the question — but it empties the delay figure too, which tells the
    /// two apart.</summary>
    private void OnDelayStoreChanged()
    {
        var snapshot = _delay.Snapshot();
        if (!_questionAsked || snapshot.DelayMinutes is null || snapshot.Code is not null || snapshot.Asking.QuestionStands || snapshot.Asking.Answer is not null)
        {
            return;
        }

        var other = _codes.Entries.FirstOrDefault(e => e.Code == DelayCodeCatalog.OtherCode);
        RecordCode(DelayCodeCatalog.OtherCode, other?.Title ?? "Other", "timeout", "no answer to the delay-code question");
    }

    private void RecordCode(string code, string title, string source, string? note)
    {
        if (!_delay.Record(code, title, source))
        {
            return;
        }

        var minutes = _delay.Snapshot().DelayMinutes;
        _eventLog.Record("flight.delayCode", new { code, title, delayMinutes = minutes, source, note });
        _logger.LogInformation("Delay code {Code} ({Title}) on record, {Source}{Note}", code, title, source, note is null ? "" : $" — {note}");
        if (source != "timeout")
        {
            _ = _arbiter.SpeakAsync($"Delay code {Digits(code)}, {title}, noted.", SpeechPriority.Normal);
        }
    }

    private async Task SendOffAsync(DateTimeOffset outUtc, DateTimeOffset offUtc, CompanyOptions options)
    {
        var ofp = _ofp.Current;
        var eta = _progress.Snapshot().EtaUtc ?? (ofp?.EstimatedEnroute is { } enroute ? offUtc + enroute : null);
        var delay = _delay.Snapshot();
        var text = MovementMessageComposer.Off(
            FlightNumber(ofp), ofp?.AircraftReg ?? "", ofp?.OriginIcao ?? "", ofp?.DestinationIcao ?? "",
            outUtc, offUtc, eta, delay.Code, delay.DelayMinutes);
        await SendAsync(MovementKind.Off, OffSlot, text, options).ConfigureAwait(false);
    }

    private async Task SendInAsync(DateTimeOffset onUtc, DateTimeOffset inUtc, CompanyOptions options)
    {
        var ofp = _ofp.Current;
        var delay = _delay.Snapshot();
        var text = MovementMessageComposer.In(
            FlightNumber(ofp), ofp?.AircraftReg ?? "", ofp?.DestinationIcao ?? "",
            onUtc, inUtc, delay.Code, delay.DelayMinutes);
        await SendAsync(MovementKind.In, InSlot, text, options).ConfigureAwait(false);
    }

    private async Task SendAsync(MovementKind kind, string slot, string text, CompanyOptions options)
    {
        var sent = false;
        if (_acars is not null)
        {
            sent = await _acars.SendAsync(new AcarsMessage
            {
                Type = "MVT",
                Header = "MOVEMENT",
                Id = slot,
                Accept = false,
                Content = text,
            }).ConfigureAwait(false);
        }

        _eventLog.Record("acars.mvt", new { kind = kind.ToString().ToLowerInvariant(), slot, text, sent, acars = _acars is not null });
        _logger.LogInformation("Movement message {Kind}: {Text} (sent {Sent})", kind, text.Replace('\n', ' '), sent);
        if (options.MovementMessagesSpoken)
        {
            var spoken = MovementMessageComposer.Spoken(kind, sent || _acars is null);
            if (_company is not null)
            {
                _company.DeliverMessage(spoken);
            }
            else
            {
                await _arbiter.EnqueueAsync(new SpeechRequest(spoken, SpeechPriority.Low, Tag: "acars.mvt")).ConfigureAwait(false);
            }
        }
    }

    private void LoadCodes()
    {
        try
        {
            var path = UserConfigPaths.File("delay-codes.json");
            if (File.Exists(path) && !_codes.Load(File.ReadAllText(path)))
            {
                _logger.LogWarning("delay-codes.json has no usable codes — the shipped list stands");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "delay-codes.json load failed — the shipped list stands");
        }
    }

    private static string FlightNumber(OfpData? ofp)
        => ofp is null ? "" : ofp.FlightNumber.Length > 0 ? ofp.FlightNumber : ofp.Callsign;

    private static string Digits(string code) => string.Join(" ", code.Select(c => c.ToString(CultureInfo.InvariantCulture)));
}
