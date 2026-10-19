using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.State;
using Xunit;

namespace ProsimCompanion.Core.Tests.State;

/// <summary>
/// The one captain's-question store (architecture review C2): once per cycle, same-subject
/// idempotence, the TTL verdict per expiry rule, the single re-ask, yes overrides no, the
/// pick-one match, the flight-cycle reset, and the Raised/Answered edges — the rules the four
/// typed adapters (de-icing, turnaround start, gate suffix, pushback) inherit. The #166 rules
/// ("silence is not an answer", the TTL as the only clock that settles) are pinned here once.
/// </summary>
public sealed class CaptainsQuestionStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 10, 0, 0, TimeSpan.Zero);

    private static readonly CaptainsQuestionDefinition YesNo = new(
        "test", CaptainsQuestionShape.YesNo, TimeSpan.FromMinutes(5), CaptainsQuestionExpiry.CountsAsNo,
        CaptainsQuestionDefinition.StandardMaxAsks, AnswerIsFinalForCycle: true, TimeoutReasonSuffix: " (test.timeout)");

    private static readonly CaptainsQuestionDefinition PickOne = new(
        "pick", CaptainsQuestionShape.PickOne, null, CaptainsQuestionExpiry.Never,
        CaptainsQuestionDefinition.StandardMaxAsks, AnswerIsFinalForCycle: false);

    private static readonly CaptainsQuestionDefinition PickOneWithClock = PickOne with
    {
        Topic = "pickClock", DefaultTtl = TimeSpan.FromMinutes(5), Expiry = CaptainsQuestionExpiry.Withdraws,
    };

    private static readonly IReadOnlyList<QuestionChoice> Variants =
        [new("36A", "Gate W 36A", null), new("36B", "Gate W 36B", "Safedock")];

    /// <summary>The smallest adapter: the shared state is the snapshot, the raiser is <c>Ask</c>.</summary>
    private sealed class TestStore(CaptainsQuestionDefinition definition, GroundOpsSignals signals)
        : CaptainsQuestionStore<CaptainsQuestionState>(definition, CaptainsQuestionState.Empty, signals)
    {
        public CaptainsQuestion? Ask(string prompt, IReadOnlyList<QuestionChoice>? choices = null, string? subject = null, TimeSpan? ttl = null)
            => Raise(prompt, choices, subject, ttl);
    }

    // ---- once per cycle (yes/no) ----

    [Fact]
    public void Ask_RaisesOnce_ThenIsANoOp_WhileItStands()
    {
        using var store = new TestStore(YesNo, new GroundOpsSignals());
        var raised = 0;
        store.QuestionRaised += (_, _) => raised++;

        Assert.NotNull(store.Ask("Request it?"));
        Assert.Null(store.Ask("again"));

        Assert.Equal(1, raised);
        Assert.True(store.Asking.QuestionStands);
        Assert.Equal(1, store.Asking.Question!.Attempt);
        Assert.Equal(TimeSpan.FromMinutes(5), store.Asking.Question.Ttl); // the definition's default
    }

    [Fact]
    public void Ask_CarriesItsOwnTtl_WhenTheRaiserPassesOne()
    {
        using var store = new TestStore(YesNo, new GroundOpsSignals());
        var question = store.Ask("Request it?", ttl: TimeSpan.FromMinutes(12))!;

        Assert.Equal(TimeSpan.FromMinutes(12), question.Ttl);
        Assert.True(question.OpenAt(question.AskedAtUtc + TimeSpan.FromMinutes(11)));
        Assert.False(question.OpenAt(question.AskedAtUtc + TimeSpan.FromMinutes(12)));
        Assert.Equal(question.AskedAtUtc + TimeSpan.FromMinutes(12), question.ExpiresAtUtc);
    }

    [Fact]
    public void AnswerIsFinalForCycle_BlocksALaterAsk_UntilTheCycleResets()
    {
        var signals = new GroundOpsSignals();
        using var store = new TestStore(YesNo, signals);
        store.Ask("q");
        store.Decline("voice", "captain said 'not yet'");

        Assert.Null(store.Ask("q")); // declined for the cycle
        Assert.Equal("captain said 'not yet'", store.Asking.Declined);

        signals.RaiseFlightCycleReset();

        Assert.Equal(CaptainsQuestionState.Empty, store.Snapshot());
        Assert.NotNull(store.Ask("q"));
    }

    [Fact]
    public void AcceptWithoutAQuestion_StillRecords_SoTheQuestionIsNeverAskedThisCycle()
    {
        // The Start button / "commence ground services" before the FO ever asked (issue #169).
        using var store = new TestStore(YesNo, new GroundOpsSignals());

        Assert.True(store.Accept("web"));

        Assert.True(store.Asking.Accepted);
        Assert.Null(store.Ask("q"));
    }

    // ---- accept / decline edges ----

    [Fact]
    public void Accept_SettlesTheQuestion_NamesTheSource_AndIsIdempotent()
    {
        using var store = new TestStore(YesNo, new GroundOpsSignals());
        var answers = new List<(bool Accepted, string Source)>();
        store.Answered += (_, a) => answers.Add(a);
        store.Ask("q");

        Assert.True(store.Accept("voice"));
        Assert.False(store.Accept("web")); // idempotent — one event

        Assert.Equal([(true, "voice")], answers);
        Assert.False(store.Asking.QuestionStands);
        Assert.True(store.Asking.Accepted);
        Assert.Equal("voice", store.Asking.Answer!.Source);
    }

    [Fact]
    public void Decline_RecordsTheReason_AndNeverOverridesAYes_ButAYesOverridesANo()
    {
        using var store = new TestStore(YesNo, new GroundOpsSignals());
        var answers = new List<(bool Accepted, string Source)>();
        store.Answered += (_, a) => answers.Add(a);
        store.Ask("q");

        Assert.True(store.Decline("web", "declined on the Status board"));
        Assert.False(store.Decline("voice", "again"));
        Assert.Equal("declined on the Status board", store.Asking.Declined);

        // "request de-icing" after "no": the captain changed their mind before the truck came.
        Assert.True(store.Accept("voice"));
        Assert.Null(store.Asking.Declined);
        Assert.True(store.Asking.Accepted);
        Assert.False(store.Decline("web", "too late"));

        Assert.Equal([(false, "web"), (true, "voice")], answers);
    }

    // ---- the TTL (#166) ----

    [Fact]
    public void ExpireOverdue_CountsAsNo_ForAYesNoQuestion_WithTheSettingNameInTheReason()
    {
        using var store = new TestStore(YesNo, new GroundOpsSignals());
        (bool Accepted, string Source)? answer = null;
        store.Answered += (_, a) => answer = a;
        var question = store.Ask("q")!;

        Assert.Equal(CaptainsQuestionExpiry.Never, store.ExpireOverdue(question.AskedAtUtc + TimeSpan.FromMinutes(4)));
        Assert.True(store.Asking.QuestionStands);
        Assert.Null(answer);

        Assert.Equal(CaptainsQuestionExpiry.CountsAsNo, store.ExpireOverdue(question.AskedAtUtc + TimeSpan.FromMinutes(5)));
        Assert.Equal((false, "timeout"), answer);
        Assert.Equal("no answer within 5 minutes (test.timeout)", store.Asking.Declined);
        Assert.False(store.Asking.QuestionStands);
        Assert.Equal(CaptainsQuestionExpiry.Never, store.ExpireOverdue(question.AskedAtUtc + TimeSpan.FromMinutes(9)));
    }

    [Fact]
    public void ExpireOverdue_Withdraws_APickOneQuestion_WithoutAnAnswer()
    {
        using var store = new TestStore(PickOneWithClock, new GroundOpsSignals());
        var answered = 0;
        store.Answered += (_, _) => answered++;
        var question = store.Ask("which?", Variants)!;

        Assert.Equal(CaptainsQuestionExpiry.Withdraws, store.ExpireOverdue(question.AskedAtUtc + TimeSpan.FromMinutes(5)));

        Assert.False(store.Asking.QuestionStands);
        Assert.Null(store.Asking.Answer);
        Assert.Equal(0, answered);
        Assert.NotNull(store.Ask("which?", Variants)); // not final for the cycle: the next menu asks again
    }

    [Fact]
    public void AQuestionWithoutATtl_StandsUntilTheRaiserSettlesIt()
    {
        // The arrival gate: probe gsx-gate-suffix asserts no default before the TaxiIn edge.
        using var store = new TestStore(PickOne, new GroundOpsSignals());
        var question = store.Ask("which?", Variants)!;

        Assert.Null(question.Ttl);
        Assert.Null(question.ExpiresAtUtc);
        Assert.True(store.Asking.QuestionOpen(T0.AddYears(1)));
        Assert.Equal(CaptainsQuestionExpiry.Never, store.ExpireOverdue(T0.AddYears(1)));
        Assert.True(store.Asking.QuestionStands);
    }

    [Fact]
    public void QuestionOpen_IsFalse_OncePastTheTtl_EvenBeforeTheSweep()
    {
        var question = new CaptainsQuestion("test", T0, "q", TimeSpan.FromMinutes(5), 1, [], null);
        var state = CaptainsQuestionState.Empty with { Question = question };

        Assert.True(state.QuestionOpen(T0.AddMinutes(4)));
        Assert.False(state.QuestionOpen(T0.AddMinutes(5)));
        Assert.True(state.QuestionStands); // the sweep has not run yet
    }

    // ---- the single re-ask (#166) ----

    [Fact]
    public void ReAsk_RaisesAttemptTwo_WithAFreshClock_AndNeverAThirdTime()
    {
        using var store = new TestStore(YesNo, new GroundOpsSignals());
        var raised = new List<CaptainsQuestion>();
        store.QuestionRaised += (_, q) => raised.Add(q);
        var first = store.Ask("q")!;

        var again = store.ReAsk();

        Assert.NotNull(again);
        Assert.Equal(2, again!.Attempt);
        Assert.True(again.AskedAtUtc >= first.AskedAtUtc);
        Assert.Equal(first.Prompt, again.Prompt);
        Assert.Equal(first.Ttl, again.Ttl);
        Assert.Equal(2, raised.Count);
        Assert.Equal(2, store.Asking.Question!.Attempt);

        Assert.Null(store.ReAsk()); // MaxAsks = 2
        Assert.Equal(2, raised.Count);
    }

    [Fact]
    public void ReAsk_WithNoQuestion_OrAnExpiredOne_RaisesNothing()
    {
        using var store = new TestStore(YesNo, new GroundOpsSignals());
        Assert.Null(store.ReAsk());

        var expired = CaptainsQuestionState.Empty with { Question = new CaptainsQuestion("test", T0, "q", TimeSpan.FromMinutes(5), 1, [], null) };
        Assert.Same(expired, CaptainsQuestionRules.ReAsk(expired, YesNo, T0.AddMinutes(6)));
    }

    // ---- pick one ----

    [Theory]
    [InlineData("36B", "36B")]
    [InlineData("36 b", "36B")]
    [InlineData("gate w 36a", "36A")]
    [InlineData("Gate W 36B", "36B")]
    public void Choose_MatchesLabelOrToken(string answer, string expectedLabel)
    {
        using var store = new TestStore(PickOne, new GroundOpsSignals());
        (bool Accepted, string Source)? answered = null;
        store.Answered += (_, a) => answered = a;
        store.Ask("which?", Variants, subject: "W36");

        Assert.True(store.Choose(answer, "web"));

        var state = store.Asking;
        Assert.False(state.QuestionStands);
        Assert.Equal(expectedLabel, state.Chosen?.Label);
        Assert.Equal("web", state.Answer?.Source);
        Assert.Equal("W36", state.Answer?.Subject);
        Assert.True(state.Accepted);
        Assert.Equal((true, "web"), answered);
    }

    [Fact]
    public void Choose_UnknownOrNoQuestion_IsFalse_AndLeavesTheQuestionStanding()
    {
        using var store = new TestStore(PickOne, new GroundOpsSignals());
        Assert.False(store.Choose("36A", "web"));

        store.Ask("which?", Variants);
        Assert.False(store.Choose("36C", "web"));
        Assert.True(store.Asking.QuestionStands);
    }

    [Fact]
    public void Ask_SameSubjectIsANoOp_AnotherSubjectReplacesTheStandingQuestion()
    {
        using var store = new TestStore(PickOne, new GroundOpsSignals());
        var raised = new List<CaptainsQuestion>();
        store.QuestionRaised += (_, q) => raised.Add(q);

        store.Ask("W36?", Variants, subject: "W36");
        store.Ask("W36?", Variants, subject: "w36");
        Assert.Single(raised);

        store.Ask("W40?", Variants, subject: "W40");
        Assert.Equal(2, raised.Count);
        Assert.Equal("W40", store.Asking.Question?.Subject);
    }

    [Fact]
    public void PickOne_NotFinalForTheCycle_AsksAgainAfterAnAnswer_AndClearsTheOldAnswer()
    {
        using var store = new TestStore(PickOne, new GroundOpsSignals());
        store.Ask("which?", Variants, subject: "W36");
        store.Choose("36A", "voice");

        Assert.NotNull(store.Ask("which?", Variants, subject: "W36"));
        Assert.Null(store.Asking.Answer);
    }

    [Fact]
    public void PickOne_WithoutChoices_IsRefused()
    {
        using var store = new TestStore(PickOne, new GroundOpsSignals());
        Assert.Throws<ArgumentException>(() => store.Ask("which?", []));
    }

    // ---- withdraw / reset / change relay ----

    [Fact]
    public void Withdraw_DropsTheQuestion_WithoutAnAnswer_AndIsSilentWhenNoneStands()
    {
        using var store = new TestStore(PickOne, new GroundOpsSignals());
        var changed = 0;
        store.Changed += () => changed++;
        store.Ask("which?", Variants);
        var afterAsk = changed;

        store.Withdraw();
        Assert.False(store.Asking.QuestionStands);
        Assert.Null(store.Asking.Answer);
        Assert.Equal(afterAsk + 1, changed);

        store.Withdraw();
        Assert.Equal(afterAsk + 1, changed); // no snapshot change, no relay
    }

    [Fact]
    public void FlightCycleReset_ClearsEverything_AndDisposeUnsubscribes()
    {
        var signals = new GroundOpsSignals();
        var store = new TestStore(PickOne, signals);
        store.Ask("which?", Variants);
        store.Choose("36A", "voice");

        signals.RaiseFlightCycleReset();
        Assert.Equal(CaptainsQuestionState.Empty, store.Snapshot());

        store.Ask("which?", Variants);
        store.Dispose();
        signals.RaiseFlightCycleReset();
        Assert.True(store.Asking.QuestionStands); // no longer listening
    }

    // ---- the sweeper ----

    [Fact]
    public void Sweeper_SettlesEveryOverdueQuestion_PerItsOwnRule()
    {
        var signals = new GroundOpsSignals();
        using var yesNo = new TestStore(YesNo, signals);
        using var pick = new TestStore(PickOneWithClock, signals);
        using var forever = new TestStore(PickOne, signals);
        var asked = yesNo.Ask("q")!.AskedAtUtc;
        pick.Ask("which?", Variants);
        forever.Ask("which?", Variants);
        using var sweeper = new CaptainsQuestionTtlSweeper([yesNo, pick, forever], NullLogger<CaptainsQuestionTtlSweeper>.Instance);

        sweeper.Sweep(asked + TimeSpan.FromMinutes(1));
        Assert.True(yesNo.Asking.QuestionStands);
        Assert.True(pick.Asking.QuestionStands);

        sweeper.Sweep(asked + TimeSpan.FromMinutes(6));
        Assert.Equal("timeout", yesNo.Asking.Answer?.Source);
        Assert.False(pick.Asking.QuestionStands);
        Assert.Null(pick.Asking.Answer);
        Assert.True(forever.Asking.QuestionStands);
    }

    [Fact]
    public void Sweeper_AThrowingSubscriber_DoesNotStopTheOtherClocks()
    {
        var signals = new GroundOpsSignals();
        using var first = new TestStore(YesNo, signals);
        using var second = new TestStore(YesNo with { Topic = "second" }, signals);
        first.Answered += (_, _) => throw new InvalidOperationException("boom");
        var asked = first.Ask("q")!.AskedAtUtc;
        second.Ask("q");
        using var sweeper = new CaptainsQuestionTtlSweeper([first, second], NullLogger<CaptainsQuestionTtlSweeper>.Instance);

        sweeper.Sweep(asked + TimeSpan.FromMinutes(6));

        Assert.Equal("timeout", first.Asking.Answer?.Source); // the state changed before the subscriber threw
        Assert.Equal("timeout", second.Asking.Answer?.Source);
    }

    // ---- the pure rules, tabled ----

    [Fact]
    public void Rules_ReturnTheSameInstance_WhenNothingChanges()
    {
        var empty = CaptainsQuestionState.Empty;
        Assert.Same(empty, CaptainsQuestionRules.Withdraw(empty));
        Assert.Same(empty, CaptainsQuestionRules.ReAsk(empty, YesNo, T0));
        Assert.Same(empty, CaptainsQuestionRules.Choose(empty, "36A", "web", T0).State);
        Assert.Same(empty, CaptainsQuestionRules.Expire(empty, YesNo, T0).State);

        var accepted = CaptainsQuestionRules.Accept(empty, "web", T0);
        Assert.Same(accepted, CaptainsQuestionRules.Accept(accepted, "voice", T0));
        Assert.Same(accepted, CaptainsQuestionRules.Decline(accepted, "voice", "no", T0));
        Assert.Same(accepted, CaptainsQuestionRules.Ask(accepted, YesNo, "q", [], null, null, T0));
    }

    [Fact]
    public void Rules_Expire_IgnoresAQuestionStillInsideItsTtl_AndOneWithoutATtl()
    {
        var inside = CaptainsQuestionState.Empty with { Question = new CaptainsQuestion("test", T0, "q", TimeSpan.FromMinutes(5), 1, [], null) };
        Assert.Equal(CaptainsQuestionExpiry.Never, CaptainsQuestionRules.Expire(inside, YesNo, T0.AddMinutes(4)).Outcome);

        var forever = CaptainsQuestionState.Empty with { Question = new CaptainsQuestion("pick", T0, "q", null, 1, Variants, "W36") };
        Assert.Equal(CaptainsQuestionExpiry.Never, CaptainsQuestionRules.Expire(forever, PickOne, T0.AddDays(1)).Outcome);
    }
}
