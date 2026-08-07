using ProsimCompanion.Core.Commands;
using Xunit;

namespace ProsimCompanion.Core.Tests.Commands;

public sealed class CommandRegistryTests
{
    private sealed record PingRequest
    {
        public string? Text { get; init; }
    }

    [Fact]
    public async Task Register_ThenExecute_InvokesHandlerWithRequestAndToken()
    {
        var registry = new CommandRegistry();
        registry.Register<PingRequest, CommandResult>(
            "test.ping",
            (request, _) => Task.FromResult(CommandResult.Ok($"pong:{request.Text}")));

        var result = await registry.ExecuteAsync<PingRequest, CommandResult>(
            "test.ping", new PingRequest { Text = "hello" });

        Assert.Equal(CommandOutcome.Success, result.Outcome);
        Assert.Equal("pong:hello", result.Reason);
    }

    [Fact]
    public void Register_DuplicateName_Throws()
    {
        var registry = new CommandRegistry();
        registry.Register<EmptyCommandRequest, CommandResult>(
            "test.dupe", (_, _) => Task.FromResult(CommandResult.Ok("first")));

        Assert.Throws<InvalidOperationException>(() =>
            registry.Register<EmptyCommandRequest, CommandResult>(
                "test.dupe", (_, _) => Task.FromResult(CommandResult.Ok("second"))));
    }

    [Fact]
    public async Task Execute_UnknownName_ThrowsCommandNotFound()
    {
        var registry = new CommandRegistry();

        var typed = await Assert.ThrowsAsync<CommandNotFoundException>(() =>
            registry.ExecuteAsync<EmptyCommandRequest, CommandResult>("test.missing", new EmptyCommandRequest()));
        Assert.Equal("test.missing", typed.CommandName);

        await Assert.ThrowsAsync<CommandNotFoundException>(() =>
            registry.ExecuteUntypedAsync("test.missing", new EmptyCommandRequest()));
    }

    [Fact]
    public async Task Execute_WrongSignature_ThrowsInvalidOperation()
    {
        var registry = new CommandRegistry();
        registry.Register<PingRequest, CommandResult>(
            "test.typed", (_, _) => Task.FromResult(CommandResult.Ok("ok")));

        // Same name, different request type — a caller/bundle mismatch, not a soft failure.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.ExecuteAsync<EmptyCommandRequest, CommandResult>("test.typed", new EmptyCommandRequest()));

        // Different response type is just as much of a bug.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.ExecuteAsync<PingRequest, string>("test.typed", new PingRequest()));
    }

    [Fact]
    public async Task ExecuteUntyped_RunsHandlerAndReturnsBoxedResult()
    {
        var registry = new CommandRegistry();
        registry.Register<PingRequest, CommandResult>(
            "test.ping",
            (request, _) => Task.FromResult(CommandResult.Ok($"pong:{request.Text}")));

        var result = await registry.ExecuteUntypedAsync("test.ping", new PingRequest { Text = "x" });

        var commandResult = Assert.IsType<CommandResult>(result);
        Assert.Equal("pong:x", commandResult.Reason);
    }

    [Fact]
    public void NamesAndIsRegisteredAndRequestType_ReflectRegistrations()
    {
        var registry = new CommandRegistry();
        registry.Register<PingRequest, CommandResult>(
            "b.second", (_, _) => Task.FromResult(CommandResult.Ok("ok")));
        registry.Register<EmptyCommandRequest, CommandResult>(
            "a.first", (_, _) => Task.FromResult(CommandResult.Ok("ok")));

        Assert.Equal(["a.first", "b.second"], registry.Names); // sorted for stable diagnostics
        Assert.True(registry.IsRegistered("a.first"));
        Assert.False(registry.IsRegistered("a.missing"));

        Assert.True(registry.TryGetRequestType("b.second", out var requestType));
        Assert.Equal(typeof(PingRequest), requestType);
        Assert.False(registry.TryGetRequestType("a.missing", out _));
    }
}
