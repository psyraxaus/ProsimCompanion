using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Radios;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.SayIntentions;

/// <summary>
/// What the FO does when the wrong-frequency report fires (owner request 2026-11-16). The old
/// report was a statement, said once after the wait, with no offer: "Captain, we should be on
/// Ground, …". With <c>sayIntentions.offerToTune</c> on it is a captain's question —
/// "Captain, COM1 is on …. Ground is …. Shall I tune it?" — voiced by the shared dialogue and
/// shown on the open-questions card. Yes tunes COM1 through <see cref="IRadioTuner"/> (the R_
/// datarefs: standby, verify, swap) and the FO reports the outcome; no, or no answer within
/// the question's minute, changes nothing.
/// <para>One question at a time, and one question per assignment and COM1 frequency
/// (<see cref="TuneOfferMemory"/>): the wait and the ATC push-to-talk can both fire for the
/// same pair, and a declined pair is not asked about again.</para>
/// <para>The statement is kept as it was when the option is off, when no tuner is composed in,
/// and when FO voice dialogues are off (the question would then be a silent web card). The FO
/// working the radios (<c>foRadios.enabled</c>) changes nothing here since 2026-11-16: the
/// owner flies that way and wants the question there too.</para>
/// </summary>
public sealed class Com1TuneOfferService : IDisposable
{
    /// <summary>The existing report event (2026-10-05), now with trigger / atGate / offered.</summary>
    public const string AdvisoryEventName = "fo.wrong-frequency-advisory";

    /// <summary>The offer's outcome: accepted + tune result, declined, or timeout.</summary>
    public const string OfferEventName = "fo.wrong-frequency-offer";

    /// <summary>The <see cref="IRadioTuner"/> reason of a tune the captain said yes to.</summary>
    public const string TuneReason = "fo-offer";

    private readonly IOptionsMonitor<SayIntentionsOptions> _options;
    private readonly IOptionsMonitor<GsxOptions> _gsxOptions;
    private readonly Com1TuneOfferStore _store;
    private readonly GroundOpsSignals _signals;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<Com1TuneOfferService> _logger;
    private readonly IRadioTuner? _tuner;
    private readonly TuneOfferMemory _memory = new();
    private readonly object _gate = new();
    private (WrongFrequencyReport Report, string Subject)? _pending;

    public Com1TuneOfferService(
        IOptionsMonitor<SayIntentionsOptions> options,
        IOptionsMonitor<GsxOptions> gsxOptions,
        Com1TuneOfferStore store,
        GroundOpsSignals signals,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        ILogger<Com1TuneOfferService> logger,
        IRadioTuner? tuner = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(gsxOptions);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _gsxOptions = gsxOptions;
        _store = store;
        _signals = signals;
        _arbiter = arbiter;
        _eventLog = eventLog;
        _logger = logger;
        _tuner = tuner;
        _store.Answered += OnAnswered;
        _signals.FlightCycleReset += OnFlightCycleReset;
    }

    public void Dispose()
    {
        _store.Answered -= OnAnswered;
        _signals.FlightCycleReset -= OnFlightCycleReset;
    }

    /// <summary>True while a report is asked as a question; false = the statement.</summary>
    internal bool OffersTune
    {
        get
        {
            var options = _options.CurrentValue;
            return options.OfferToTune && _tuner is not null
                && _gsxOptions.CurrentValue.VoiceControlEnabled;
        }
    }

    /// <summary>The report fired. Returns what was done, for the log and the tests:
    /// <c>question</c>, <c>statement</c> or <c>none</c>.</summary>
    internal string Report(WrongFrequencyReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!OffersTune)
        {
            // The push-to-talk trigger exists for the offer alone: without it the statement
            // keeps its wait, as before.
            if (report.Trigger != WrongFrequencyReport.WaitTrigger)
            {
                return "none";
            }

            Statement(report);
            return "statement";
        }

        var offer = new Com1TuneOffer(
            report.Assigned.Khz, report.Com1Khz, SayIntentionsFrequencyMonitor.SpeakableStation(report.Assigned.Station),
            SayIntentionsFrequencyMonitor.SpeakableStation(report.Com1Station ?? ""));
        lock (_gate)
        {
            if (_store.Asking.QuestionStands || !_memory.TryClaim(offer.AssignedKhz, offer.Com1Khz))
            {
                return "none";
            }

            _pending = (report, offer.Subject);
        }

        if (!_store.Ask(Com1TuneOfferPhrases.CardPrompt(offer), offer.Subject))
        {
            lock (_gate)
            {
                _memory.Release(offer.AssignedKhz, offer.Com1Khz);
                _pending = null;
            }

            return "none";
        }

        var text = Com1TuneOfferPhrases.SpokenQuestion(offer);
        _logger.LogInformation(
            "Wrong-frequency question ({Trigger}, at gate {AtGate}): COM1 on {Com1Khz} kHz, assigned {AssignedKhz} kHz ({Station})",
            report.Trigger, report.AtGate, report.Com1Khz, report.Assigned.Khz, report.Assigned.Station);
        RecordAdvisory(report, text, offered: true);
        return "question";
    }

    private void OnFlightCycleReset(FlightCycleResetKind _)
    {
        lock (_gate)
        {
            _memory.Reset();
            _pending = null;
        }
    }

    private void OnAnswered(object? sender, (bool Accepted, string Source) answer)
    {
        (WrongFrequencyReport Report, string Subject)? pending;
        lock (_gate)
        {
            pending = _pending;
            // An accept with no question standing (a stray command) carries no subject — it is
            // never a yes to an offer that is gone.
            if (pending is null || !string.Equals(_store.Asking.Answer?.Subject, pending.Value.Subject, StringComparison.Ordinal))
            {
                return;
            }

            _pending = null;
        }

        var report = pending.Value.Report;
        if (!answer.Accepted)
        {
            var outcome = answer.Source == "timeout" ? "timeout" : "declined";
            _logger.LogInformation("Wrong-frequency question: {Outcome} ({Source}) — COM1 left alone", outcome, answer.Source);
            RecordOffer(report, accepted: false, outcome, answer.Source);
            return;
        }

        _ = TuneAsync(report, answer.Source);
    }

    /// <summary>The yes: tune, then one line — "Ground tuned." or the plain unable.</summary>
    internal async Task TuneAsync(WrongFrequencyReport report, string source)
    {
        ArgumentNullException.ThrowIfNull(report);
        var outcome = RadioTuneOutcome.NotConnected;
        try
        {
            if (_tuner is not null)
            {
                outcome = await _tuner.TuneCom1Async(report.Assigned.Khz, TuneReason).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Wrong-frequency question: COM1 tune to {Khz} kHz failed", report.Assigned.Khz);
            outcome = RadioTuneOutcome.Failed;
        }

        // A yes ends the matter for this pair: if the captain goes back to the same wrong
        // frequency later, that is a new mistake and the FO asks again. Only a no (or silence)
        // keeps the pair quiet. Owner's rig 2026-11-16 01:16Z: after "yes" and "Ground tuned."
        // the pilot tuned 118.000 again and keyed ATC, and the FO said nothing.
        lock (_gate)
        {
            _memory.Reset();
        }

        var said = Com1TuneOfferPhrases.TuneResult(report.Assigned.Station, outcome);
        _logger.LogInformation(
            "Wrong-frequency question: accepted ({Source}) — COM1 tune to {Khz} kHz -> {Outcome}", source, report.Assigned.Khz, outcome);
        RecordOffer(report, accepted: true, outcome.ToString(), source);
        _ = _arbiter.EnqueueAsync(new SpeechRequest(
            said, SpeechPriority.Normal, Ttl: TimeSpan.FromSeconds(30), Tag: "fo.wrong-frequency"));
    }

    private void Statement(WrongFrequencyReport report)
    {
        var text = SayIntentionsFrequencyMonitor.ComposeAdvisory(report.Assigned);
        _logger.LogInformation(
            "Wrong-frequency report: COM1 on {Com1Khz} kHz, assigned {AssignedKhz} kHz ({Station}) for {WaitedSec} s",
            report.Com1Khz, report.Assigned.Khz, report.Assigned.Station, (int)report.Waited.TotalSeconds);
        RecordAdvisory(report, text, offered: false);

        // Advisory band with a shelf life: said late, behind a long ATC exchange, the radio
        // has usually moved on and the line would be wrong.
        _ = _arbiter.EnqueueAsync(new SpeechRequest(
            text, SpeechPriority.Normal, Ttl: TimeSpan.FromSeconds(30), Tag: "fo.wrong-frequency"));
    }

    private void RecordAdvisory(WrongFrequencyReport report, string text, bool offered)
        => _eventLog.Record(AdvisoryEventName, new
        {
            text,
            station = report.Assigned.Station,
            assignedKhz = report.Assigned.Khz,
            com1Khz = report.Com1Khz,
            com1Station = report.Com1Station,
            source = report.Assigned.Source.ToString(),
            waitedSec = (int)report.Waited.TotalSeconds,
            trigger = report.Trigger,
            atGate = report.AtGate,
            offered,
        });

    private void RecordOffer(WrongFrequencyReport report, bool accepted, string outcome, string answeredBy)
        => _eventLog.Record(OfferEventName, new
        {
            assignedKhz = report.Assigned.Khz,
            com1Khz = report.Com1Khz,
            com1Station = report.Com1Station,
            station = report.Assigned.Station,
            trigger = report.Trigger,
            atGate = report.AtGate,
            accepted,
            outcome,
            answeredBy,
        });
}

/// <summary>
/// The words of the FO's "Shall I tune it?" (<see cref="Com1TuneOfferStore"/>): the phrase table
/// the shared captain's-question dialogue voices, and the lines around it. Cockpit talk — the
/// frequency is read digit by digit with "decimal" in every radio phraseology style.
/// </summary>
public static class Com1TuneOfferPhrases
{
    private static readonly IReadOnlyList<string> Grammar =
    [
        .. ConfirmVocabulary.Affirm, .. ConfirmVocabulary.Negative, .. ConfirmVocabulary.SayAgain,
        "tune it", "go ahead", "please do", "leave it", "i'll do it", "i will do it",
    ];

    public static Gsx.CaptainsQuestionPhrases Table { get; } = new(
        Topic: Com1TuneOfferStore.QuestionDefinition.Topic,
        LogName: "Wrong-frequency question",
        MicOwner: "com1TuneOffer",
        EventName: "fo.wrong-frequency-question",
        Grammar: _ => Grammar,
        SpokenPrompt: question => Com1TuneOffer.Parse(question.Subject) is { } offer ? SpokenQuestion(offer) : question.Prompt,
        Interpret: (heard, _) => Interpret(heard) switch
        {
            true => Gsx.QuestionReply.Yes,
            false => Gsx.QuestionReply.No,
            null => null,
        },
        UnclearPrompt: _ => "Say again — shall I tune it, yes or no?",
        // Nobody answered: the FO says nothing more (the card stays until the minute is up).
        UnansweredPhrase: (_, _) => "",
        RaisedPayload: question => new { subject = question.Subject, prompt = question.Prompt },
        AnsweredPayload: (question, reply, heard) => new { subject = question.Subject, outcome = reply.Kind == Gsx.QuestionReplyKind.Yes ? "accepted" : "declined", heard },
        UnansweredPayload: (question, _) => new { subject = question.Subject, outcome = "unanswered" });

    /// <summary>"Captain, COM1 is on one two four decimal eight five. Ground is one two one
    /// decimal seven zero five. Shall I tune it?" — only the assigned half when COM1 is not
    /// read; with the station COM1 is on known: "Captain, COM1 is on Scottish Control, one one
    /// niner decimal zero. We should be on Ground, …. Shall I tune it?"</summary>
    public static string SpokenQuestion(Com1TuneOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);
        var assigned = SayIntentionsFrequencyMonitor.SpokenFrequency(offer.AssignedKhz);
        if (offer.Com1Khz <= 0)
        {
            return offer.Station.Length > 0
                ? $"Captain, {offer.Station} is {assigned}. Shall I tune it?"
                : $"Captain, we should be on {assigned}. Shall I tune it?";
        }

        var com1 = SayIntentionsFrequencyMonitor.SpokenFrequency(offer.Com1Khz);
        if (offer.Com1Station.Length > 0)
        {
            // Both stations named: "COM1 is on Scottish Control, …. We should be on Ground, …."
            var shouldBe = offer.Station.Length > 0 ? $"We should be on {offer.Station}, {assigned}." : $"We should be on {assigned}.";
            return $"Captain, COM one is on {offer.Com1Station}, {com1}. {shouldBe} Shall I tune it?";
        }

        var expected = offer.Station.Length > 0 ? $"{offer.Station} is {assigned}." : $"We should be on {assigned}.";
        return $"Captain, COM one is on {com1}. {expected} Shall I tune it?";
    }

    /// <summary>The same question for the web card, with the frequencies as figures.</summary>
    public static string CardPrompt(Com1TuneOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);
        var assigned = RadioFrequency.Format(offer.AssignedKhz);
        if (offer.Com1Khz > 0 && offer.Com1Station.Length > 0)
        {
            var shouldBe = offer.Station.Length > 0 ? $"We should be on {offer.Station}, {assigned}." : $"We should be on {assigned}.";
            return $"COM1 is on {offer.Com1Station}, {RadioFrequency.Format(offer.Com1Khz)}. {shouldBe} Shall I tune it?";
        }

        var expected = offer.Station.Length > 0 ? $"{offer.Station} is {assigned}." : $"We should be on {assigned}.";
        return offer.Com1Khz > 0
            ? $"COM1 is on {RadioFrequency.Format(offer.Com1Khz)}. {expected} Shall I tune it?"
            : $"{expected} Shall I tune it?";
    }

    /// <summary>"Ground tuned." on <see cref="RadioTuneOutcome.Tuned"/>; one plain line otherwise.</summary>
    public static string TuneResult(string station, RadioTuneOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(station);
        if (outcome != RadioTuneOutcome.Tuned)
        {
            return "Unable to tune COM one, Captain. The radio is yours.";
        }

        var spoken = SayIntentionsFrequencyMonitor.SpeakableStation(station);
        return spoken.Length > 0 ? $"{spoken} tuned." : "COM one tuned.";
    }

    /// <summary>True = tune, false = leave, null = not an answer. Pure, for tests.</summary>
    public static bool? Interpret(string heard)
    {
        ArgumentNullException.ThrowIfNull(heard);
        var text = CommandMatcher.Normalize(heard);
        if (text.Contains("leave it", StringComparison.Ordinal) || text.Contains("i ll do it", StringComparison.Ordinal)
            || text.Contains("i will do it", StringComparison.Ordinal)
            || text.Contains("don t", StringComparison.Ordinal) || text.Contains("do not", StringComparison.Ordinal))
        {
            return false;
        }

        if (text.Contains("tune it", StringComparison.Ordinal) || text.Contains("go ahead", StringComparison.Ordinal)
            || text.Contains("please do", StringComparison.Ordinal))
        {
            return true;
        }

        if (ConfirmVocabulary.Negative.Any(n => text == n || text.StartsWith(n + " ", StringComparison.Ordinal)))
        {
            return false;
        }

        if (ConfirmVocabulary.Affirm.Any(a => text == a || text.StartsWith(a + " ", StringComparison.Ordinal)))
        {
            return true;
        }

        return null;
    }
}
