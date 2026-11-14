using ProsimCompanion.Core.Commands;
using ProsimCompanion.Core.Commands.Handlers;
using ProsimCompanion.Core.Day;
using Xunit;

namespace ProsimCompanion.Core.Tests.Commands;

/// <summary>Issue #239: <c>day.discard</c> — absent pillar → unavailable, an open day →
/// success, no open day → alreadySatisfied; and it never ends the day the debrief way.</summary>
public sealed class DayCommandHandlersTests
{
    private sealed class FakeDayControl : IDayControl
    {
        public bool Open { get; set; }

        public int Ended { get; private set; }

        public int Discarded { get; private set; }

        public void StartDay() => Open = true;

        public void EndDay()
        {
            Ended++;
            Open = false;
        }

        public bool DiscardDay()
        {
            if (!Open)
            {
                return false;
            }

            Discarded++;
            Open = false;
            return true;
        }
    }

    [Fact]
    public void AbsentPillar_AnswersUnavailable()
        => Assert.Equal(CommandOutcome.Unavailable, DayCommandHandlers.Discard(null).Outcome);

    [Fact]
    public void Discard_OfAnOpenDay_Succeeds_ThenIsAlreadySatisfied()
    {
        var control = new FakeDayControl { Open = true };

        Assert.Equal(CommandOutcome.Success, DayCommandHandlers.Discard(control).Outcome);
        Assert.Equal(1, control.Discarded);
        Assert.Equal(0, control.Ended); // discard is not an end of duty

        Assert.Equal(CommandOutcome.AlreadySatisfied, DayCommandHandlers.Discard(control).Outcome);
        Assert.Equal(1, control.Discarded);
    }

    [Fact]
    public void Register_AddsTheCommand_WithOrWithoutThePillar()
    {
        var registry = new CommandRegistry();
        DayCommandHandlers.Register(registry, null);
        Assert.Contains("day.discard", registry.Names);
    }
}
