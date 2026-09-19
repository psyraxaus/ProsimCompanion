using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Commands.Handlers;

/// <summary>
/// Per-service <c>gsx.request*</c>/<c>gsx.retract*</c> command handlers over the
/// <see cref="IGsxServiceControl"/> seam (the serialized single-writer trigger path in the GSX
/// layer). As with every bundle, the seam is nullable: with the GSX pillar absent the commands
/// stay registered — stable API shape — and answer <see cref="CommandOutcome.Unavailable"/>.
/// </summary>
public static class GsxServiceCommandHandlers
{
    /// <summary>Command name → action, in registration order. Exposed for tests so the mapping
    /// and the registration can never drift apart.</summary>
    public static IReadOnlyDictionary<string, GsxServiceAction> Commands { get; } =
        new Dictionary<string, GsxServiceAction>(StringComparer.Ordinal)
        {
            ["gsx.requestRefuel"] = GsxServiceAction.RequestRefuel,
            ["gsx.requestCatering"] = GsxServiceAction.RequestCatering,
            ["gsx.requestBoarding"] = GsxServiceAction.RequestBoarding,
            ["gsx.requestDeboarding"] = GsxServiceAction.RequestDeboarding,
            ["gsx.requestJetway"] = GsxServiceAction.RequestJetway,
            ["gsx.retractJetway"] = GsxServiceAction.RetractJetway,
            ["gsx.requestStairs"] = GsxServiceAction.RequestStairs,
            ["gsx.retractStairs"] = GsxServiceAction.RetractStairs,
            ["gsx.requestGpu"] = GsxServiceAction.RequestGpu,
            ["gsx.requestDeice"] = GsxServiceAction.RequestDeice,
            ["gsx.requestPushback"] = GsxServiceAction.RequestPushback,
            ["gsx.confirmFuel"] = GsxServiceAction.ConfirmFuel,
        };

    public static void Register(CommandRegistry registry, IGsxServiceControl? serviceControl)
    {
        ArgumentNullException.ThrowIfNull(registry);

        foreach (var (name, action) in Commands)
        {
            registry.Register<EmptyCommandRequest, CommandResult>(
                name,
                (_, cancellationToken) => CallAsync(serviceControl, action, cancellationToken));
        }
    }

    private static async Task<CommandResult> CallAsync(
        IGsxServiceControl? serviceControl,
        GsxServiceAction action,
        CancellationToken cancellationToken)
    {
        if (serviceControl is null)
        {
            return CommandResult.Unavailable("The GSX pillar is not running.");
        }

        var outcome = await serviceControl.TryCallAsync(action, cancellationToken).ConfigureAwait(false);
        return outcome.Status switch
        {
            GsxServiceCallStatus.Called => CommandResult.Ok(outcome.Detail),
            GsxServiceCallStatus.AlreadySatisfied => CommandResult.AlreadySatisfied(outcome.Detail),
            GsxServiceCallStatus.NotCallable => CommandResult.PreconditionFailed(outcome.Detail),
            GsxServiceCallStatus.Rejected => CommandResult.Failed(outcome.Detail),
            _ => CommandResult.Unavailable(outcome.Detail),
        };
    }
}
