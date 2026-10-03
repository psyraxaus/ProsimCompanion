using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Weather;
using ProsimCompanion.Speech.Llm;
using ProsimCompanion.Speech.Questions;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Issue #149: the FO answers a question from the fact sheet, numbers verified; the
/// strict re-ask; the time budget; the sterile and offline paths; and — the one that matters
/// most — an answer is speech only, whatever it says.</summary>
public sealed class FoQuestionServiceTests : IDisposable
{
    /// <summary>Answers each request in turn: a streamed body for a stream request, a plain
    /// completion for the strict re-ask. A null entry hangs until the request is cancelled.</summary>
    private sealed class SequenceHandler(params Func<Stream>?[] bodies) : HttpMessageHandler
    {
        private int _index;
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            var body = _index < bodies.Length ? bodies[_index++] : null;
            if (body is null)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                throw new TimeoutException("the test endpoint never answered");
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body()) };
        }
    }

    private static Func<Stream> Streamed(params string[] deltas) => () => LlmWire.Sliced(LlmWire.Sse(deltas), 13);

    private static Func<Stream> Completion(string text) => () => LlmWire.Whole(JsonSerializer.Serialize(new
    {
        choices = new[] { new { message = new { role = "assistant", content = text } } },
    }));

    private readonly DrainingArbiter _arbiter = new();
    private readonly FakePhaseSource _phases = new();
    private readonly FakeDataRefs _refs = new();
    private readonly LlmHealthStore _health = new();
    private readonly JsonlEventLog _eventLog = SpeechTestSupport.TempEventLog();
    private readonly SpeechOptions _speech = new();
    private readonly BriefingOptions _briefing = new()
    {
        LlmEnabled = true,
        LlmBaseUrl = "http://llm.test/v1",
        LlmModel = "test-model",
        LlmTimeoutSeconds = 0,
    };

    public FoQuestionServiceTests()
    {
        _speech.FoQuestions.Enabled = true;
        _speech.FoQuestions.StandBySeconds = 0;
        _phases.SetPhase(FlightPhase.Cruise);
        _phases.Data = new FlightDataSnapshot { IsValid = true, AltitudeFt = 37000, GroundSpeedKt = 450, IndicatedAirspeedKt = 270 };
        _refs.Values["aircraft.fuel.total.amount.kg"] = 6200.0;
        _refs.Values["aircraft.weight.zfw"] = 58800.0;
        _refs.Values["aircraft.weight.gross"] = 65000.0;
    }

    private FoQuestionService Service(SequenceHandler handler)
    {
        var briefing = SpeechTestSupport.BriefingMonitor(_briefing);
        var client = new OpenAiChatClient(briefing, new HttpClient(handler), _health) { StreamTimeoutFloorSeconds = 20 };
        var narrator = new StreamingNarrator(client, _arbiter, _eventLog, briefing, NullLogger<StreamingNarrator>.Instance)
        {
            WatchInterval = TimeSpan.FromMilliseconds(20),
        };
        var ofp = new OfpStore();
        ofp.Set(new OfpData { DestinationIcao = "LIRF", DestinationName = "Rome Fiumicino", FuelPlanLandingKg = 3050, FuelMinTakeoffKg = 7000 });
        var facts = new FoFactSource(
            _phases, new FlightProgressStore(), new FlightTimesStore(), ofp, new LoadsheetStore(),
            new HeroWeatherStore(), new ArrivalMinimaStore(), _refs);
        return new FoQuestionService(
            SpeechTestSupport.SpeechMonitor(_speech), briefing, client, narrator, _arbiter, _phases, facts, _health,
            _eventLog, NullLogger<FoQuestionService>.Instance);
    }

    private static async Task Settle(FoQuestionService service)
    {
        if (service.InFlight is { } task)
        {
            await task;
        }
    }

    private List<JsonElement> Events(string type)
    {
        _eventLog.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return File.ReadAllLines(_eventLog.Path)
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Where(e => e.GetProperty("type").GetString() == type)
            .ToList();
    }

    private string[] Spoken => [.. _arbiter.Segments.Select(s => s.Text).Concat(_arbiter.Requests.Where(r => r.Stream is null).Select(r => r.Text))];

    public void Dispose() => _eventLog.DisposeAsync().AsTask().GetAwaiter().GetResult();

    [Fact]
    public async Task Question_IsAnsweredFromTheFacts_Streamed_AndRecorded()
    {
        var handler = new SequenceHandler(Streamed("Fuel on board is six point ", "two tonnes, against a planned landing fuel of three point one."));
        using var service = Service(handler);

        Assert.True(service.TryAsk("what is our fuel on board right now"));
        await Settle(service);

        Assert.Equal(["Fuel on board is six point two tonnes, against a planned landing fuel of three point one."], Spoken);
        Assert.Equal("fo.answer", _arbiter.Requests[0].Tag);
        Assert.Contains("- Fuel on board: 6200 kilograms (6.2 tonnes)", handler.RequestBodies[0], StringComparison.Ordinal);
        Assert.Contains("CAPTAIN ASKS: what is our fuel on board right now", handler.RequestBodies[0], StringComparison.Ordinal);

        var query = Assert.Single(Events("fo.query")).GetProperty("payload");
        Assert.Equal("what is our fuel on board right now", query.GetProperty("question").GetString());
        var answer = Assert.Single(Events("fo.answer")).GetProperty("payload");
        Assert.Equal("answered", answer.GetProperty("outcome").GetString());
        Assert.True(answer.GetProperty("totalMs").GetInt64() >= 0);
    }

    [Fact]
    public async Task WakeWord_IsStripped_BeforeTheModelSeesTheQuestion()
    {
        var handler = new SequenceHandler(Streamed("We are in the cruise."));
        using var service = Service(handler);

        Assert.True(service.TryAsk("question, what phase are we in"));
        await Settle(service);

        Assert.Contains("CAPTAIN ASKS: what phase are we in", handler.RequestBodies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnverifiedStream_EarnsOneStrictReask_WhichIsSpokenWhenItVerifies()
    {
        // 7.5 tonnes is not in the facts: the streamed sentence is refused before a word is heard.
        var handler = new SequenceHandler(
            Streamed("Fuel on board is seven point five tonnes."),
            Completion("Fuel on board is six point two tonnes."));
        using var service = Service(handler);

        Assert.True(service.TryAsk("what is our fuel on board right now"));
        await Settle(service);

        Assert.Equal(["Fuel on board is six point two tonnes."], Spoken);
        Assert.Equal(2, handler.RequestBodies.Count);
        Assert.Contains("Use ONLY these numbers", handler.RequestBodies[1], StringComparison.Ordinal);
        Assert.DoesNotContain("\"stream\":true", handler.RequestBodies[1], StringComparison.Ordinal);
        Assert.Equal("answered-on-retry", Assert.Single(Events("fo.answer")).GetProperty("payload").GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task UnverifiedTwice_SpeaksTheFixedLine()
    {
        var handler = new SequenceHandler(
            Streamed("Fuel on board is seven point five tonnes."),
            Completion("Fuel on board is eight point one tonnes."));
        using var service = Service(handler);

        Assert.True(service.TryAsk("what is our fuel on board right now"));
        await Settle(service);

        Assert.Equal([FoQuestionCore.NoVerifiedAnswer], Spoken);
        Assert.Equal("unverified", Assert.Single(Events("fo.answer")).GetProperty("payload").GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task NoAnswerInsideTheBudget_SaysStandBy_ThenTheFixedLine()
    {
        _speech.FoQuestions.TimeBudgetSeconds = 1;
        _speech.FoQuestions.StandBySeconds = 1;      // fires at 1 s; the budget also ends at 1 s — order below is by content
        var handler = new SequenceHandler(new Func<Stream>?[] { null });     // never answers
        using var service = Service(handler);
        var started = DateTime.UtcNow;

        Assert.True(service.TryAsk("how long until we reach the destination"));
        await Settle(service);

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10), "the budget did not cut the wait");
        Assert.Contains(FoQuestionCore.NoVerifiedAnswer, Spoken);
        Assert.Equal("timed-out", Assert.Single(Events("fo.answer")).GetProperty("payload").GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task AnAnswerThatSpeaksPastTheBudget_IsNotCut()
    {
        // Owner's gate test 2026-10-03: every answer was dropped "cancelled" at exactly the
        // 6 s budget although speech had begun at ~2 s. The budget covers the wait for the
        // FIRST word only; once speaking, the answer plays out.
        _speech.FoQuestions.TimeBudgetSeconds = 1;
        _arbiter.SegmentDuration = TimeSpan.FromMilliseconds(700);      // two sentences ≈ 1.4 s of speech
        var handler = new SequenceHandler(Streamed("We are in the cruise. ", "Fuel on board is six point two tonnes."));
        using var service = Service(handler);

        Assert.True(service.TryAsk("what phase are we in right now"));
        await Settle(service);

        Assert.Equal(["We are in the cruise.", "Fuel on board is six point two tonnes."], _arbiter.Segments.Select(s => s.Text));
        var answer = Assert.Single(Events("fo.answer")).GetProperty("payload");
        Assert.Equal("answered", answer.GetProperty("outcome").GetString());
        Assert.True(answer.GetProperty("totalMs").GetInt64() >= 1000);
    }

    [Fact]
    public async Task StandBy_IsSpoken_WhenTheModelIsSlow_ButNotWhenItIsQuick()
    {
        _speech.FoQuestions.StandBySeconds = 1;
        _speech.FoQuestions.TimeBudgetSeconds = 5;
        var quick = new SequenceHandler(Streamed("We are in the cruise."));
        using (var service = Service(quick))
        {
            Assert.True(service.TryAsk("what phase of flight are we in"));
            await Settle(service);
        }

        Assert.DoesNotContain(FoQuestionCore.StandBy, Spoken);
    }

    [Fact]
    public async Task AnAnswerIsSpeechOnly_EvenWhenItContainsCommandPhrases()
    {
        // The model is told never to instruct; if it does anyway, the words are only ever spoken.
        var handler = new SequenceHandler(Streamed(
            "Set heading one two zero and request refueling. ", "Tune the ILS and arm the approach."));
        using var service = Service(handler);

        Assert.True(service.TryAsk("what should we do next captain"));
        await Settle(service);

        // Spoken verbatim (no numbers outside the facts? 120 is not a fact — so the stream is refused
        // and the fixed line is spoken instead; either way nothing but speech happens).
        Assert.NotEmpty(Spoken);
        Assert.Empty(_refs.Writes);                                        // no dataref write, no momentary press
        Assert.All(_arbiter.Requests, r => Assert.Equal("fo.answer", r.Tag));
        Assert.Single(Events("fo.query"));                                 // the answer did not become a new utterance
    }

    [Fact]
    public async Task AnAnswerWithoutNumbers_IsSpokenVerbatim_AndStillOnlySpoken()
    {
        var handler = new SequenceHandler(Streamed("Request refueling and tune the ILS."));
        using var service = Service(handler);

        Assert.True(service.TryAsk("what should we do next captain"));
        await Settle(service);

        Assert.Equal(["Request refueling and tune the ILS."], Spoken);
        Assert.Empty(_refs.Writes);
    }

    [Fact]
    public void NotAQuestion_IsDeclined_SoTheRouterGoesOn()
    {
        using var service = Service(new SequenceHandler());

        Assert.False(service.TryAsk("what is fuel"));                     // three words
        Assert.False(service.TryAsk("please give me the fuel figure"));   // no lead-in
        Assert.False(service.TryAsk(""));
        _speech.FoQuestions.Enabled = false;
        Assert.False(service.TryAsk("what is our fuel on board right now"));
        Assert.Empty(_arbiter.Requests);
    }

    [Fact]
    public void SterileCockpit_HearsTheQuestion_AndSaysNothing()
    {
        _phases.SetPhase(FlightPhase.Climb);
        _phases.Data = new FlightDataSnapshot { IsValid = true, AltitudeFt = 6000 };
        using var service = Service(new SequenceHandler(Streamed("Never asked.")));

        Assert.True(service.TryAsk("what is our fuel on board right now"));

        Assert.Null(service.InFlight);
        Assert.Empty(_arbiter.Requests);
        var answer = Assert.Single(Events("fo.answer")).GetProperty("payload");
        Assert.Equal("sterile", answer.GetProperty("outcome").GetString());
    }

    [Fact]
    public void LlmUnhealthy_GivesTheOfflineAdvisory()
    {
        _health.Report(LlmHealthState.Unreachable, "refused");
        using var service = Service(new SequenceHandler(Streamed("Never asked.")));

        Assert.True(service.TryAsk("what is our fuel on board right now"));

        Assert.Equal([FoQuestionCore.LlmOffline], Spoken);
        Assert.Equal("llm-offline", Assert.Single(Events("fo.answer")).GetProperty("payload").GetProperty("outcome").GetString());
    }

    [Theory]
    [InlineData("what is our fuel on board", true)]
    [InlineData("What's the destination weather like", true)]
    [InlineData("tell me the time to top of descent", true)]
    [InlineData("are we above minimum takeoff fuel", true)]
    [InlineData("question how many passengers today", true)]
    [InlineData("what is fuel", false)]
    [InlineData("request refueling now please", false)]
    [InlineData("set heading one two zero", false)]
    [InlineData("whatever you say captain", false)]
    public void IsQuestion_LeadInAndLength(string utterance, bool expected)
        => Assert.Equal(expected, FoQuestionCore.IsQuestion(utterance, new FoQuestionOptions()));

    // ---- small talk (issue #152) ---------------------------------------------------------

    [Theory]
    [InlineData("who is better chelsea or arsenal", false)]
    [InlineData("tell me a fun fact", false)]
    [InlineData("what is the capital of peru", false)]
    [InlineData("do you like your job", false)]
    [InlineData("what is our fuel on board", true)]
    [InlineData("how long to top of descent", true)]
    [InlineData("tell me the destination weather", true)]
    [InlineData("what time do we land", true)]
    [InlineData("how many passengers today", true)]
    [InlineData("are we above minimum takeoff fuel", true)]
    [InlineData("what is the gate number", true)]
    [InlineData("", true)]
    public void IsFlightQuestion_SortsFlightFromChat(string question, bool flight)
        => Assert.Equal(flight, FoQuestionCore.IsFlightQuestion(question));

    [Fact]
    public void SmallTalk_DropsTheLeadInRule_KeepsTheLengthRule()
    {
        var on = new FoQuestionOptions { SmallTalk = true };
        Assert.True(FoQuestionCore.IsQuestion("who is better chelsea or arsenal", on));
        Assert.False(FoQuestionCore.IsQuestion("chelsea or arsenal", on));                 // three words
        Assert.False(FoQuestionCore.IsQuestion("who is better chelsea or arsenal", new FoQuestionOptions()));
    }

    [Theory]
    [InlineData("Octopuses have three hearts, Captain.", true)]
    [InlineData("Jupiter has ninety five moons.", true)]
    [InlineData("Arsenal won it in nineteen ninety eight, Captain.", true)]
    [InlineData("Fuel on board is six point two tonnes.", false)]                         // flight data with a figure
    [InlineData("We land at fourteen zero five zulu.", false)]
    [InlineData("I'd never say Arsenal in this cockpit.", true)]
    public void ChatGuard_RefusesFlightFiguresOnly(string sentence, bool allowed)
        => Assert.Equal(allowed, FoQuestionCore.ChatSentenceAllowed(sentence));

    [Fact]
    public async Task SmallTalk_AnswersFromGeneralKnowledge_WithUnverifiedNumbers_AndRecordsChat()
    {
        _speech.FoQuestions.SmallTalk = true;
        var handler = new SequenceHandler(Streamed("Octopuses have three hearts, Captain. ", "Jupiter has ninety five moons."));
        using var service = Service(handler);

        Assert.True(service.TryAsk("tell me a fun fact please"));
        await Settle(service);

        Assert.Equal(["Octopuses have three hearts, Captain.", "Jupiter has ninety five moons."], Spoken);
        Assert.Contains("CAPTAIN SAYS: tell me a fun fact please", handler.RequestBodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("FACTS:", handler.RequestBodies[0], StringComparison.Ordinal);     // the fact sheet is not sent on the chat path
        var answer = Assert.Single(Events("fo.answer")).GetProperty("payload");
        Assert.Equal("chat", answer.GetProperty("mode").GetString());
        Assert.Equal("answered", answer.GetProperty("outcome").GetString());
        Assert.Equal("chat", Assert.Single(Events("fo.query")).GetProperty("payload").GetProperty("mode").GetString());
    }

    [Fact]
    public async Task SmallTalk_AFlightFigureOnTheChatPath_IsRefused()
    {
        _speech.FoQuestions.SmallTalk = true;
        var handler = new SequenceHandler(Streamed("Fuel on board is seven point five tonnes, Captain."));
        using var service = Service(handler);

        Assert.True(service.TryAsk("do you like your job here"));
        await Settle(service);

        Assert.Equal([FoQuestionCore.NoVerifiedAnswer], Spoken);
        Assert.Equal("unverified", Assert.Single(Events("fo.answer")).GetProperty("payload").GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task SmallTalk_LetMeCheck_HandsOverToTheStrictPath()
    {
        _speech.FoQuestions.SmallTalk = true;
        var handler = new SequenceHandler(
            Streamed("Let me check."),
            Streamed("Fuel on board is six point two tonnes."));
        using var service = Service(handler);

        Assert.True(service.TryAsk("give me the number please"));   // no flight word → chat first
        await Settle(service);

        Assert.Equal(["Let me check.", "Fuel on board is six point two tonnes."], Spoken);
        Assert.Contains("FACTS:", handler.RequestBodies[1], StringComparison.Ordinal);
        var answers = Events("fo.answer").Select(e => e.GetProperty("payload")).ToList();
        Assert.Equal(2, answers.Count);
        Assert.Equal("chat→flight", answers[0].GetProperty("mode").GetString());
        Assert.Equal("flight", answers[1].GetProperty("mode").GetString());
    }

    [Fact]
    public async Task SmallTalk_AFlightQuestion_StillTakesTheStrictPath()
    {
        _speech.FoQuestions.SmallTalk = true;
        var handler = new SequenceHandler(Streamed("Fuel on board is seven point five tonnes."));   // not in the facts
        using var service = Service(handler);

        Assert.True(service.TryAsk("what is our fuel on board right now"));
        await Settle(service);

        Assert.Contains("FACTS:", handler.RequestBodies[0], StringComparison.Ordinal);
        Assert.Equal("flight", Assert.Single(Events("fo.query")).GetProperty("payload").GetProperty("mode").GetString());
        Assert.DoesNotContain("seven point five", string.Join(" ", Spoken), StringComparison.Ordinal);   // verified away
    }
}
