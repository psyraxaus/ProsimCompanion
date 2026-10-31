namespace ProsimCompanion.Core.Commands.Handlers;

/// <summary>A scenario command's request: the catalogue id (never a failure name).</summary>
public sealed record ScenarioIdRequest
{
    public string? Id { get; init; }
}

/// <summary>
/// The one serialised path every failure write takes (issue #197, ADR-0018): the Speech
/// pillar's scenario service implements it, and the web page, the HTTP API, the Stream Deck
/// and voice all arrive through the commands below. Ids are catalogue ids; nothing arriving
/// over the network is a ProSim failure name.
/// </summary>
public interface IScenarioControl
{
    /// <summary>Arms the catalogue entry now (announce → cancel window → write → verify).</summary>
    Task<CommandResult> ArmAsync(string? id, string source, CancellationToken cancellationToken = default);

    /// <summary>Clears the entry's armed failure.</summary>
    Task<CommandResult> ClearAsync(string? id, string source, CancellationToken cancellationToken = default);

    /// <summary>Clears every armed failure ProSim reports — unconditionally.</summary>
    Task<CommandResult> ClearAllAsync(string source, CancellationToken cancellationToken = default);

    /// <summary>ProSim's live failure list, for verifying the catalogue's names.</summary>
    Task<CommandResult> ListFailuresAsync(CancellationToken cancellationToken = default);
}

public static class ScenarioCommandHandlers
{
    public static void Register(CommandRegistry registry, IScenarioControl? control)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register<ScenarioIdRequest, CommandResult>(
            "scenario.arm",
            (request, cancellationToken) => control is null
                ? Task.FromResult(CommandResult.Unavailable("Scenario mode is not running."))
                : control.ArmAsync(request.Id, "command", cancellationToken));
        registry.Register<ScenarioIdRequest, CommandResult>(
            "scenario.clear",
            (request, cancellationToken) => control is null
                ? Task.FromResult(CommandResult.Unavailable("Scenario mode is not running."))
                : control.ClearAsync(request.Id, "command", cancellationToken));
        registry.Register<EmptyCommandRequest, CommandResult>(
            "scenario.clearAll",
            (_, cancellationToken) => control is null
                ? Task.FromResult(CommandResult.Unavailable("Scenario mode is not running."))
                : control.ClearAllAsync("command", cancellationToken));
        registry.Register<EmptyCommandRequest, CommandResult>(
            "scenario.listFailures",
            (_, cancellationToken) => control is null
                ? Task.FromResult(CommandResult.Unavailable("Scenario mode is not running."))
                : control.ListFailuresAsync(cancellationToken));
    }
}
