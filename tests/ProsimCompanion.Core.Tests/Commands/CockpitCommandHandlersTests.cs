using Moq;
using ProsimCompanion.Core.Commands;
using ProsimCompanion.Core.Commands.Handlers;
using ProsimCompanion.Core.State;
using Xunit;

namespace ProsimCompanion.Core.Tests.Commands;

public sealed class CockpitCommandHandlersTests
{
    [Fact]
    public async Task Press_WithoutAnId_IsAValidationError()
        => await Assert.ThrowsAsync<CommandValidationException>(() =>
            CockpitCommandHandlers.PressAsync(Mock.Of<ICockpitSwitchControl>(), new CockpitPressRequest(), CancellationToken.None));

    [Fact]
    public async Task Set_WithoutAValue_IsAValidationError()
        => await Assert.ThrowsAsync<CommandValidationException>(() =>
            CockpitCommandHandlers.SetAsync(Mock.Of<ICockpitSwitchControl>(), new CockpitSetRequest { Id = "x" }, CancellationToken.None));

    [Fact]
    public async Task WithoutTheProsimPillar_BothAnswerUnavailable()
    {
        var press = await CockpitCommandHandlers.PressAsync(null, new CockpitPressRequest { Id = "lmv" }, CancellationToken.None);
        var set = await CockpitCommandHandlers.SetAsync(null, new CockpitSetRequest { Id = "lmv", Value = 1 }, CancellationToken.None);

        Assert.Equal(CommandOutcome.Unavailable, press.Outcome);
        Assert.Equal(CommandOutcome.Unavailable, set.Outcome);
    }

    [Fact]
    public async Task Press_TrimsTheId_AndPassesTheLongFlag()
    {
        var control = new Mock<ICockpitSwitchControl>();
        control.Setup(c => c.PressAsync("oh_adirs_1", true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CommandResult.Ok("ADIRS 1: NAV → OFF."));

        var result = await CockpitCommandHandlers.PressAsync(control.Object, new CockpitPressRequest { Id = " oh_adirs_1 ", Long = true }, CancellationToken.None);

        Assert.Equal(CommandOutcome.Success, result.Outcome);
        control.VerifyAll();
    }

    [Fact]
    public async Task Registered_CommandsExist()
    {
        var registry = new CommandRegistry();
        CockpitCommandHandlers.Register(registry, null);

        Assert.Contains("cockpit.press", registry.Names);
        Assert.Contains("cockpit.set", registry.Names);
        var result = await registry.ExecuteAsync<CockpitPressRequest, CommandResult>("cockpit.press", new CockpitPressRequest { Id = "x" });
        Assert.Equal(CommandOutcome.Unavailable, result.Outcome);
    }
}
