using ProsimCompanion.Core.Checklists;
using ProsimCompanion.Core.Commands;
using ProsimCompanion.Core.Commands.Handlers;
using Xunit;

namespace ProsimCompanion.Core.Tests.Commands;

public sealed class ChecklistCommandHandlerTests
{
    private static ChecklistItemView Manual(string label)
        => new(label, "CHECKED", ChecklistItemStatus.Pending, IsAuto: false, ConditionSatisfied: false);

    private static ChecklistItemView Auto(string label)
        => new(label, "ON", ChecklistItemStatus.Pending, IsAuto: true, ConditionSatisfied: false);

    private static ChecklistView View(int activeIndex, bool complete, params ChecklistItemView[] items)
        => new("Before Start", complete, activeIndex, [.. items]);

    [Fact]
    public void EvaluateCheck_MissingIndex_IsValidationError()
        => Assert.Throws<CommandValidationException>(() =>
            ChecklistCommandHandlers.EvaluateCheck(View(0, false, Manual("a")), null));

    [Fact]
    public void EvaluateCheck_NoActiveChecklist_IsPreconditionFailed()
    {
        var result = ChecklistCommandHandlers.EvaluateCheck(null, 0);
        Assert.Equal(CommandOutcome.PreconditionFailed, result.Outcome);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(100)]
    public void EvaluateCheck_IndexOutOfBounds_IsValidationError(int index)
        => Assert.Throws<CommandValidationException>(() =>
            ChecklistCommandHandlers.EvaluateCheck(View(0, false, Manual("a"), Manual("b")), index));

    [Fact]
    public void EvaluateCheck_NotTheActiveLine_IsPreconditionFailed()
    {
        var result = ChecklistCommandHandlers.EvaluateCheck(View(0, false, Manual("a"), Manual("b")), 1);
        Assert.Equal(CommandOutcome.PreconditionFailed, result.Outcome);
    }

    [Fact]
    public void EvaluateCheck_AutoItem_IsPreconditionFailed_ChecklistsMustNotLie()
    {
        var result = ChecklistCommandHandlers.EvaluateCheck(View(0, false, Auto("beacon")), 0);
        Assert.Equal(CommandOutcome.PreconditionFailed, result.Outcome);
    }

    [Fact]
    public void EvaluateCheck_CompletedChecklist_IsAlreadySatisfied()
    {
        var result = ChecklistCommandHandlers.EvaluateCheck(View(1, true, Manual("a")), 0);
        Assert.Equal(CommandOutcome.AlreadySatisfied, result.Outcome);
    }

    [Fact]
    public void EvaluateCheck_ManualActiveLine_Succeeds()
    {
        var result = ChecklistCommandHandlers.EvaluateCheck(View(0, false, Manual("a"), Manual("b")), 0);
        Assert.Equal(CommandOutcome.Success, result.Outcome);
    }

    [Fact]
    public void EvaluateSkip_AutoActiveLine_Succeeds_SkipIsTheEscapeHatch()
    {
        var result = ChecklistCommandHandlers.EvaluateSkip(View(0, false, Auto("beacon")), 0);
        Assert.Equal(CommandOutcome.Success, result.Outcome);
    }

    [Fact]
    public void EvaluateSkip_IndexOutOfBounds_IsValidationError()
        => Assert.Throws<CommandValidationException>(() =>
            ChecklistCommandHandlers.EvaluateSkip(View(0, false, Manual("a")), 5));

    [Fact]
    public void EvaluateAdvanceNext_NoActiveChecklist_IsPreconditionFailed()
    {
        var result = ChecklistCommandHandlers.EvaluateAdvanceNext(null);
        Assert.Equal(CommandOutcome.PreconditionFailed, result.Outcome);
    }

    [Fact]
    public void EvaluateAdvanceNext_CompletedChecklist_IsAlreadySatisfied()
    {
        var result = ChecklistCommandHandlers.EvaluateAdvanceNext(View(1, true, Manual("a")));
        Assert.Equal(CommandOutcome.AlreadySatisfied, result.Outcome);
    }

    [Fact]
    public void EvaluateAdvanceNext_ManualActiveLine_Succeeds()
    {
        var result = ChecklistCommandHandlers.EvaluateAdvanceNext(View(1, false, Manual("a"), Manual("b")));
        Assert.Equal(CommandOutcome.Success, result.Outcome);
    }
}
