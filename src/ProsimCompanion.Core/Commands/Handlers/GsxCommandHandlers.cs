using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Commands.Handlers;

/// <summary>Request DTO for <c>gsx.requestGate</c>. Nullable so a missing field is
/// distinguishable from an empty string and reported as a validation error.</summary>
public sealed record GsxRequestGateRequest
{
    /// <summary>Gate/stand designator as GSX shows it, e.g. "B12".</summary>
    public string? Gate { get; init; }
}

/// <summary>
/// <c>gsx.*</c> command handlers over the existing web-UI control seams. The seams are nullable
/// because the GSX pillar is optional (degrade-not-fail): with the pillar absent the commands
/// stay registered — the API shape is stable — and answer <see cref="CommandOutcome.Unavailable"/>.
/// </summary>
public static class GsxCommandHandlers
{
    public static void Register(
        CommandRegistry registry,
        IGsxDepartureControl? departureControl,
        IGsxGateControl? gateControl)
    {
        ArgumentNullException.ThrowIfNull(registry);

        registry.Register<EmptyCommandRequest, CommandResult>(
            "gsx.startDepartureServices",
            (_, _) => Task.FromResult(StartDepartureServices(departureControl)));

        registry.Register<EmptyCommandRequest, CommandResult>(
            "gsx.forceNextService",
            (_, _) => Task.FromResult(ForceNextService(departureControl)));

        registry.Register<GsxRequestGateRequest, CommandResult>(
            "gsx.requestGate",
            (request, _) => Task.FromResult(RequestGate(gateControl, request)));

        registry.Register<EmptyCommandRequest, CommandResult>(
            "gsx.cancelGate",
            (_, _) => Task.FromResult(CancelGate(gateControl)));
    }

    private static CommandResult StartDepartureServices(IGsxDepartureControl? control)
    {
        if (control is null)
        {
            return CommandResult.Unavailable("GSX automation is not running.");
        }

        if (control.Started)
        {
            return CommandResult.AlreadySatisfied("The departure service sequence is already started.");
        }

        control.Start();
        return CommandResult.Ok("Departure service sequence started.");
    }

    private static CommandResult ForceNextService(IGsxDepartureControl? control)
    {
        if (control is null)
        {
            return CommandResult.Unavailable("GSX automation is not running.");
        }

        if (!control.Started)
        {
            return CommandResult.PreconditionFailed(
                "The departure service sequence has not been started — start it first.");
        }

        if (control.Complete)
        {
            return CommandResult.AlreadySatisfied("All departure services are already complete.");
        }

        control.ForceNext();
        return CommandResult.Ok(
            "Next departure service forced — its activation rule is bypassed on the next evaluation.");
    }

    private static CommandResult RequestGate(IGsxGateControl? control, GsxRequestGateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (control is null)
        {
            return CommandResult.Unavailable("GSX gate selection is not running.");
        }

        if (string.IsNullOrWhiteSpace(request.Gate))
        {
            throw new CommandValidationException("gate is required, e.g. { \"gate\": \"B12\" }.");
        }

        var gate = request.Gate.Trim();
        control.RequestGate(gate);
        return CommandResult.Ok($"Arrival gate request for '{gate}' armed.");
    }

    private static CommandResult CancelGate(IGsxGateControl? control)
    {
        if (control is null)
        {
            return CommandResult.Unavailable("GSX gate selection is not running.");
        }

        control.Cancel();
        return CommandResult.Ok("Arrival gate request cancelled.");
    }
}
