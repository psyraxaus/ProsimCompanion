using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Commands.Handlers;

/// <summary>Request DTO for <c>flight.divert</c> (issue #184).</summary>
public sealed record FlightDivertRequest
{
    /// <summary>The new destination ICAO; blank = the OFP's alternate.</summary>
    public string? Icao { get; init; }

    /// <summary>weather | technical | medical | fuel | other; blank = other.</summary>
    public string? Reason { get; init; }
}

/// <summary>What the diversion workflow does for a command (issue #184); implemented by the
/// Speech pillar's diversion service (it speaks and messages). Absent = the pillar is off.</summary>
public interface IDiversionControl
{
    /// <summary>Declares the diversion; the result says what happened.</summary>
    Task<CommandResult> DivertAsync(string? icao, DiversionReason reason, string source, CancellationToken cancellationToken = default);

    /// <summary>Back to the planned destination.</summary>
    Task<CommandResult> CancelAsync(string source, CancellationToken cancellationToken = default);
}

/// <summary><c>flight.divert</c> / <c>flight.cancelDiversion</c> over the diversion seam.</summary>
public static class FlightCommandHandlers
{
    public static void Register(CommandRegistry registry, IDiversionControl? control)
    {
        ArgumentNullException.ThrowIfNull(registry);

        registry.Register<FlightDivertRequest, CommandResult>(
            "flight.divert",
            async (request, cancellationToken) =>
            {
                if (control is null)
                {
                    return CommandResult.Unavailable("The diversion workflow is not running.");
                }

                return await control.DivertAsync(request.Icao, ParseReason(request.Reason), "command", cancellationToken).ConfigureAwait(false);
            });

        registry.Register<EmptyCommandRequest, CommandResult>(
            "flight.cancelDiversion",
            async (_, cancellationToken) =>
            {
                if (control is null)
                {
                    return CommandResult.Unavailable("The diversion workflow is not running.");
                }

                return await control.CancelAsync("command", cancellationToken).ConfigureAwait(false);
            });
    }

    /// <summary>"weather" / "technical" / "medical" / "fuel" / anything else → Other; also the
    /// words a pilot says ("for weather", "tech", "sick passenger"). Pure, for tests.</summary>
    public static DiversionReason ParseReason(string? text)
    {
        var words = (text ?? "").Trim().ToLowerInvariant();
        if (words.Contains("weather", StringComparison.Ordinal) || words.Contains("wx", StringComparison.Ordinal) || words.Contains("storm", StringComparison.Ordinal) || words.Contains("fog", StringComparison.Ordinal))
        {
            return DiversionReason.Weather;
        }

        if (words.Contains("tech", StringComparison.Ordinal) || words.Contains("fail", StringComparison.Ordinal) || words.Contains("engine", StringComparison.Ordinal) || words.Contains("hydraulic", StringComparison.Ordinal))
        {
            return DiversionReason.Technical;
        }

        if (words.Contains("medic", StringComparison.Ordinal) || words.Contains("sick", StringComparison.Ordinal) || words.Contains("passenger", StringComparison.Ordinal) || words.Contains("ill", StringComparison.Ordinal))
        {
            return DiversionReason.Medical;
        }

        if (words.Contains("fuel", StringComparison.Ordinal))
        {
            return DiversionReason.Fuel;
        }

        return DiversionReason.Other;
    }
}
