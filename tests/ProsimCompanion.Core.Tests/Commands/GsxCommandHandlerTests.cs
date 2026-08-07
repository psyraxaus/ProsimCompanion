using Moq;
using ProsimCompanion.Core.Commands;
using ProsimCompanion.Core.Commands.Handlers;
using ProsimCompanion.Core.State;
using Xunit;

namespace ProsimCompanion.Core.Tests.Commands;

public sealed class GsxCommandHandlerTests
{
    private static CommandRegistry Registry(IGsxDepartureControl? departure, IGsxGateControl? gate)
    {
        var registry = new CommandRegistry();
        GsxCommandHandlers.Register(registry, departure, gate);
        return registry;
    }

    [Fact]
    public async Task StartDepartureServices_WhenNotStarted_StartsAndSucceeds()
    {
        var departure = new Mock<IGsxDepartureControl>();
        departure.SetupGet(d => d.Started).Returns(false);
        var registry = Registry(departure.Object, gate: null);

        var result = await registry.ExecuteAsync<EmptyCommandRequest, CommandResult>(
            "gsx.startDepartureServices", new EmptyCommandRequest());

        Assert.Equal(CommandOutcome.Success, result.Outcome);
        departure.Verify(d => d.Start(), Times.Once);
    }

    [Fact]
    public async Task StartDepartureServices_WhenAlreadyStarted_IsAlreadySatisfied()
    {
        var departure = new Mock<IGsxDepartureControl>();
        departure.SetupGet(d => d.Started).Returns(true);
        var registry = Registry(departure.Object, gate: null);

        var result = await registry.ExecuteAsync<EmptyCommandRequest, CommandResult>(
            "gsx.startDepartureServices", new EmptyCommandRequest());

        Assert.Equal(CommandOutcome.AlreadySatisfied, result.Outcome);
        departure.Verify(d => d.Start(), Times.Never);
    }

    [Fact]
    public async Task ForceNextService_BeforeStart_IsPreconditionFailed()
    {
        var departure = new Mock<IGsxDepartureControl>();
        departure.SetupGet(d => d.Started).Returns(false);
        var registry = Registry(departure.Object, gate: null);

        var result = await registry.ExecuteAsync<EmptyCommandRequest, CommandResult>(
            "gsx.forceNextService", new EmptyCommandRequest());

        Assert.Equal(CommandOutcome.PreconditionFailed, result.Outcome);
        departure.Verify(d => d.ForceNext(), Times.Never);
    }

    [Fact]
    public async Task ForceNextService_MidSequence_Forces()
    {
        var departure = new Mock<IGsxDepartureControl>();
        departure.SetupGet(d => d.Started).Returns(true);
        departure.SetupGet(d => d.Complete).Returns(false);
        var registry = Registry(departure.Object, gate: null);

        var result = await registry.ExecuteAsync<EmptyCommandRequest, CommandResult>(
            "gsx.forceNextService", new EmptyCommandRequest());

        Assert.Equal(CommandOutcome.Success, result.Outcome);
        departure.Verify(d => d.ForceNext(), Times.Once);
    }

    [Fact]
    public async Task GsxCommands_WithAbsentPillar_AreUnavailableNotMissing()
    {
        var registry = Registry(departure: null, gate: null);

        // The commands stay registered (stable API shape); they answer Unavailable.
        Assert.True(registry.IsRegistered("gsx.forceNextService"));
        var result = await registry.ExecuteAsync<EmptyCommandRequest, CommandResult>(
            "gsx.forceNextService", new EmptyCommandRequest());

        Assert.Equal(CommandOutcome.Unavailable, result.Outcome);
    }

    [Fact]
    public async Task RequestGate_MissingGate_IsValidationError()
    {
        var gate = new Mock<IGsxGateControl>(MockBehavior.Strict);
        var registry = Registry(departure: null, gate.Object);

        await Assert.ThrowsAsync<CommandValidationException>(() =>
            registry.ExecuteAsync<GsxRequestGateRequest, CommandResult>(
                "gsx.requestGate", new GsxRequestGateRequest()));
    }

    [Fact]
    public async Task RequestGate_TrimsAndArms()
    {
        var gate = new Mock<IGsxGateControl>();
        var registry = Registry(departure: null, gate.Object);

        var result = await registry.ExecuteAsync<GsxRequestGateRequest, CommandResult>(
            "gsx.requestGate", new GsxRequestGateRequest { Gate = " B12 " });

        Assert.Equal(CommandOutcome.Success, result.Outcome);
        gate.Verify(g => g.RequestGate("B12"), Times.Once);
    }
}
