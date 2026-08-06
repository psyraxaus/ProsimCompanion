using ProsimCompanion.Core.Checklists;
using Xunit;

namespace ProsimCompanion.Core.Tests.Checklists;

public sealed class ChecklistRunnerTests
{
    private static ChecklistItemDefinition Auto(string say, string dataref, double expected, bool freeze = false)
        => new()
        {
            Behavior = "verify",
            Say = say,
            Verify = new VerifyCondition { Dataref = dataref, Op = ComparisonOp.Equals, Value = expected },
            Freeze = freeze,
        };

    private static ChecklistItemDefinition Manual(string say)
        => new() { Behavior = "acknowledge", Say = say };

    private static ChecklistDefinition Definition(params ChecklistItemDefinition[] items)
        => new() { Checklist = "Test", Items = [.. items] };

    private static Func<string, double> Reads(Dictionary<string, double> values)
        => name => values.TryGetValue(name, out var value) ? value : 0.0;

    [Fact]
    public void Gating_ManualItemBlocksLaterAutoItems_EvenWhenSatisfied()
    {
        var runner = new ChecklistRunner(Definition(Manual("first"), Auto("beacon", "beacon", 1)));
        var values = new Dictionary<string, double> { ["beacon"] = 1 };

        runner.Evaluate(Reads(values));

        var view = runner.Snapshot();
        Assert.Equal(ChecklistItemStatus.Active, view.Items[0].Status);
        Assert.Equal(ChecklistItemStatus.Pending, view.Items[1].Status); // gated behind the manual line
        Assert.True(view.Items[1].ConditionSatisfied);                    // but the live hint shows ready

        runner.Check(0);
        runner.Evaluate(Reads(values));
        Assert.True(runner.IsComplete); // cascade: manual done → auto satisfied → complete
    }

    [Fact]
    public void Gating_CascadesThroughConsecutiveSatisfiedAutoItems()
    {
        var runner = new ChecklistRunner(Definition(Auto("a", "a", 1), Auto("b", "b", 1), Manual("c")));
        runner.Evaluate(Reads(new() { ["a"] = 1, ["b"] = 1 }));

        var view = runner.Snapshot();
        Assert.Equal(ChecklistItemStatus.Done, view.Items[0].Status);
        Assert.Equal(ChecklistItemStatus.Done, view.Items[1].Status);
        Assert.Equal(ChecklistItemStatus.Active, view.Items[2].Status);
    }

    [Fact]
    public void Retreat_RegressedConditionReopensItsLine_LaterItemsKeepTheirState()
    {
        var runner = new ChecklistRunner(Definition(Auto("beacon", "beacon", 1), Manual("doors")));
        var values = new Dictionary<string, double> { ["beacon"] = 1 };
        runner.Evaluate(Reads(values));
        runner.Check(1); // manual done — checklist would complete on next evaluate, but first:

        values["beacon"] = 0; // beacon switched back off before completion evaluation
        runner.Evaluate(Reads(values));

        var view = runner.Snapshot();
        Assert.Equal(ChecklistItemStatus.Active, view.Items[0].Status); // reopened + active again
        Assert.Equal(ChecklistItemStatus.Done, view.Items[1].Status);   // manual line untouched
        Assert.False(runner.IsComplete);

        values["beacon"] = 1;
        runner.Evaluate(Reads(values));
        Assert.True(runner.IsComplete); // cursor jumps over the already-done manual line
    }

    [Fact]
    public void Freeze_FrozenAutoItemDoesNotRetreat()
    {
        var runner = new ChecklistRunner(Definition(Auto("gear", "gear", 1, freeze: true), Manual("flaps")));
        var values = new Dictionary<string, double> { ["gear"] = 1 };
        runner.Evaluate(Reads(values));

        values["gear"] = 0;
        runner.Evaluate(Reads(values));

        Assert.Equal(ChecklistItemStatus.Done, runner.Snapshot().Items[0].Status);
    }

    [Fact]
    public void Freeze_CompletedChecklistFreezesWholesale()
    {
        var runner = new ChecklistRunner(Definition(Auto("beacon", "beacon", 1)));
        var values = new Dictionary<string, double> { ["beacon"] = 1 };
        runner.Evaluate(Reads(values));
        Assert.True(runner.IsComplete);

        values["beacon"] = 0;
        runner.Evaluate(Reads(values));
        Assert.True(runner.IsComplete);
        Assert.Equal(ChecklistItemStatus.Done, runner.Snapshot().Items[0].Status);
    }

    [Fact]
    public void AutoItemsCannotBeHandTicked_ButCanBeSkipped()
    {
        var runner = new ChecklistRunner(Definition(Auto("beacon", "beacon", 1)));
        runner.Evaluate(Reads([]));

        Assert.False(runner.Check(0));
        Assert.True(runner.Skip(0));
        runner.Evaluate(Reads([]));
        Assert.True(runner.IsComplete); // skipped items don't block completion
    }

    [Fact]
    public void Restart_ClearsEverything()
    {
        var runner = new ChecklistRunner(Definition(Manual("a"), Manual("b")));
        runner.Evaluate(Reads([]));
        runner.Check(0);
        runner.Evaluate(Reads([]));
        runner.Check(1);
        runner.Evaluate(Reads([]));
        Assert.True(runner.IsComplete);

        runner.Restart();
        runner.Evaluate(Reads([]));
        Assert.False(runner.IsComplete);
        Assert.Equal(ChecklistItemStatus.Active, runner.Snapshot().Items[0].Status);
    }
}

public sealed class ConditionEvaluatorTests
{
    private static double Read(string name) => name switch
    {
        "one" => 1.0,
        "half" => 0.5,
        "big" => 100.0,
        _ => 0.0,
    };

    [Fact]
    public void Leaf_Operators_MatchPredecessorSemantics()
    {
        Assert.True(ConditionEvaluator.Evaluate(new VerifyCondition { Dataref = "one", Op = ComparisonOp.Equals, Value = 1 }, Read));
        Assert.True(ConditionEvaluator.Evaluate(new VerifyCondition { Dataref = "one", Op = ComparisonOp.NotEquals, Value = 0 }, Read));
        Assert.True(ConditionEvaluator.Evaluate(new VerifyCondition { Dataref = "big", Op = ComparisonOp.GreaterThan, Value = 99 }, Read));
        Assert.True(ConditionEvaluator.Evaluate(new VerifyCondition { Dataref = "half", Op = ComparisonOp.LessThan, Value = 1 }, Read));
        Assert.True(ConditionEvaluator.Evaluate(new VerifyCondition { Dataref = "half", Op = ComparisonOp.Between, Low = 0.5, High = 0.5 }, Read)); // inclusive
        Assert.True(ConditionEvaluator.Evaluate(new VerifyCondition { Dataref = "one", Op = ComparisonOp.OneOf, Values = [0, 1] }, Read));
        Assert.False(ConditionEvaluator.Evaluate(new VerifyCondition { Dataref = "one", Op = ComparisonOp.OneOf, Values = [2, 3] }, Read));
    }

    [Fact]
    public void Compound_DefaultsToAnd_OrNeedsExplicitLogic()
    {
        var and = new VerifyCondition
        {
            Conditions =
            [
                new VerifyCondition { Dataref = "one", Op = ComparisonOp.Equals, Value = 1 },
                new VerifyCondition { Dataref = "half", Op = ComparisonOp.Equals, Value = 1 },
            ],
        };
        Assert.False(ConditionEvaluator.Evaluate(and, Read));

        and.Logic = ConditionLogic.Or;
        Assert.True(ConditionEvaluator.Evaluate(and, Read));
    }

    [Fact]
    public void MalformedLeaf_IsFalse_NotAnException()
    {
        Assert.False(ConditionEvaluator.Evaluate(new VerifyCondition { Dataref = "one" }, Read)); // no op
        Assert.False(ConditionEvaluator.Evaluate(new VerifyCondition { Op = ComparisonOp.Equals, Value = 1 }, Read)); // no dataref
    }
}
