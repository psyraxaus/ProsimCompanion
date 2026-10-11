using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Automation;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>The per-departure de-icing store (2026-10-09): one question per cycle, answers
/// are final for the cycle, and the flight-cycle reset starts over. Issue #166 (2026-10-11):
/// the step's turn arms the policy and re-asks an open question once; the question carries
/// its own TTL; nothing but the TTL declines on silence.</summary>
public sealed class DeiceRequestStoreTests
{
    private static (DeiceRequestStore Store, GroundOpsSignals Signals) Make()
    {
        var signals = new GroundOpsSignals();
        return (new DeiceRequestStore(signals), signals);
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
    public void RequestVerdict_MarksTheCycle_WithoutAQuestion()
    {
        var (store, _) = Make();
        store.Publish(DeicePolicyVerdict.Request, "snow", -2, "snow", "EGLL", "Request it?");

        Assert.True(store.Snapshot().RequestThisCycle);
        Assert.Null(store.Snapshot().Question);
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
        var question = new DeiceQuestion(DateTimeOffset.UtcNow - DeiceRequestSnapshot.QuestionTtl - TimeSpan.FromSeconds(1), "Request it?");
        var snapshot = DeiceRequestSnapshot.Empty with { Question = question };
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
        Assert.True(DeiceRequestSnapshot.Empty.Undecided);
        Assert.False((DeiceRequestSnapshot.Empty with { StepNext = true }).Undecided);
        Assert.False((DeiceRequestSnapshot.Empty with { Verdict = DeicePolicyVerdict.Off }).Undecided);
        Assert.False((DeiceRequestSnapshot.Empty with { Declined = "no" }).Undecided);
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
        var raised = new List<DeiceQuestion>();
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
        var raised = new List<DeiceQuestion>();
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
