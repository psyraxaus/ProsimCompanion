using ProsimCompanion.Core.State;
using Xunit;

namespace ProsimCompanion.Core.Tests.State;

/// <summary>The turnaround "commence ground services?" question (issue #169) as an adapter of
/// the shared captain's-question store (C2): asked once per cycle, settled by yes/no or by any
/// other start, reset with the flight cycle — and, since C2, declined only by the TTL when
/// nobody answers. The shared rules themselves are tabled in <see cref="CaptainsQuestionStoreTests"/>.</summary>
public sealed class TurnaroundStartStoreTests
{
    private readonly GroundOpsSignals _signals = new();

    [Fact]
    public void Definition_IsYesNo_FinalForTheCycle_WithTheStandardClock()
    {
        var definition = TurnaroundStartStore.QuestionDefinition;
        Assert.Equal("turnaroundStart", definition.Topic);
        Assert.Equal(CaptainsQuestionShape.YesNo, definition.Shape);
        Assert.Equal(TimeSpan.FromMinutes(5), definition.DefaultTtl);
        Assert.Equal(CaptainsQuestionExpiry.CountsAsNo, definition.Expiry);
        Assert.Equal(2, definition.MaxAsks);
        Assert.True(definition.AnswerIsFinalForCycle);
    }

    [Fact]
    public void Ask_RaisesOnce_ThenIsANoOp()
    {
        using var store = new TurnaroundStartStore(_signals);
        var raised = 0;
        store.QuestionRaised += (_, _) => raised++;

        Assert.True(store.Ask("Next plan loaded — commence ground services?"));
        Assert.False(store.Ask("again"));

        Assert.Equal(1, raised);
        Assert.True(store.Snapshot().QuestionStands);
        Assert.Equal("Next plan loaded — commence ground services?", store.Snapshot().Question?.Prompt);
        Assert.Equal(TimeSpan.FromMinutes(5), store.Snapshot().Question?.Ttl);
    }

    [Fact]
    public void Accept_SettlesTheQuestion_AndNamesTheSource()
    {
        using var store = new TurnaroundStartStore(_signals);
        (bool Accepted, string Source)? answer = null;
        store.Answered += (_, a) => answer = a;
        store.Ask("q");

        store.Accept("voice");
        store.Accept("web"); // idempotent — one event

        Assert.Equal((true, "voice"), answer);
        Assert.False(store.Snapshot().QuestionStands);
        Assert.True(store.Snapshot().Accepted);
    }

    [Fact]
    public void Decline_SettlesTheQuestion_AndAskIsNotRepeated()
    {
        using var store = new TurnaroundStartStore(_signals);
        store.Ask("q");

        store.Decline("voice", "captain said 'not yet'");

        Assert.False(store.Snapshot().QuestionStands);
        Assert.Equal("captain said 'not yet'", store.Snapshot().Declined);
        Assert.False(store.Ask("q")); // declined for the cycle
    }

    [Fact]
    public void AcceptWithoutAQuestion_StillRecords_SoAnotherStartCannotBeAskedAgain()
    {
        // The Start button / "commence ground services" before the FO ever asked.
        using var store = new TurnaroundStartStore(_signals);

        store.Accept("web");

        Assert.True(store.Snapshot().Accepted);
        Assert.False(store.Ask("q"));
    }

    /// <summary>C2: the #166 rule reaches the turnaround question — nobody answering is
    /// settled by the clock (source timeout), never by the FO's listen window.</summary>
    [Fact]
    public void NoAnswer_IsDeclinedByTheTtl_WithSourceTimeout()
    {
        using var store = new TurnaroundStartStore(_signals);
        (bool Accepted, string Source)? answer = null;
        store.Answered += (_, a) => answer = a;
        store.Ask("q");
        var asked = store.Snapshot().Question!.AskedAtUtc;

        Assert.Equal(CaptainsQuestionExpiry.Never, store.ExpireOverdue(asked + TimeSpan.FromMinutes(4)));
        Assert.Null(answer);

        Assert.Equal(CaptainsQuestionExpiry.CountsAsNo, store.ExpireOverdue(asked + TimeSpan.FromMinutes(5)));
        Assert.Equal((false, "timeout"), answer);
        Assert.Equal("no answer within 5 minutes", store.Snapshot().Declined);
    }

    [Fact]
    public void FlightCycleReset_ClearsEverything()
    {
        using var store = new TurnaroundStartStore(_signals);
        store.Ask("q");
        store.Decline("voice", "no");

        _signals.RaiseFlightCycleReset();

        Assert.Equal(CaptainsQuestionState.Empty, store.Snapshot());
        Assert.True(store.Ask("q"));
    }
}
