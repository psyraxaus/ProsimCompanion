using Moq;
using ProsimCompanion.Core.Commands;
using ProsimCompanion.Core.Commands.Handlers;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using Xunit;

namespace ProsimCompanion.Core.Tests.Commands;

/// <summary>The <c>input.*</c> command of the vJoy relay (2026-10-24): view lookup, the row
/// bound, and the degrade path when the Sim pillar is absent. There is no raw button command —
/// a remote key names a configured view only (review 2026-10-25).</summary>
public sealed class InputCommandHandlersTests
{
    private static InputRelayOptions Views(params (string Name, int Button)[] rows) => new()
    {
        Views = rows.Select(r => new InputViewButton { Name = r.Name, Button = r.Button }).ToList(),
    };

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

    [Theory]
    [InlineData(0)]
    [InlineData(129)]
    public async Task PressView_RowOutsideTheVJoyRange_IsPreconditionFailed_WithoutTouchingTheRelay(int button)
    {
        var relay = new Mock<IInputRelay>(MockBehavior.Strict);

        var result = await InputCommandHandlers.PressViewAsync(
            relay.Object, Views(("Wing", button)), new InputPressViewRequest { Name = "wing" }, CancellationToken.None);

        Assert.Equal(CommandOutcome.PreconditionFailed, result.Outcome);
        Assert.Contains("128", result.Reason, StringComparison.Ordinal);
    }

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
    public async Task WithoutTheSimPillar_TheCommandAnswersUnavailable_AfterValidation()
    {
        var view = await InputCommandHandlers.PressViewAsync(null, Views(("Wing", 5)), new InputPressViewRequest { Name = "wing" }, CancellationToken.None);
        Assert.Equal(CommandOutcome.Unavailable, view.Outcome);

        // Validation still runs first: a missing name is a 400, not an "unavailable".
        await Assert.ThrowsAsync<CommandValidationException>(() =>
            InputCommandHandlers.PressViewAsync(null, Views(), new InputPressViewRequest(), CancellationToken.None));
    }

    [Fact]
    public async Task Registered_OnlyPressViewExists_AndCarriesItsRequestType()
    {
        var registry = new CommandRegistry();
        InputCommandHandlers.Register(registry, null, null);

        Assert.Contains("input.pressView", registry.Names);
        Assert.DoesNotContain("input.vjoyPress", registry.Names); // no raw button over the network
        Assert.True(registry.TryGetRequestType("input.pressView", out var type));
        Assert.Equal(typeof(InputPressViewRequest), type);

        var result = await registry.ExecuteAsync<InputPressViewRequest, CommandResult>("input.pressView", new InputPressViewRequest { Name = "wing" });
        Assert.Equal(CommandOutcome.PreconditionFailed, result.Outcome); // no options → no rows → named refusal
    }
}
