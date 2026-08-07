using Moq;
using ProsimCompanion.Core.Commands;
using ProsimCompanion.Core.Commands.Handlers;
using ProsimCompanion.Core.State;
using Xunit;

namespace ProsimCompanion.Core.Tests.Commands;

public sealed class GsxServiceCommandHandlerTests
{
    private static CommandRegistry Registry(IGsxServiceControl? control)
    {
        var registry = new CommandRegistry();
        GsxServiceCommandHandlers.Register(registry, control);
        return registry;
    }

    private static Task<CommandResult> ExecuteAsync(CommandRegistry registry, string name)
        => registry.ExecuteAsync<EmptyCommandRequest, CommandResult>(name, new EmptyCommandRequest());

    [Fact]
    public void AllElevenCommands_AreRegistered()
    {
        var registry = Registry(control: null);

        string[] expected =
        [
            "gsx.requestRefuel", "gsx.requestCatering", "gsx.requestBoarding",
            "gsx.requestDeboarding", "gsx.requestJetway", "gsx.retractJetway",
            "gsx.requestStairs", "gsx.retractStairs", "gsx.requestGpu",
            "gsx.requestDeice", "gsx.requestPushback",
        ];
        Assert.Equal(11, GsxServiceCommandHandlers.Commands.Count);
        foreach (var name in expected)
        {
            Assert.True(registry.IsRegistered(name), $"{name} should be registered");
        }
    }

    [Fact]
    public async Task WithAbsentSeam_CommandsAnswerUnavailable_NotMissing()
    {
        var registry = Registry(control: null);

        var result = await ExecuteAsync(registry, "gsx.requestBoarding");

        Assert.Equal(CommandOutcome.Unavailable, result.Outcome);
    }

    [Theory]
    [InlineData("gsx.requestRefuel", GsxServiceAction.RequestRefuel)]
    [InlineData("gsx.requestCatering", GsxServiceAction.RequestCatering)]
    [InlineData("gsx.requestBoarding", GsxServiceAction.RequestBoarding)]
    [InlineData("gsx.requestDeboarding", GsxServiceAction.RequestDeboarding)]
    [InlineData("gsx.requestJetway", GsxServiceAction.RequestJetway)]
    [InlineData("gsx.retractJetway", GsxServiceAction.RetractJetway)]
    [InlineData("gsx.requestStairs", GsxServiceAction.RequestStairs)]
    [InlineData("gsx.retractStairs", GsxServiceAction.RetractStairs)]
    [InlineData("gsx.requestGpu", GsxServiceAction.RequestGpu)]
    [InlineData("gsx.requestDeice", GsxServiceAction.RequestDeice)]
    [InlineData("gsx.requestPushback", GsxServiceAction.RequestPushback)]
    public async Task EachCommand_RoutesItsAction(string name, GsxServiceAction expected)
    {
        var control = new Mock<IGsxServiceControl>();
        control
            .Setup(c => c.TryCallAsync(It.IsAny<GsxServiceAction>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GsxServiceCallOutcome(GsxServiceCallStatus.Called, "ok"));
        var registry = Registry(control.Object);

        _ = await ExecuteAsync(registry, name);

        control.Verify(c => c.TryCallAsync(expected, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(GsxServiceCallStatus.Called, CommandOutcome.Success)]
    [InlineData(GsxServiceCallStatus.AlreadySatisfied, CommandOutcome.AlreadySatisfied)]
    [InlineData(GsxServiceCallStatus.NotCallable, CommandOutcome.PreconditionFailed)]
    [InlineData(GsxServiceCallStatus.Rejected, CommandOutcome.Failed)]
    [InlineData(GsxServiceCallStatus.Unavailable, CommandOutcome.Unavailable)]
    public async Task SeamStatus_MapsToCommandOutcome(GsxServiceCallStatus status, CommandOutcome expected)
    {
        var control = new Mock<IGsxServiceControl>();
        control
            .Setup(c => c.TryCallAsync(It.IsAny<GsxServiceAction>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GsxServiceCallOutcome(status, "detail text"));
        var registry = Registry(control.Object);

        var result = await ExecuteAsync(registry, "gsx.requestRefuel");

        Assert.Equal(expected, result.Outcome);
        Assert.Equal("detail text", result.Reason);
    }
}
