using ProsimCompanion.Core.Commands;
using Xunit;

namespace ProsimCompanion.Core.Tests.Commands;

public sealed class CommandOutcomeHttpTests
{
    [Theory]
    [InlineData(CommandOutcome.Success, 200)]
    [InlineData(CommandOutcome.AlreadySatisfied, 200)]
    [InlineData(CommandOutcome.PhaseMismatch, 409)]
    [InlineData(CommandOutcome.PreconditionFailed, 409)]
    [InlineData(CommandOutcome.Failed, 500)]
    [InlineData(CommandOutcome.Unavailable, 503)]
    public void StatusCodeFor_MapsEveryOutcome(CommandOutcome outcome, int expected)
        => Assert.Equal(expected, CommandOutcomeHttp.StatusCodeFor(outcome));

    [Fact]
    public void StatusCodeFor_UnknownOutcome_FallsBackTo500()
        => Assert.Equal(500, CommandOutcomeHttp.StatusCodeFor((CommandOutcome)999));
}
