using ProsimCompanion.Core.Checklists;
using ProsimCompanion.Speech.Checklists;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>The spoken checklist's read-through of already-set lines (owner request
/// 2026-10-18): only a plain verify line with every dataref live and the condition true is
/// read through; a missing value fails closed so the catalog default never passes a line.</summary>
public sealed class SatisfiedLineReadThroughTests
{
    private static ChecklistItemDefinition Flaps(string behavior = "verify", string expects = "none") => new()
    {
        Behavior = behavior,
        Say = "Flaps",
        ExpectedResponse = "RETRACTED",
        AcceptedPhrases = ["retracted", "up"],
        Expects = expects,
        Verify = new VerifyCondition { Dataref = "system.switches.S_FC_FLAPS", Op = ComparisonOp.Equals, Value = 0 },
        ConfirmCallout = "Flaps, retracted",
    };

    private static Func<string, double?> Live(params (string Name, double? Value)[] values)
        => name => values.FirstOrDefault(v => v.Name == name).Value;

    [Fact]
    public void ConditionTrueOnLiveValue_IsSatisfied()
        => Assert.True(SatisfiedLineReadThrough.IsSatisfied(Flaps(), Live(("system.switches.S_FC_FLAPS", 0))));

    [Fact]
    public void ConditionFalse_IsNotSatisfied()
        => Assert.False(SatisfiedLineReadThrough.IsSatisfied(Flaps(), Live(("system.switches.S_FC_FLAPS", 2))));

    [Fact]
    public void NoValueYet_FailsClosed_EvenWhenDefaultWouldMatch()
    {
        // The subscription's fallback is 0.0 and the line wants 0 — without this guard the
        // line would pass before ProSim ever answered.
        Assert.False(SatisfiedLineReadThrough.IsSatisfied(Flaps(), Live(("system.switches.S_FC_FLAPS", null))));
        Assert.False(SatisfiedLineReadThrough.IsSatisfied(Flaps(), Live()));
    }

    [Fact]
    public void CompoundCondition_OneStaleLeaf_FailsClosed()
    {
        var item = Flaps();
        item.Verify = new VerifyCondition
        {
            Logic = ConditionLogic.And,
            Conditions =
            [
                new VerifyCondition { Dataref = "a", Op = ComparisonOp.Equals, Value = 1 },
                new VerifyCondition { Dataref = "b", Op = ComparisonOp.Equals, Value = 1 },
            ],
        };

        Assert.True(SatisfiedLineReadThrough.IsSatisfied(item, Live(("a", 1), ("b", 1))));
        Assert.False(SatisfiedLineReadThrough.IsSatisfied(item, Live(("a", 1), ("b", null))));
    }

    [Theory]
    [InlineData("acknowledge", "none")]
    [InlineData("verify", "number")]
    [InlineData("verify", "flapConfig")]
    public void NonVerifyOrReadbackLines_NeverReadThrough(string behavior, string expects)
        => Assert.False(SatisfiedLineReadThrough.IsSatisfied(
            Flaps(behavior, expects), Live(("system.switches.S_FC_FLAPS", 0))));

    [Fact]
    public void VerifyWithoutCondition_NeverReadThrough()
    {
        var item = Flaps();
        item.Verify = null;
        Assert.False(SatisfiedLineReadThrough.IsSatisfied(item, Live(("system.switches.S_FC_FLAPS", 0))));
    }

    [Fact]
    public void Text_PrefersConfirmCallout()
        => Assert.Equal("Flaps, retracted", SatisfiedLineReadThrough.Text(Flaps()));

    [Fact]
    public void Text_FallsBackToChallengeAndLowerCaseResponse()
    {
        var item = Flaps();
        item.ConfirmCallout = null;
        Assert.Equal("Flaps, retracted", SatisfiedLineReadThrough.Text(item));

        item.ExpectedResponse = "  ";
        Assert.Equal("Flaps, checked", SatisfiedLineReadThrough.Text(item));
    }
}
