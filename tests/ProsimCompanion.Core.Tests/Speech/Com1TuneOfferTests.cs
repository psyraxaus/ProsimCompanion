using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Radios;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Gsx;
using ProsimCompanion.Speech.Recognition;
using ProsimCompanion.Speech.SayIntentions;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>
/// The wrong-frequency report at the gate, as a question, and on the push-to-talk (owner flight
/// 2026-11-16, EGLL stand 313: SayIntentions assigned Ground 121.705, the radio was on 124.850,
/// ProSim's R_COM1_ACTIVE read 0, and the FO said nothing).
/// </summary>
public sealed class Com1TuneOfferTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 16, 0, 22, 52, TimeSpan.Zero);
    private static readonly AssignedFrequency Ground = new(121_705, "GROUND", AssignedFrequencySource.CorrectFrequency);
    private static readonly AssignedFrequency Tallinn = new(134_325, "Tallinn Control", AssignedFrequencySource.Handoff);
    private static readonly int[] Egll = [121_935 /* ATIS */, 121_980 /* Delivery */, 121_705 /* Ground */, 118_505 /* Tower */];

    private readonly SayIntentionsOptions _options = new() { Enabled = true };
    private readonly GsxOptions _gsx = new() { VoiceControlEnabled = true };
    private readonly GroundOpsSignals _signals = new();
    private readonly Com1TuneOfferStore _store;
    private readonly FakeArbiter _arbiter = new();
    private readonly Mock<IRadioTuner> _tuner = new();
    private readonly Com1TuneOfferService _service;

    public Com1TuneOfferTests()
    {
        _store = new Com1TuneOfferStore(_signals);
        _tuner.Setup(t => t.TuneCom1Async(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RadioTuneOutcome.Tuned);
        _service = Create(_tuner.Object);
    }

    public void Dispose()
    {
        _service.Dispose();
        _store.Dispose();
    }

    private Com1TuneOfferService Create(IRadioTuner? tuner)
        => new(Monitor(_options), Monitor(_gsx), _store, _signals, _arbiter, SpeechTestSupport.TempEventLog(),
            NullLogger<Com1TuneOfferService>.Instance, tuner);

    private static IOptionsMonitor<T> Monitor<T>(T value)
    {
        var monitor = new Mock<IOptionsMonitor<T>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(() => value);
        return monitor.Object;
    }

    private static WrongFrequencyReport Report(AssignedFrequency assigned, int com1Khz, string trigger = WrongFrequencyReport.WaitTrigger, bool atGate = true)
        => new(assigned, com1Khz, TimeSpan.FromSeconds(trigger == WrongFrequencyReport.WaitTrigger ? 60 : 0), trigger, atGate);

    private List<string> Spoken()
    {
        lock (_arbiter.Requests)
        {
            return [.. _arbiter.Requests.Select(r => r.Text)];
        }
    }

    private async Task<List<string>> SpokenAfterAsync(int count)
    {
        for (var i = 0; i < 100 && Spoken().Count < count; i++)
        {
            await Task.Delay(10);
        }

        return Spoken();
    }

    // ---- the judgement (pure) ----

    [Theory]
    [InlineData(true, FlightPhase.ColdAndDark, FrequencyWatchRule.AirportStations)]
    [InlineData(true, FlightPhase.Preflight, FrequencyWatchRule.AirportStations)]
    [InlineData(true, FlightPhase.Departure, FrequencyWatchRule.AirportStations)]
    [InlineData(true, FlightPhase.PushbackAndStart, FrequencyWatchRule.Assigned)]
    [InlineData(true, FlightPhase.TaxiOut, FrequencyWatchRule.Assigned)]
    [InlineData(true, FlightPhase.Cruise, FrequencyWatchRule.Assigned)]
    [InlineData(true, FlightPhase.TaxiIn, FrequencyWatchRule.Assigned)]
    [InlineData(true, FlightPhase.Shutdown, FrequencyWatchRule.None)]
    [InlineData(true, FlightPhase.Unknown, FrequencyWatchRule.None)]
    [InlineData(false, FlightPhase.Preflight, FrequencyWatchRule.None)]      // the flight-live gate stays
    [InlineData(false, FlightPhase.Cruise, FrequencyWatchRule.None)]
    public void Rule_ByPhase(bool live, FlightPhase phase, object expected)
        => Assert.Equal((FrequencyWatchRule)expected, FrequencyJudgement.Rule(live, phase));

    [Theory]
    [InlineData(121_705, Com1Verdict.Correct)]   // the assigned station
    [InlineData(121_935, Com1Verdict.Correct)]   // ATIS — on the airport's list
    [InlineData(121_980, Com1Verdict.Correct)]   // Delivery
    [InlineData(118_500, Com1Verdict.Correct)]   // Tower by its 25 kHz name (118.505 on the list)
    [InlineData(124_850, Com1Verdict.Wrong)]     // the 2026-11-16 frequency: no station of the airport
    [InlineData(0, Com1Verdict.Unknown)]         // COM1 not read
    [InlineData(108_500, Com1Verdict.Unknown)]   // not a COM frequency
    public void AtTheGate_AnyStationOfTheAirportIsCorrect(int com1Khz, object expected)
        => Assert.Equal((Com1Verdict)expected, FrequencyJudgement.Judge(FrequencyWatchRule.AirportStations, Ground, com1Khz, Egll));

    [Fact]
    public void AtTheGate_WithoutAStationList_NothingIsJudged()
    {
        Assert.Equal(Com1Verdict.Unknown, FrequencyJudgement.Judge(FrequencyWatchRule.AirportStations, Ground, 124_850, []));
        Assert.Equal(Com1Verdict.Unknown, FrequencyJudgement.Judge(FrequencyWatchRule.AirportStations, Ground, 124_850, null));
        // On the assigned frequency it is correct even without a list.
        Assert.Equal(Com1Verdict.Correct, FrequencyJudgement.Judge(FrequencyWatchRule.AirportStations, Ground, 121_705, null));
    }

    [Fact]
    public void FromPushbackOn_OnlyTheAssignedFrequencyIsCorrect()
    {
        Assert.Equal(Com1Verdict.Correct, FrequencyJudgement.Judge(FrequencyWatchRule.Assigned, Ground, 121_705, Egll));
        Assert.Equal(Com1Verdict.Wrong, FrequencyJudgement.Judge(FrequencyWatchRule.Assigned, Ground, 121_935, Egll));
        Assert.Equal(Com1Verdict.Wrong, FrequencyJudgement.Judge(FrequencyWatchRule.Assigned, Tallinn, 119_100, null));
        Assert.Equal(Com1Verdict.Unknown, FrequencyJudgement.Judge(FrequencyWatchRule.Assigned, null, 119_100, null));
        Assert.Equal(Com1Verdict.Unknown, FrequencyJudgement.Judge(FrequencyWatchRule.None, Ground, 124_850, Egll));
    }

    private static RadioSnapshot Radio(int com1Khz, bool live = true)
        => RadioSnapshot.Empty with
        {
            Com1ActiveKhz = com1Khz,
            Com1Live = live,
            SayIntentionsOn = true,
            Airports = [new AirportStations("EGLL", [.. Egll.Select(khz => new AtcStation("X", "", khz))], T0)],
        };

    [Fact]
    public void JudgeWrong_AtTheGate_ReadsTheLocalAirportsListFromTheRadioStore()
    {
        var wrong = SayIntentionsFrequencyMonitor.JudgeWrong(
            _options, flightLive: true, FlightPhase.Preflight, Ground, Radio(124_850), "EGLL", "EKCH", WrongFrequencyReport.WaitTrigger);

        Assert.NotNull(wrong);
        Assert.Equal(124_850, wrong.Com1Khz);
        Assert.True(wrong.AtGate);
        Assert.Equal(Ground, wrong.Assigned);

        // On ATIS: fine. Another airport's list (no list for the origin): quiet. COM1 not live: quiet.
        Assert.Null(SayIntentionsFrequencyMonitor.JudgeWrong(_options, true, FlightPhase.Preflight, Ground, Radio(121_935), "EGLL", "EKCH", "wait"));
        Assert.Null(SayIntentionsFrequencyMonitor.JudgeWrong(_options, true, FlightPhase.Preflight, Ground, Radio(124_850), "EKCH", "EGLL", "wait"));
        Assert.Null(SayIntentionsFrequencyMonitor.JudgeWrong(_options, true, FlightPhase.Preflight, Ground, Radio(124_850, live: false), "EGLL", "EKCH", "wait"));
        Assert.Null(SayIntentionsFrequencyMonitor.JudgeWrong(_options, false, FlightPhase.Preflight, Ground, Radio(124_850), "EGLL", "EKCH", "wait"));
        Assert.Null(SayIntentionsFrequencyMonitor.JudgeWrong(_options, true, FlightPhase.Preflight, Ground, null, "EGLL", "EKCH", "wait"));
    }

    [Fact]
    public void JudgeWrong_FromPushback_IsTheAssignedRule_AndTheSwitchStillRules()
    {
        // ATIS is on the list, but from pushback on COM1 must be on the assignment.
        var wrong = SayIntentionsFrequencyMonitor.JudgeWrong(_options, true, FlightPhase.TaxiOut, Ground, Radio(121_935), "EGLL", "EKCH", "wait");
        Assert.NotNull(wrong);
        Assert.False(wrong.AtGate);

        _options.FrequencyMonitorEnabled = false;
        Assert.Null(SayIntentionsFrequencyMonitor.JudgeWrong(_options, true, FlightPhase.TaxiOut, Ground, Radio(121_935), "EGLL", "EKCH", "wait"));
    }

    // ---- once per assignment and COM1 frequency ----

    [Fact]
    public void Memory_OneClaimPerPair_AnotherPairReArms()
    {
        var memory = new TuneOfferMemory();

        Assert.True(memory.TryClaim(121_705, 124_850));
        Assert.False(memory.TryClaim(121_705, 124_850));
        Assert.True(memory.TryClaim(121_705, 126_000));
        Assert.True(memory.TryClaim(121_705, 124_850));

        memory.Release(121_705, 124_850);
        Assert.True(memory.TryClaim(121_705, 124_850));
        memory.Reset();
        Assert.True(memory.TryClaim(121_705, 124_850));
    }

    // ---- the words ----

    [Fact]
    public void Question_NamesBothFrequenciesDigitByDigit()
    {
        Assert.Equal(
            "Captain, COM one is on one two four decimal eight five. Ground is one two one decimal seven zero five. Shall I tune it?",
            Com1TuneOfferPhrases.SpokenQuestion(new Com1TuneOffer(121_705, 124_850, "Ground")));
        Assert.Equal(
            "Captain, Ground is one two one decimal seven zero five. Shall I tune it?",
            Com1TuneOfferPhrases.SpokenQuestion(new Com1TuneOffer(121_705, 0, "Ground")));
        Assert.Equal(
            "Captain, COM one is on one two four decimal eight five. We should be on one two one decimal seven zero five. Shall I tune it?",
            Com1TuneOfferPhrases.SpokenQuestion(new Com1TuneOffer(121_705, 124_850, "")));
        Assert.Equal(
            "COM1 is on 124.850. Ground is 121.705. Shall I tune it?",
            Com1TuneOfferPhrases.CardPrompt(new Com1TuneOffer(121_705, 124_850, "Ground")));
    }

    [Fact]
    public void Subject_RoundTrips()
    {
        var offer = new Com1TuneOffer(134_325, 119_100, "Tallinn Control");

        Assert.Equal("134325|119100|Tallinn Control|", offer.Subject);
        Assert.Equal(offer, Com1TuneOffer.Parse(offer.Subject));
        var named = new Com1TuneOffer(121_705, 119_000, "Ground", "Scottish Control");
        Assert.Equal("121705|119000|Ground|Scottish Control", named.Subject);
        Assert.Equal(named, Com1TuneOffer.Parse(named.Subject));
        Assert.Null(Com1TuneOffer.Parse("InitB"));
        Assert.Null(Com1TuneOffer.Parse(null));
    }

    [Theory]
    [InlineData("yes", true)]
    [InlineData("affirm", true)]
    [InlineData("tune it", true)]
    [InlineData("go ahead", true)]
    [InlineData("no", false)]
    [InlineData("negative", false)]
    [InlineData("leave it", false)]
    [InlineData("i'll do it", false)]
    [InlineData("what", null)]
    public void Interpret_ClassifiesTheAnswer(string heard, bool? expected)
        => Assert.Equal(expected, Com1TuneOfferPhrases.Interpret(heard));

    [Theory]
    [InlineData(RadioTuneOutcome.Tuned, "Ground tuned.")]
    [InlineData(RadioTuneOutcome.NotConnected, "Unable to tune COM one, Captain. The radio is yours.")]
    [InlineData(RadioTuneOutcome.Failed, "Unable to tune COM one, Captain. The radio is yours.")]
    [InlineData(RadioTuneOutcome.Unverified, "Unable to tune COM one, Captain. The radio is yours.")]
    public void TuneResult_OneLine(object outcome, string expected)
        => Assert.Equal(expected, Com1TuneOfferPhrases.TuneResult("GROUND", (RadioTuneOutcome)outcome));

    // ---- the station COM1 is really on ----

    private static CommEntry Heard(long id, string frequency, string station, string channel = "COM1")
        => CommHistory.Parse(
            $$"""{"flight_id":1,"comm_history":[{"id":{{id}},"channel":"{{channel}}","frequency":"{{frequency}}","station_name":"{{station}}","incoming_message":"Scottish, Speedbird 504","outgoing_message":""}]}""")
            .Entries[0];

    private static readonly AirportStations EgllNamed = new("EGLL",
        [new AtcStation("GROUND", "Heathrow Ground", 121_705), new AtcStation("TOWER", "", 118_505)], T0);

    /// <summary>Rig 2026-11-16: on 119.000 at Heathrow SayIntentions answered as Scottish Control.</summary>
    [Fact]
    public void Com1Station_FromSayIntentionsOwnRecord_OfThatFrequency()
    {
        var names = new Com1StationNames();
        names.Note([Heard(10, "121.705", "Heathrow Ground"), Heard(11, "119.000", "Scottish Control"), Heard(12, "119.000", "Ramp", "INTERCOM3")]);

        Assert.Equal("Scottish Control", names.Resolve(119_000, EgllNamed));
        Assert.Equal("Scottish Control", names.Resolve(119_005, null));       // the 8.33 kHz name of the channel
        Assert.Equal(119_000, Heard(11, "119.000", "Scottish Control").FrequencyKhz);
    }

    /// <summary>Owner's rig 2026-11-16 01:21Z: SayIntentions named the station of 118.000
    /// "Unknown" and the FO said "COM one is on Unknown, …". A placeholder is not a name.</summary>
    [Theory]
    [InlineData("Unknown", false)]
    [InlineData("unknown", false)]
    [InlineData(" ", false)]
    [InlineData(null, false)]
    [InlineData("N/A", false)]
    [InlineData("Scottish Control", true)]
    public void Com1Station_APlaceholderIsNotAName(string? stationName, bool expected)
        => Assert.Equal(expected, Com1StationNames.IsARealName(stationName));

    [Fact]
    public void Com1Station_ElseTheLocalAirportsStation_ElseNone()
    {
        var names = new Com1StationNames();
        names.Note([Heard(11, "119.000", "Scottish Control")]);

        Assert.Equal("Heathrow Ground", names.Resolve(121_705, EgllNamed));   // the callsign
        Assert.Equal("TOWER", names.Resolve(118_505, EgllNamed));             // no callsign: the type
        Assert.Null(names.Resolve(124_850, EgllNamed));                       // nobody names it: no name
        Assert.Null(names.Resolve(124_850, null));
        Assert.Null(names.Resolve(0, EgllNamed));
    }

    [Fact]
    public void Com1Station_BelongsToTheFrequencyCom1IsOnNow_NewestRecordWins_ANewFlightForgets()
    {
        var names = new Com1StationNames();
        names.Note([Heard(11, "119.000", "Scottish Control")]);
        Assert.Equal("Scottish Control", names.Resolve(119_000, null));

        // COM1 moves on: the name does not follow it.
        Assert.Null(names.Resolve(126_000, null));

        names.Note([Heard(20, "119.000", "London Control")]);
        Assert.Equal("London Control", names.Resolve(119_000, null));

        names.Reset();
        Assert.Null(names.Resolve(119_000, null));
    }

    [Fact]
    public void JudgeWrong_CarriesTheStationCom1IsOn()
    {
        var names = new Com1StationNames();
        names.Note([Heard(11, "119.000", "Scottish Control")]);

        var wrong = SayIntentionsFrequencyMonitor.JudgeWrong(_options, true, FlightPhase.TaxiOut, Ground, Radio(119_000), "EGLL", "EKCH", "wait", names);
        Assert.Equal("Scottish Control", wrong!.Com1Station);

        var unnamed = SayIntentionsFrequencyMonitor.JudgeWrong(_options, true, FlightPhase.TaxiOut, Ground, Radio(126_000), "EGLL", "EKCH", "wait", names);
        Assert.Null(unnamed!.Com1Station);
    }

    [Fact]
    public void Question_NamesTheStationCom1IsOn_WhenKnown()
    {
        var offer = new Com1TuneOffer(121_705, 119_000, "Ground", "Scottish Control");

        Assert.Equal(
            "Captain, COM one is on Scottish Control, one one niner decimal zero. We should be on Ground, one two one decimal seven zero five. Shall I tune it?",
            Com1TuneOfferPhrases.SpokenQuestion(offer));
        Assert.Equal(
            "COM1 is on Scottish Control, 119.000. We should be on Ground, 121.705. Shall I tune it?",
            Com1TuneOfferPhrases.CardPrompt(offer));

        Assert.Equal("question", _service.Report(Report(Ground, 119_000) with { Com1Station = "Scottish Control" }));
        Assert.Equal("COM1 is on Scottish Control, 119.000. We should be on Ground, 121.705. Shall I tune it?", _store.Asking.Question!.Prompt);
        Assert.Equal("121705|119000|Ground|Scottish Control", _store.Asking.Question.Subject);
    }

    // ---- the question flow ----

    [Fact]
    public async Task Yes_TunesTheAssignedFrequencyThroughTheTuner_AndReportsIt()
    {
        Assert.Equal("question", _service.Report(Report(Ground, 124_850)));

        var question = _store.Asking.Question;
        Assert.NotNull(question);
        Assert.Equal("COM1 is on 124.850. Ground is 121.705. Shall I tune it?", question.Prompt);
        Assert.Empty(Spoken()); // the dialogue voices the question, not the service
        _tuner.Verify(t => t.TuneCom1Async(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        Assert.True(_store.Accept("web"));

        Assert.Equal(["Ground tuned."], await SpokenAfterAsync(1));
        _tuner.Verify(t => t.TuneCom1Async(121_705, "fo-offer", It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Owner's rig 2026-11-16 01:16Z: after a yes and "Ground tuned." the pilot went
    /// back to the same wrong frequency and keyed ATC; the FO stayed silent because the pair
    /// was remembered. A yes ends the matter, so the same pair is asked again.</summary>
    [Fact]
    public async Task AfterAYes_TheSamePairIsAskedAgain()
    {
        Assert.Equal("question", _service.Report(Report(Ground, 118_000)));
        Assert.True(_store.Accept("voice"));
        Assert.Equal(["Ground tuned."], await SpokenAfterAsync(1));

        Assert.Equal("question", _service.Report(Report(Ground, 118_000)));
    }

    [Fact]
    public async Task Yes_WithATuneThatDidNotTake_SaysOnePlainLine()
    {
        _tuner.Setup(t => t.TuneCom1Async(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RadioTuneOutcome.Unverified);
        _service.Report(Report(Ground, 124_850));

        _store.Accept("voice");

        Assert.Equal(["Unable to tune COM one, Captain. The radio is yours."], await SpokenAfterAsync(1));
    }

    [Fact]
    public async Task No_ChangesNothing_AndTheSamePairIsNotAskedAgain()
    {
        _service.Report(Report(Ground, 124_850));
        Assert.True(_store.Decline("voice", "captain said 'no'"));
        await Task.Delay(30);

        Assert.Empty(Spoken());
        _tuner.Verify(t => t.TuneCom1Async(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        // The wait and the push-to-talk for the same pair: nothing.
        Assert.Equal("none", _service.Report(Report(Ground, 124_850)));
        Assert.Equal("none", _service.Report(Report(Ground, 124_850, WrongFrequencyReport.PttTrigger)));
        Assert.Null(_store.Asking.Question);

        // Another COM1 frequency is a new question.
        Assert.Equal("question", _service.Report(Report(Ground, 126_000)));
    }

    [Fact]
    public async Task NoAnswerWithinTheMinute_CountsAsNo_NothingIsTuned()
    {
        _service.Report(Report(Ground, 124_850));
        var asked = _store.Asking.Question!.AskedAtUtc;

        Assert.Equal(CaptainsQuestionExpiry.Never, _store.ExpireOverdue(asked.AddSeconds(59)));
        Assert.Equal(CaptainsQuestionExpiry.CountsAsNo, _store.ExpireOverdue(asked.AddSeconds(61)));
        await Task.Delay(30);

        Assert.Null(_store.Asking.Question);
        Assert.Empty(Spoken());
        _tuner.Verify(t => t.TuneCom1Async(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal("none", _service.Report(Report(Ground, 124_850)));
    }

    [Fact]
    public void OneQuestionAtATime()
    {
        Assert.Equal("question", _service.Report(Report(Ground, 124_850)));
        Assert.Equal("none", _service.Report(Report(Ground, 126_000)));

        Assert.Equal("121705|124850|Ground|", _store.Asking.Question!.Subject);
    }

    [Fact]
    public void PushToTalk_AsksAtOnce_OncePerPair()
    {
        Assert.Equal("question", _service.Report(Report(Tallinn, 119_100, WrongFrequencyReport.PttTrigger, atGate: false)));
        _store.Decline("web", "declined on the Status board");

        // The wait fires for the same pair a minute later: already asked.
        Assert.Equal("none", _service.Report(Report(Tallinn, 119_100, atGate: false)));
    }

    [Fact]
    public void ANewFlightCycle_ForgetsThePair()
    {
        _service.Report(Report(Ground, 124_850));
        _store.Decline("web", "declined on the Status board");

        _signals.RaiseFlightCycleReset();

        Assert.Equal("question", _service.Report(Report(Ground, 124_850)));
    }

    [Fact]
    public void AStrayAccept_WithNoQuestionStanding_TunesNothing()
    {
        _service.Report(Report(Ground, 124_850));
        _store.Withdraw();

        _store.Accept("called");

        _tuner.Verify(t => t.TuneCom1Async(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- the statement, kept ----

    [Fact]
    public void OptionOff_TheStatementIsSpokenAsBefore()
    {
        _options.OfferToTune = false;

        Assert.Equal("statement", _service.Report(Report(Ground, 124_850)));

        Assert.Equal(["Captain, we should be on Ground, one two one decimal seven zero five."], Spoken());
        Assert.Equal("fo.wrong-frequency", _arbiter.Requests[0].Tag);
        Assert.Null(_store.Asking.Question);
    }

    [Fact]
    public void OptionOff_ThePushToTalkSaysNothing()
    {
        _options.OfferToTune = false;

        Assert.Equal("none", _service.Report(Report(Ground, 124_850, WrongFrequencyReport.PttTrigger)));

        Assert.Empty(Spoken());
    }

    /// <summary>The owner's rig, 2026-11-16 00:57Z: FO on the radios, TaxiOut, Ground 121.705
    /// assigned, the radio on 119.000 — the FO must ask there too.</summary>
    [Fact]
    public async Task FoOnTheRadios_TheQuestionIsAskedToo_ByTheSameRules()
    {
        _options.FoRadios.Enabled = true;

        var wrong = SayIntentionsFrequencyMonitor.JudgeWrong(_options, true, FlightPhase.TaxiOut, Ground, Radio(119_000), "EGLL", "EKCH", "wait");
        Assert.NotNull(wrong);
        // The gate rule and the flight-live gate hold with the FO on the radios as well.
        Assert.Null(SayIntentionsFrequencyMonitor.JudgeWrong(_options, true, FlightPhase.Preflight, Ground, Radio(121_935), "EGLL", "EKCH", "wait"));
        Assert.NotNull(SayIntentionsFrequencyMonitor.JudgeWrong(_options, true, FlightPhase.Preflight, Ground, Radio(124_850), "EGLL", "EKCH", "wait"));
        Assert.Null(SayIntentionsFrequencyMonitor.JudgeWrong(_options, false, FlightPhase.TaxiOut, Ground, Radio(119_000), "EGLL", "EKCH", "wait"));

        Assert.Equal("question", _service.Report(wrong));
        _store.Accept("voice");

        Assert.Equal(["Ground tuned."], await SpokenAfterAsync(1));
        _tuner.Verify(t => t.TuneCom1Async(121_705, "fo-offer", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void FoOnTheRadios_ThePushToTalkAsksToo()
    {
        _options.FoRadios.Enabled = true;

        Assert.Equal("question", _service.Report(Report(Tallinn, 119_100, WrongFrequencyReport.PttTrigger, atGate: false)));
    }

    // ---- the FO's own hand-off tune ----

    [Fact]
    public void HandoffWindow_OpenWhilePending_AndForTheGraceAfter()
    {
        var window = new FoHandoffTuneWindow();
        Assert.False(window.IsOpen(T0));

        window.Begin();
        Assert.True(window.IsOpen(T0.AddMinutes(5)));       // pending, however long the check-in waits

        window.Begin();                                     // ATC handed us on again meanwhile
        window.End(T0.AddSeconds(20));
        Assert.True(window.IsOpen(T0.AddSeconds(100)));

        window.End(T0.AddSeconds(30));
        Assert.True(window.IsOpen(T0.AddSeconds(39)));
        Assert.False(window.IsOpen(T0.AddSeconds(41)));
    }

    [Fact]
    public void WhileTheFoTunesAHandoff_ThePushToTalkDoesNotAsk()
    {
        _options.FoRadios.Enabled = true;
        var phases = new FakePhaseSource();
        phases.SetPhase(FlightPhase.Climb);
        var radio = new RadioStore();
        radio.Update(_ => Radio(119_100));                  // still on the old station
        var window = new FoHandoffTuneWindow();
        using var monitor = new SayIntentionsFrequencyMonitor(
            Monitor(_options), phases, _service, SpeechTestSupport.TempEventLog(),
            NullLogger<SayIntentionsFrequencyMonitor>.Instance, radio, handoffWindow: window);

        window.Begin();                                     // "Contact Tallinn Control on 134.325" — the FO is on it
        Assert.Equal("none", monitor.HandleAtcPtt(Tallinn));
        Assert.Null(_store.Asking.Question);

        window.End(DateTimeOffset.UtcNow - FoHandoffTuneWindow.Grace);
        Assert.Equal("question", monitor.HandleAtcPtt(Tallinn));   // the tune did not take: now it is a wrong frequency
    }

    [Fact]
    public void VoiceDialoguesOff_OrNoTuner_FallBackToTheStatement()
    {
        _gsx.VoiceControlEnabled = false;
        Assert.Equal("statement", _service.Report(Report(Ground, 124_850)));

        _gsx.VoiceControlEnabled = true;
        using var noTuner = Create(tuner: null);
        Assert.Equal("statement", noTuner.Report(Report(Ground, 126_000)));

        Assert.Equal(2, Spoken().Count);
        Assert.Null(_store.Asking.Question);
    }

    // ---- through the shared dialogue ----

    private CaptainsQuestionDialogue Dialogue(string? heard)
    {
        var mic = new Mock<IMicOwnership>();
        mic.Setup(m => m.Borrow(It.IsAny<string>())).Returns(Mock.Of<IDisposable>());
        mic.Setup(m => m.ListenAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(heard);
        return new CaptainsQuestionDialogue([_store], [Com1TuneOfferPhrases.Table], mic.Object, _arbiter, Monitor(_gsx),
            SpeechTestSupport.TempEventLog(), NullLogger<CaptainsQuestionDialogue>.Instance);
    }

    [Fact]
    public async Task Dialogue_AsksTheSpokenForm_AndYesTunes()
    {
        using var dialogue = Dialogue("yes");
        _service.Report(Report(Ground, 124_850));

        await dialogue.RunAsync(_store.Asking.Question!);

        var spoken = await SpokenAfterAsync(2);
        Assert.Equal(
            "Captain, COM one is on one two four decimal eight five. Ground is one two one decimal seven zero five. Shall I tune it?",
            spoken[0]);
        Assert.Equal("Ground tuned.", spoken[1]);
        _tuner.Verify(t => t.TuneCom1Async(121_705, "fo-offer", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Dialogue_NoAnswer_SaysNothingMore_AndLeavesTheQuestionToItsClock()
    {
        using var dialogue = Dialogue(null);
        _service.Report(Report(Ground, 124_850));

        await dialogue.RunAsync(_store.Asking.Question!);

        Assert.Single(Spoken()); // the question only — no "no answer" line
        Assert.NotNull(_store.Asking.Question);
        _tuner.Verify(t => t.TuneCom1Async(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- the push-to-talk into the monitor ----

    private SayIntentionsFrequencyMonitor MonitorWith(FakePhaseSource phases, RadioStore radio)
        => new(Monitor(_options), phases, _service, SpeechTestSupport.TempEventLog(),
            NullLogger<SayIntentionsFrequencyMonitor>.Instance, radio);

    [Fact]
    public void AtcPtt_OnAWrongFrequency_RaisesTheQuestionAtOnce()
    {
        var phases = new FakePhaseSource();
        phases.SetPhase(FlightPhase.TaxiOut);
        var radio = new RadioStore();
        radio.Update(_ => Radio(119_100));
        using var monitor = MonitorWith(phases, radio);

        Assert.Equal("question", monitor.HandleAtcPtt(Tallinn));
        Assert.Equal("134325|119100|Tallinn Control|", _store.Asking.Question!.Subject);

        // The same press again (or the wait): once per pair.
        _store.Decline("voice", "captain said 'no'");
        Assert.Equal("none", monitor.HandleAtcPtt(Tallinn));
    }

    [Fact]
    public void AtcPtt_OnTheRightFrequency_OrUnknown_OrOptionOff_DoesNothing()
    {
        var phases = new FakePhaseSource();
        phases.SetPhase(FlightPhase.TaxiOut);
        var radio = new RadioStore();
        radio.Update(_ => Radio(134_325));
        using var monitor = MonitorWith(phases, radio);

        Assert.Equal("none", monitor.HandleAtcPtt(Tallinn));       // on it
        Assert.Equal("none", monitor.HandleAtcPtt(null));          // no assignment

        radio.Update(_ => Radio(119_100));
        phases.SetPhase(FlightPhase.Preflight);                    // at the gate with no OFP: no list to judge by
        Assert.Equal("none", monitor.HandleAtcPtt(Tallinn));

        phases.SetPhase(FlightPhase.TaxiOut);
        _options.OfferToTune = false;
        Assert.Equal("none", monitor.HandleAtcPtt(Tallinn));
        _options.OfferToTune = true;
        _options.Enabled = false;
        Assert.Equal("none", monitor.HandleAtcPtt(Tallinn));

        Assert.Null(_store.Asking.Question);
        Assert.Empty(Spoken());
    }

    [Fact]
    public void AtcPtt_ThePressEdgeOfARegisteredSignal_ReachesTheMonitor()
    {
        var phases = new FakePhaseSource();
        phases.SetPhase(FlightPhase.TaxiOut);
        var signal = new FakePttSignal();
        using var monitor = new SayIntentionsFrequencyMonitor(
            Monitor(_options), phases, _service, SpeechTestSupport.TempEventLog(),
            NullLogger<SayIntentionsFrequencyMonitor>.Instance, new RadioStore(), pttSignals: [signal]);

        Assert.Equal(0, signal.Handlers);
        monitor.Start();
        Assert.Equal(1, signal.Handlers);
        monitor.Dispose();
        Assert.Equal(0, signal.Handlers);
    }

    private sealed class FakePttSignal : IAtcPttSignal
    {
        private Action? _handlers;

        public int Handlers => _handlers?.GetInvocationList().Length ?? 0;

        public event Action? AtcPttPressed
        {
            add => _handlers += value;
            remove => _handlers -= value;
        }
    }
}
