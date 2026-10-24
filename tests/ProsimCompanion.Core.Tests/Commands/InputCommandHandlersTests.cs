using Moq;
using ProsimCompanion.Core.Commands;
using ProsimCompanion.Core.Commands.Handlers;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using Xunit;

namespace ProsimCompanion.Core.Tests.Commands;

/// <summary>The <c>input.*</c> commands of the vJoy relay (2026-10-24): validation, view
/// lookup, and the degrade paths when the Sim pillar is absent.</summary>
public sealed class InputCommandHandlersTests
{
    private static InputRelayOptions Views(params (string Name, int Button)[] rows) => new()
    {
        Views = rows.Select(r => new InputViewButton { Name = r.Name, Button = r.Button }).ToList(),
    };

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(129)]
    [InlineData(-3)]
    public void ValidateButton_RefusesMissingAndOutOfRange(int? button)
        => Assert.Throws<CommandValidationException>(() => InputCommandHandlers.ValidateButton(button));

    [Fact]
    public void ValidateButton_AcceptsTheVJoyRange()
    {
        Assert.Equal(1, InputCommandHandlers.ValidateButton(1));
        Assert.Equal(128, InputCommandHandlers.ValidateButton(128));
    }

    [Fact]
    public void ValidateHold_NullMeansConfigured_AndBoundsAreEnforced()
    {
        Assert.Null(InputCommandHandlers.ValidateHold(null));
        Assert.Equal(150, InputCommandHandlers.ValidateHold(150));
        Assert.Throws<CommandValidationException>(() => InputCommandHandlers.ValidateHold(5));
        Assert.Throws<CommandValidationException>(() => InputCommandHandlers.ValidateHold(5000));
    }

    [Fact]
    public void ResolveView_IsCaseInsensitiveAndTrimmed()
    {
        var options = Views(("Wing", 5), ("Cockpit", 1));

        Assert.Equal(5, InputCommandHandlers.ResolveView(options, "  wing ")?.Button);
        Assert.Equal(1, InputCommandHandlers.ResolveView(options, "COCKPIT")?.Button);
        Assert.Null(InputCommandHandlers.ResolveView(options, "tail"));
        Assert.Null(InputCommandHandlers.ResolveView(options, ""));
        Assert.Null(InputCommandHandlers.ResolveView(null, "wing"));
    }

    [Fact]
    public async Task PressView_UnknownName_IsPreconditionFailed_NamingTheKnownViews()
    {
        var relay = new Mock<IInputRelay>(MockBehavior.Strict);

        var result = await InputCommandHandlers.PressViewAsync(
            relay.Object, Views(("Wing", 5), ("Cockpit", 1)), new InputPressViewRequest { Name = "tail" }, CancellationToken.None);

        Assert.Equal(CommandOutcome.PreconditionFailed, result.Outcome);
        Assert.Contains("Wing", result.Reason, StringComparison.Ordinal);
        Assert.Contains("Cockpit", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PressView_MissingName_IsAValidationError()
        => await Assert.ThrowsAsync<CommandValidationException>(() =>
            InputCommandHandlers.PressViewAsync(null, Views(), new InputPressViewRequest(), CancellationToken.None));

    [Fact]
    public async Task PressView_KnownName_PressesTheMappedButton_WithTheViewAsLabel()
    {
        var relay = new Mock<IInputRelay>();
        relay.Setup(r => r.PressAsync(5, null, "Wing", It.IsAny<CancellationToken>()))
            .ReturnsAsync(CommandResult.Ok("Pressed Wing"));

        var result = await InputCommandHandlers.PressViewAsync(
            relay.Object, Views(("Wing", 5)), new InputPressViewRequest { Name = "wing" }, CancellationToken.None);

        Assert.Equal(CommandOutcome.Success, result.Outcome);
        relay.VerifyAll();
    }

    [Fact]
    public async Task WithoutTheSimPillar_BothCommandsAnswerUnavailable_AfterValidation()
    {
        var pressed = await InputCommandHandlers.PressAsync(null, new InputVJoyPressRequest { Button = 3 }, CancellationToken.None);
        Assert.Equal(CommandOutcome.Unavailable, pressed.Outcome);

        var view = await InputCommandHandlers.PressViewAsync(null, Views(("Wing", 5)), new InputPressViewRequest { Name = "wing" }, CancellationToken.None);
        Assert.Equal(CommandOutcome.Unavailable, view.Outcome);

        // Validation still runs first: a bad button is a 400, not an "unavailable".
        await Assert.ThrowsAsync<CommandValidationException>(() =>
            InputCommandHandlers.PressAsync(null, new InputVJoyPressRequest { Button = 0 }, CancellationToken.None));
    }

    [Fact]
    public async Task Registered_CommandsExist_AndCarryTheRequestTypes()
    {
        var registry = new CommandRegistry();
        InputCommandHandlers.Register(registry, null, null);

        Assert.Contains("input.vjoyPress", registry.Names);
        Assert.Contains("input.pressView", registry.Names);
        Assert.True(registry.TryGetRequestType("input.vjoyPress", out var type));
        Assert.Equal(typeof(InputVJoyPressRequest), type);

        var result = await registry.ExecuteAsync<InputVJoyPressRequest, CommandResult>("input.vjoyPress", new InputVJoyPressRequest { Button = 2 });
        Assert.Equal(CommandOutcome.Unavailable, result.Outcome);
    }
}
