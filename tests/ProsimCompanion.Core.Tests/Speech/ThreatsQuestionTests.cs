using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Weather;
using ProsimCompanion.Speech.Briefings;
using ProsimCompanion.Speech.Gsx;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Issue #180: the "any other threats, captain?" question — its store adapter, its
/// phrase table's reading of free text, and the METAR inferences the threat list rests on.</summary>
public sealed class ThreatsQuestionTests
{
    [Fact]
    public void Definition_IsYesNo_StandardTtl_CountsAsNo_NotFinalForTheCycle()
    {
        var d = ThreatsQuestionStore.QuestionDefinition;

        Assert.Equal("briefingThreats", d.Topic);
        Assert.Equal(CaptainsQuestionShape.YesNo, d.Shape);
        Assert.Equal(CaptainsQuestionDefinition.StandardTtl, d.DefaultTtl);
        Assert.Equal(CaptainsQuestionExpiry.CountsAsNo, d.Expiry);
        Assert.Equal(CaptainsQuestionDefinition.StandardMaxAsks, d.MaxAsks);
        Assert.False(d.AnswerIsFinalForCycle);
    }

    [Fact]
    public void Ask_RaisesOnce_Note_AppendsAndAnswersYes_AndASecondBriefingAsksAgain()
    {
        var signals = new GroundOpsSignals();
        using var store = new ThreatsQuestionStore(signals);
        var raised = 0;
        var answered = new List<(bool Accepted, string Source)>();
        store.QuestionRaised += (_, _) => raised++;
        store.Answered += (_, a) => answered.Add(a);

        Assert.True(store.Ask());
        Assert.False(store.Ask()); // one stands
        Assert.Equal(ThreatsQuestionStore.Prompt, store.Asking.Question!.Prompt);
        Assert.False(store.Note("  ", "voice")); // nothing said = nothing noted
        Assert.True(store.Note(" birds reported on the approach ", "voice"));

        Assert.Equal(["birds reported on the approach"], store.CaptainsThreats);
        Assert.True(store.Asking.Accepted);
        Assert.Null(store.Asking.Question);
        Assert.Equal(1, raised);
        Assert.Equal([(true, "voice")], answered);

        // A re-brief after a runway change asks again and keeps what was said.
        Assert.True(store.Ask());
        Assert.Equal(2, raised);
        Assert.Single(store.CaptainsThreats);
        Assert.True(store.Decline("voice", "captain said 'nothing else'"));
        Assert.False(store.Asking.Accepted);

        signals.RaiseFlightCycleReset();
        Assert.Empty(store.CaptainsThreats);
        Assert.Null(store.Asking.Question);
        Assert.Null(store.Asking.Answer);
    }

    [Theory]
    [InlineData("no", QuestionReplyKind.No)]
    [InlineData("Negative.", QuestionReplyKind.No)]
    [InlineData("nothing else", QuestionReplyKind.No)]
    [InlineData("no further threats today", QuestionReplyKind.No)]
    [InlineData("that's all", QuestionReplyKind.No)]
    [InlineData("none, thank you", QuestionReplyKind.No)]
    [InlineData("birds on the approach", QuestionReplyKind.Settled)]
    [InlineData("yes, the wind is gusting", QuestionReplyKind.Settled)]
    [InlineData("Notam, taxiway Bravo closed.", QuestionReplyKind.Settled)]
    public void Interpret_ReadsTheNothingElseWords_AndTreatsAnythingElseAsAThreat(string heard, QuestionReplyKind expected)
    {
        string? noted = null;
        var reply = ThreatsQuestionPhrases.Interpret(heard, t => noted = t);

        Assert.NotNull(reply);
        Assert.Equal(expected, reply.Kind);
        if (expected == QuestionReplyKind.Settled)
        {
            reply.Apply!();
            Assert.Equal(heard.Trim().TrimEnd('.', '!', ','), noted);
        }
        else
        {
            Assert.Null(noted);
        }
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("affirm")]
    [InlineData("   ")]
    public void Interpret_ABareYesOrNothingIsNotAnAnswer(string heard)
        => Assert.Null(ThreatsQuestionPhrases.Interpret(heard, _ => { }));

    [Fact]
    public void Table_IsFreeForm_WithTheFixedPromptAndEventName()
    {
        var store = new ThreatsQuestionStore(new GroundOpsSignals());
        var arbiter = new Moq.Mock<ProsimCompanion.Speech.Arbiter.ISpeechArbiter>();
        var table = ThreatsQuestionPhrases.Create(store, arbiter.Object);
        var question = new CaptainsQuestion("briefingThreats", DateTimeOffset.UtcNow, ThreatsQuestionStore.Prompt, null, 1, [], null);

        Assert.Equal("briefingThreats", table.Topic);
        Assert.Equal("briefing.threats", table.EventName);
        Assert.Empty(table.Grammar(question)); // free-form: the LAN transcription reaches the table raw
        Assert.Equal("Any other threats, captain?", table.SpokenPrompt(question));
        Assert.Equal("No further threats noted.", table.UnansweredPhrase(question, store.Asking));
    }

    [Fact]
    public void MetarConditions_Surface_FollowsThePrecipitation()
    {
        var dry = new WxFacts("YSSY 1200Z 27012KT 9999 FEW030 22/12 Q1013", 270, 12, 9999, 1013, 22, null, null);

        Assert.Equal(RunwaySurfaceState.Unknown, MetarConditions.Surface(WxFacts.None));
        Assert.Equal(RunwaySurfaceState.Dry, MetarConditions.Surface(dry));
        Assert.Equal(RunwaySurfaceState.Wet, MetarConditions.Surface(dry with { Precip = PrecipKind.Rain }));
        Assert.Equal(RunwaySurfaceState.Wet, MetarConditions.Surface(dry with { Precip = PrecipKind.Thunderstorm }));
        Assert.Equal(RunwaySurfaceState.Contaminated, MetarConditions.Surface(dry with { Precip = PrecipKind.Snow }));
        Assert.Equal(RunwaySurfaceState.Contaminated, MetarConditions.Surface(dry with { Precip = PrecipKind.Freezing }));
    }

    [Theory]
    [InlineData(22, 12, 9999, PrecipKind.Rain, false)]       // warm rain: no icing
    [InlineData(5, -2, 9999, PrecipKind.None, false)]        // cold, dry, spread 7: no
    [InlineData(5, 3, 9999, PrecipKind.None, true)]          // spread 2: frost likely
    [InlineData(8, 0, 9999, PrecipKind.Rain, true)]          // cold rain
    [InlineData(2, -5, 800, PrecipKind.None, true)]          // freezing fog visibility
    [InlineData(10, 0, 1500, PrecipKind.None, true)]         // boundary values count
    [InlineData(11, 11, 200, PrecipKind.Snow, false)]        // above the temperature gate
    public void MetarConditions_GroundIcingLikely_Table(int temp, int dewPoint, int visibility, PrecipKind precip, bool expected)
    {
        var wx = new WxFacts("x", 270, 10, visibility, 1013, temp, null, null, Precip: precip, DewPointC: dewPoint);
        Assert.Equal(expected, MetarConditions.GroundIcingLikely(wx));
        Assert.False(MetarConditions.GroundIcingLikely(WxFacts.None));
    }
}
