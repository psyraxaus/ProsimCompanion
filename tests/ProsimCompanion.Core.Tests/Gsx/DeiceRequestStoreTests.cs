using ProsimCompanion.Core.State;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>The per-departure de-icing store (2026-10-09): one question per cycle, answers
/// are final for the cycle, and the flight-cycle reset starts over.</summary>
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
    public void FlightCycleReset_StartsOver()
    {
        var (store, signals) = Make();
        store.Publish(DeicePolicyVerdict.Request, "snow", -2, "snow", "EGLL", "Request it?");
        signals.RaiseFlightCycleReset();

        Assert.Equal(DeiceRequestSnapshot.Empty, store.Snapshot());
    }
}
