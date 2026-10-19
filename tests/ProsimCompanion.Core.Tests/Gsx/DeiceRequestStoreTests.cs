using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Automation;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>The per-departure de-icing store (2026-10-09): one question per cycle, answers
/// are final for the cycle, and the flight-cycle reset starts over. Issue #166 (2026-10-11):
/// the step's turn arms the policy and re-asks an open question once; the question carries
/// its own TTL; nothing but the TTL declines on silence. Since C2 an adapter of the shared
/// captain's-question store — the verdict facts stay here, the question rules are inherited.</summary>
public sealed class DeiceRequestStoreTests
{
    private static (DeiceRequestStore Store, GroundOpsSignals Signals) Make()
    {
        var signals = new GroundOpsSignals();
        return (new DeiceRequestStore(signals), signals);
    }

    private static CaptainsQuestion Question(DateTimeOffset askedAtUtc, string prompt = "Request it?", TimeSpan? ttl = null, int attempt = 1)
        => new(DeiceRequestStore.QuestionDefinition.Topic, askedAtUtc, prompt, ttl ?? DeiceRequestSnapshot.QuestionTtl, attempt, [], null);

    [Fact]
    public void Definition_IsTheOneThatTaughtTheBaseItsRules()
    {
        var definition = DeiceRequestStore.QuestionDefinition;
        Assert.Equal("deice", definition.Topic);
        Assert.Equal(CaptainsQuestionShape.YesNo, definition.Shape);
        Assert.Equal(TimeSpan.FromMinutes(5), definition.DefaultTtl);
        Assert.Equal(TimeSpan.FromMinutes(5), DeiceRequestSnapshot.QuestionTtl);
        Assert.Equal(CaptainsQuestionExpiry.CountsAsNo, definition.Expiry);
        Assert.Equal(2, definition.MaxAsks);
        Assert.True(definition.AnswerIsFinalForCycle);
        Assert.Equal(" (gsx.deice.questionTimeoutMinutes)", definition.TimeoutReasonSuffix);
    }

    [Fact]
    public void AskVerdict_RaisesTheQuestionOnce_AndKeepsItOnReEvaluation()
    {
        var (store, _) = Make();
        var raised = 0;
        store.QuestionRaised += (_, _) => raised++;

        store.Publish(DeicePolicyVerdict.Ask, "snow", -2, "snow", "EGLL", "Request it?");
        store.Publish(DeicePolicyVerdict.Ask, "snow", -2, "snow", "EGLL", "Request it?");

        Assert.Equal(1, raised);
        Assert.NotNull(store.Snapshot().Question);
        Assert.True(store.Snapshot().QuestionOpen(DateTimeOffset.UtcNow));
        Assert.False(store.Snapshot().RequestThisCycle);
        Assert.Equal(1, store.Snapshot().Question!.Attempt);
    }

    [Fact]
    public void RequestVerdict_MarksTheCycle_WithoutAQuestion_AndWithoutAnAnsweredEdge()
    {
        var (store, _) = Make();
        var answered = 0;
        store.Answered += (_, _) => answered++;
        store.Publish(DeicePolicyVerdict.Request, "snow", -2, "snow", "EGLL", "Request it?");

        Assert.True(store.Snapshot().RequestThisCycle);
        Assert.Null(store.Snapshot().Question);
        // The policy's own decision, not the captain's answer: no gsx-deice-policy line.
        Assert.Equal(0, answered);
        Assert.Equal("policy", store.Snapshot().Asking.Answer?.Source);
    }

    [Fact]
    public void Accept_AnswersTheQuestion_AndALaterVerdictCannotUndoIt()
    {
        var (store, _) = Make();
        (bool Accepted, string Source)? answered = null;
        store.Answered += (_, a) => answered = a;
        store.Publish(DeicePolicyVerdict.Ask, "snow", -2, "snow", "EGLL", "Request it?");

        store.Accept("voice");
        store.Accept("web"); // idempotent — one Answered edge
        store.Publish(DeicePolicyVerdict.NotRequired, "warmed up", 6, "none", "EGLL", "Request it?");

        Assert.Equal((true, "voice"), answered);
        Assert.True(store.Snapshot().RequestThisCycle);
        Assert.Null(store.Snapshot().Question);
        Assert.Equal(DeicePolicyVerdict.NotRequired, store.Snapshot().Verdict); // the verdict text still updates
    }

    [Fact]
    public void Decline_RecordsTheReason_AndBlocksALaterAsk()
    {
        var (store, _) = Make();
        var raised = 0;
        store.QuestionRaised += (_, _) => raised++;
        store.Publish(DeicePolicyVerdict.Ask, "snow", -2, "snow", "EGLL", "Request it?");
        store.Decline("web", "declined on the Status board");
        store.Publish(DeicePolicyVerdict.Ask, "snow", -2, "snow", "EGLL", "Request it?");

        Assert.Equal(1, raised);
        Assert.Equal("declined on the Status board", store.Snapshot().Declined);
        Assert.Null(store.Snapshot().Question);
    }

    [Fact]
    public void NotRequiredVerdict_WithdrawsAnUnansweredQuestion()
    {
        var (store, _) = Make();
        store.Publish(DeicePolicyVerdict.Ask, "snow", -2, "snow", "EGLL", "Request it?");
        store.Publish(DeicePolicyVerdict.NotRequired, "warmed up", 6, "none", "EGLL", "Request it?");

        Assert.Null(store.Snapshot().Question);
        Assert.Null(store.Snapshot().Declined);
    }

    [Fact]
    public void QuestionTtl_Expires_WithoutAnAnswer()
    {
        var question = Question(DateTimeOffset.UtcNow - DeiceRequestSnapshot.QuestionTtl - TimeSpan.FromSeconds(1));
        var snapshot = DeiceRequestSnapshot.Empty.WithAsking(CaptainsQuestionState.Empty with { Question = question });
        Assert.False(snapshot.QuestionOpen(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void QuestionTtl_ComesFromThePublish_NotTheStaticDefault()
    {
        var (store, _) = Make();
        store.Publish(DeicePolicyVerdict.Ask, "snow", -2, "snow", "EGLL", "Request it?", TimeSpan.FromMinutes(12));

        var question = store.Snapshot().Question!;
        Assert.Equal(TimeSpan.FromMinutes(12), question.Ttl);
        Assert.True(store.Snapshot().QuestionOpen(question.AskedAtUtc + TimeSpan.FromMinutes(11)));
        Assert.False(store.Snapshot().QuestionOpen(question.AskedAtUtc + TimeSpan.FromMinutes(12)));
    }

    /// <summary>The silence rule (probe <c>gsx-deice-policy</c>): the only decline without a
    /// pilot answer is source timeout, at or after expiresAtUtc, reason "no answer within N
    /// minutes". Before C2 the policy service's own timer did this; now the shared sweeper.</summary>
    [Fact]
    public void TtlSweep_DeclinesWithSourceTimeout_AndTheSettingNameInTheReason()
    {
        var (store, _) = Make();
        (bool Accepted, string Source)? answered = null;
        store.Answered += (_, a) => answered = a;
        store.Publish(DeicePolicyVerdict.Ask, "snow", -2, "snow", "EGLL", "Request it?");
        var expires = store.Snapshot().Question!.ExpiresAtUtc!.Value;

        Assert.Equal(CaptainsQuestionExpiry.Never, store.ExpireOverdue(expires - TimeSpan.FromSeconds(1)));
        Assert.Null(answered);

        Assert.Equal(CaptainsQuestionExpiry.CountsAsNo, store.ExpireOverdue(expires));
        Assert.Equal((false, "timeout"), answered);
        Assert.Equal("no answer within 5 minutes (gsx.deice.questionTimeoutMinutes)", store.Snapshot().Declined);
        Assert.Null(store.Snapshot().Question);
    }

    [Fact]
    public void OptionsTtl_FloorsAtOneMinute()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), new GsxDeiceOptions().QuestionTtl);
        Assert.Equal(TimeSpan.FromMinutes(1), new GsxDeiceOptions { QuestionTimeoutMinutes = 0 }.QuestionTtl);
        Assert.Equal(TimeSpan.FromMinutes(8), new GsxDeiceOptions { QuestionTimeoutMinutes = 8 }.QuestionTtl);
    }

    // ---- Issue #166: the step's turn ----

    [Fact]
    public void Empty_IsUndecided_UntilTheStepsTurnOrAVerdict()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(DeiceRequestSnapshot.Empty.Undecided);
        Assert.False((DeiceRequestSnapshot.Empty with { StepNext = true }).Undecided);
        Assert.False((DeiceRequestSnapshot.Empty with { Verdict = DeicePolicyVerdict.Off }).Undecided);
        Assert.False(DeiceRequestSnapshot.Empty.WithAsking(CaptainsQuestionRules.Decline(CaptainsQuestionState.Empty, "voice", "no", now)).Undecided);
    }

    [Fact]
    public void MarkStepNext_ArmsOnce_AndRaisesStepBecameNext()
    {
        var (store, _) = Make();
        var became = 0;
        store.StepBecameNext += (_, _) => became++;

        store.MarkStepNext();
        store.MarkStepNext();

        Assert.True(store.Snapshot().StepNext);
        Assert.Equal(1, became);
    }

    [Fact]
    public void MarkStepNext_ReAsksAnOpenQuestionOnce_WithAFreshTtl()
    {
        var (store, _) = Make();
        var raised = new List<CaptainsQuestion>();
        store.QuestionRaised += (_, q) => raised.Add(q);
        store.Publish(DeicePolicyVerdict.Ask, "snow", -2, "snow", "EGLL", "Request it?", TimeSpan.FromMinutes(5));
        var first = store.Snapshot().Question!;

        store.MarkStepNext();

        Assert.Equal(2, raised.Count);
        Assert.Equal(2, raised[1].Attempt);
        Assert.True(raised[1].AskedAtUtc >= first.AskedAtUtc);
        Assert.Equal(first.Prompt, raised[1].Prompt);
        Assert.Equal(2, store.Snapshot().Question!.Attempt);

        // Idempotent: a second mark never asks a third time.
        store.MarkStepNext();
        Assert.Equal(2, raised.Count);
    }

    [Fact]
    public void MarkStepNext_WithNoQuestion_RaisesNoQuestion()
    {
        var (store, _) = Make();
        var raised = 0;
        store.QuestionRaised += (_, _) => raised++;

        store.MarkStepNext();

        Assert.Equal(0, raised);
        Assert.True(store.Snapshot().StepNext);
    }

    [Fact]
    public void AQuestionAskedAfterTheStepsTurn_IsAttemptOne_AndNeverReAsked()
    {
        var (store, _) = Make();
        var raised = new List<CaptainsQuestion>();
        store.QuestionRaised += (_, q) => raised.Add(q);

        store.MarkStepNext(); // whenStepIsNext: the policy runs here and asks
        store.Publish(DeicePolicyVerdict.Ask, "snow", -2, "snow", "EGLL", "Request it?");

        Assert.Single(raised);
        Assert.Equal(1, raised[0].Attempt);
    }

    [Fact]
    public void Armed_WhenStepIsNext_OrAfterBoardingComplete_PerOption()
    {
        var pending = DeiceRequestSnapshot.Empty;
        Assert.False(GsxDeiceRequestService.Armed(pending, GsxDeiceOptions.AskWhenStepIsNext, boardingCompleted: true));
        Assert.True(GsxDeiceRequestService.Armed(pending with { StepNext = true }, GsxDeiceOptions.AskWhenStepIsNext, boardingCompleted: false));
        Assert.True(GsxDeiceRequestService.Armed(pending, GsxDeiceOptions.AskWhenAfterBoardingComplete, boardingCompleted: true));
        Assert.False(GsxDeiceRequestService.Armed(pending, GsxDeiceOptions.AskWhenAfterBoardingComplete, boardingCompleted: false));
        Assert.True(GsxDeiceRequestService.Armed(pending with { StepNext = true }, GsxDeiceOptions.AskWhenAfterBoardingComplete, boardingCompleted: false));
        Assert.False(GsxDeiceRequestService.Armed(pending, "typo", boardingCompleted: true)); // unknown reads as whenStepIsNext
    }

    [Fact]
    public void FlightCycleReset_StartsOver()
    {
        var (store, signals) = Make();
        store.Publish(DeicePolicyVerdict.Request, "snow", -2, "snow", "EGLL", "Request it?");
        store.MarkStepNext();
        signals.RaiseFlightCycleReset();

        Assert.Equal(DeiceRequestSnapshot.Empty, store.Snapshot());
    }
}
