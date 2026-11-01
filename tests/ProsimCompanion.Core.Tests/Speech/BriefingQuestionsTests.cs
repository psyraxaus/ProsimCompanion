using Moq;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Briefings;
using ProsimCompanion.Speech.Gsx;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Issue #201: "Any questions, captain?" after every briefing — the store's round,
/// the phrase table's reading of the answer, and the follow-up that closes the round.</summary>
public sealed class BriefingQuestionsTests
{
    [Fact]
    public void Definition_IsYesNo_OneMinute_CountsAsNo()
    {
        var d = BriefingQuestionsStore.QuestionDefinition;
        Assert.Equal("briefingQuestions", d.Topic);
        Assert.Equal(CaptainsQuestionShape.YesNo, d.Shape);
        Assert.Equal(TimeSpan.FromMinutes(1), d.DefaultTtl);
        Assert.Equal(CaptainsQuestionExpiry.CountsAsNo, d.Expiry);
        Assert.False(d.AnswerIsFinalForCycle);
    }

    [Fact]
    public void Ask_OpensARound_Asked_CountsAndFollowsUp_UntilTheLimit()
    {
        var signals = new GroundOpsSignals();
        using var store = new BriefingQuestionsStore(signals);
        var prompts = new List<string>();
        store.QuestionRaised += (_, q) => prompts.Add(q.Prompt);

        Assert.True(store.Ask(departure: false));
        Assert.False(store.Ask(departure: false)); // one stands
        Assert.False(store.Departure);
        Assert.Equal([BriefingQuestionsStore.FirstPrompt], prompts);

        Assert.True(store.Asked("voice"));   // 1 → "Anything else?"
        Assert.True(store.Asked("voice"));   // 2 → "Anything else?"
        Assert.False(store.Asked("voice"));  // 3 = the limit: no follow-up, the round is closed
        Assert.Equal(3, store.QuestionsAsked);
        Assert.False(store.Asking.QuestionStands);
        Assert.Equal([BriefingQuestionsStore.FirstPrompt, BriefingQuestionsStore.AgainPrompt, BriefingQuestionsStore.AgainPrompt], prompts);

        // "No questions" declines the standing question.
        Assert.True(store.Ask(departure: true));
        Assert.True(store.Departure);
        Assert.Equal(0, store.QuestionsAsked);
        Assert.True(store.Decline("voice", "captain said 'no questions'"));
        Assert.False(store.Asking.QuestionStands);
    }

    [Theory]
    [InlineData("no questions")]
    [InlineData("No, thank you")]
    [InlineData("nothing further")]
    [InlineData("negative")]
    [InlineData("all good")]
    [InlineData("roger")]
    public void Interpret_NoQuestions_IsNo(string heard)
        => Assert.Equal(QuestionReplyKind.No, BriefingQuestionsPhrases.Interpret(heard, _ => { })?.Kind);

    [Theory]
    [InlineData("yes")]
    [InlineData("affirm")]
    [InlineData("yes please")]
    [InlineData("")]
    public void Interpret_BareYesOrNothing_AsksToGoAhead(string heard)
        => Assert.Null(BriefingQuestionsPhrases.Interpret(heard, _ => { }));

    [Fact]
    public void Interpret_AQuestion_IsSettled_AndHandsTheWordsOver()
    {
        string? asked = null;
        var reply = BriefingQuestionsPhrases.Interpret("what is our alternate today?", q => asked = q);
        Assert.Equal(QuestionReplyKind.Settled, reply!.Kind);
        Assert.Null(asked);
        reply.Apply!();
        Assert.Equal("what is our alternate today?", asked);
    }

    [Fact]
    public async Task FollowUp_WaitsForTheAnswer_ThenAsksAgain_OrClosesAtTheLimit()
    {
        var signals = new GroundOpsSignals();
        using var store = new BriefingQuestionsStore(signals);
        var spoken = new List<string>();
        var arbiter = new Moq.Mock<ISpeechArbiter>();
        arbiter.Setup(a => a.SpeakAsync(It.IsAny<string>(), It.IsAny<SpeechPriority>(), It.IsAny<CancellationToken>()))
            .Callback<string, SpeechPriority, CancellationToken>((t, _, _) => spoken.Add(t))
            .Returns(Task.FromResult(SpeechOutcome.Spoken));
        var prompts = new List<string>();
        store.QuestionRaised += (_, q) => prompts.Add(q.Prompt);

        Assert.True(store.Ask(departure: false));
        var answer = new TaskCompletionSource();
        var followUp = BriefingQuestionsPhrases.FollowUpAsync(store, arbiter.Object, answer.Task, taken: true);
        Assert.False(followUp.IsCompleted);          // nothing until the FO's answer has played
        answer.SetResult();
        await followUp;
        Assert.Equal([BriefingQuestionsStore.FirstPrompt, BriefingQuestionsStore.AgainPrompt], prompts);
        Assert.Empty(spoken);                        // the dialogue voices the follow-up, not this

        // Not taken (no question service): the offline line, then the follow-up.
        await BriefingQuestionsPhrases.FollowUpAsync(store, arbiter.Object, null, taken: false);
        Assert.Equal([ProsimCompanion.Speech.Questions.FoQuestionCore.LlmOffline], spoken);
        Assert.Equal(3, prompts.Count);

        // The third question reaches the limit: "Briefing complete." and no further prompt.
        await BriefingQuestionsPhrases.FollowUpAsync(store, arbiter.Object, Task.CompletedTask, taken: true);
        Assert.Equal(BriefingQuestionsStore.CompleteLine, spoken[^1]);
        Assert.Equal(3, prompts.Count);
        Assert.False(store.Asking.QuestionStands);
    }

    [Fact]
    public void Table_IsWiredToTheStore_AndClosesWithBriefingComplete()
    {
        var signals = new GroundOpsSignals();
        using var store = new BriefingQuestionsStore(signals);
        var arbiter = new Moq.Mock<ISpeechArbiter>();
        var table = BriefingQuestionsPhrases.Create(store, arbiter.Object, null);
        Assert.Equal(BriefingQuestionsStore.QuestionDefinition.Topic, table.Topic);
        Assert.Equal(BriefingQuestionsStore.CompleteLine, table.NoReadback);
        Assert.Empty(table.Grammar(new CaptainsQuestion("briefingQuestions", DateTimeOffset.UtcNow, BriefingQuestionsStore.FirstPrompt, null, 1, [], null)));
    }
}
