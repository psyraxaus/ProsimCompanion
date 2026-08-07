using ProsimCompanion.Core.Commands;
using ProsimCompanion.Core.Commands.Handlers;
using ProsimCompanion.Core.State;
using Xunit;

namespace ProsimCompanion.Core.Tests.Commands;

public sealed class MinimaCommandHandlerTests
{
    [Theory]
    [InlineData("da", ArrivalMinimumKind.DecisionAltitude)]
    [InlineData("DA", ArrivalMinimumKind.DecisionAltitude)]
    [InlineData("decisionAltitude", ArrivalMinimumKind.DecisionAltitude)]
    [InlineData("dh", ArrivalMinimumKind.DecisionHeight)]
    [InlineData("DecisionHeight", ArrivalMinimumKind.DecisionHeight)]
    [InlineData("mda", ArrivalMinimumKind.MinimumDescentAltitude)]
    [InlineData(" MDA ", ArrivalMinimumKind.MinimumDescentAltitude)]
    [InlineData("minimumDescentAltitude", ArrivalMinimumKind.MinimumDescentAltitude)]
    public void ParseKind_AcceptsShortAndEnumForms_CaseInsensitive(string kind, ArrivalMinimumKind expected)
        => Assert.Equal(expected, MinimaCommandHandlers.ParseKind(kind));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("qnh")]
    [InlineData("da100")]
    public void ParseKind_MissingOrUnknown_IsValidationError(string? kind)
        => Assert.Throws<CommandValidationException>(() => MinimaCommandHandlers.ParseKind(kind));

    [Theory]
    [InlineData(0)]
    [InlineData(200)]
    [InlineData(740.5)]
    [InlineData(20_000)]
    public void ValidateAltitude_InRange_ReturnsValue(double altitudeFt)
        => Assert.Equal(altitudeFt, MinimaCommandHandlers.ValidateAltitude(altitudeFt));

    [Theory]
    [InlineData(-1)]
    [InlineData(20_001)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ValidateAltitude_OutOfRange_IsValidationError(double altitudeFt)
        => Assert.Throws<CommandValidationException>(() => MinimaCommandHandlers.ValidateAltitude(altitudeFt));

    [Fact]
    public void ValidateAltitude_Missing_IsValidationError()
        => Assert.Throws<CommandValidationException>(() => MinimaCommandHandlers.ValidateAltitude(null));

    [Fact]
    public async Task Set_WritesStore_AndClear_EmptiesIt()
    {
        var registry = new CommandRegistry();
        var store = new ArrivalMinimaStore();
        MinimaCommandHandlers.Register(registry, store);

        var set = await registry.ExecuteAsync<MinimaSetRequest, CommandResult>(
            "minima.set", new MinimaSetRequest { Kind = "da", AltitudeFt = 740 });
        Assert.Equal(CommandOutcome.Success, set.Outcome);
        Assert.Equal(new ArrivalMinima(ArrivalMinimumKind.DecisionAltitude, 740), store.Current);

        var clear = await registry.ExecuteAsync<EmptyCommandRequest, CommandResult>(
            "minima.clear", new EmptyCommandRequest());
        Assert.Equal(CommandOutcome.Success, clear.Outcome);
        Assert.Null(store.Current);

        // Clearing again is an idempotent no-op, reported as such — never a bare ack.
        var again = await registry.ExecuteAsync<EmptyCommandRequest, CommandResult>(
            "minima.clear", new EmptyCommandRequest());
        Assert.Equal(CommandOutcome.AlreadySatisfied, again.Outcome);
    }

    [Fact]
    public async Task Set_WithAbsentStore_IsUnavailable()
    {
        var registry = new CommandRegistry();
        MinimaCommandHandlers.Register(registry, minima: null);

        var result = await registry.ExecuteAsync<MinimaSetRequest, CommandResult>(
            "minima.set", new MinimaSetRequest { Kind = "da", AltitudeFt = 740 });

        Assert.Equal(CommandOutcome.Unavailable, result.Outcome);
    }
}
