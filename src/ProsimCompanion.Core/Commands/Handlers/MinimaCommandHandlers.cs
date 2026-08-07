using System.Globalization;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Commands.Handlers;

/// <summary>Request DTO for <c>minima.set</c>. Both fields nullable so missing values are
/// reported as validation errors instead of silently defaulting.</summary>
public sealed record MinimaSetRequest
{
    /// <summary>Minima kind: "da", "dh" or "mda" (or the spelled-out enum names).</summary>
    public string? Kind { get; init; }

    /// <summary>Minimum in feet — MSL for DA/MDA, radio height for DH.</summary>
    public double? AltitudeFt { get; init; }
}

/// <summary>
/// <c>minima.*</c> command handlers over <see cref="ArrivalMinimaStore"/>. Minima are
/// crew-entered by design (there is no DH/MDA dataref in ProSim and guessing is forbidden), so
/// this is the Stream Deck/API path for the same entry the /speech page offers.
/// </summary>
public static class MinimaCommandHandlers
{
    /// <summary>Upper sanity bound in feet — above any published DA/MDA (high-elevation
    /// airports included); catches unit mix-ups like metres or flight levels.</summary>
    public const double MaxAltitudeFt = 20_000;

    public static void Register(CommandRegistry registry, ArrivalMinimaStore? minima)
    {
        ArgumentNullException.ThrowIfNull(registry);

        registry.Register<MinimaSetRequest, CommandResult>(
            "minima.set",
            (request, _) => Task.FromResult(Set(minima, request)));

        registry.Register<EmptyCommandRequest, CommandResult>(
            "minima.clear",
            (_, _) => Task.FromResult(Clear(minima)));
    }

    /// <summary>Parses the wire form of the minima kind ("da"/"dh"/"mda", or the enum names,
    /// case-insensitive). Public and pure so the parsing rules are unit-testable.</summary>
    /// <exception cref="CommandValidationException">Missing or unknown kind.</exception>
    public static ArrivalMinimumKind ParseKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            throw new CommandValidationException("kind is required — use \"da\", \"dh\" or \"mda\".");
        }

        return kind.Trim().ToUpperInvariant() switch
        {
            "DA" or "DECISIONALTITUDE" => ArrivalMinimumKind.DecisionAltitude,
            "DH" or "DECISIONHEIGHT" => ArrivalMinimumKind.DecisionHeight,
            "MDA" or "MINIMUMDESCENTALTITUDE" => ArrivalMinimumKind.MinimumDescentAltitude,
            _ => throw new CommandValidationException(
                $"Unknown minima kind '{kind}' — use \"da\", \"dh\" or \"mda\"."),
        };
    }

    /// <summary>Validates the altitude field. 0 is allowed (a DH of zero is a real, if rare,
    /// briefing value); negatives, non-finite values and unit mix-ups are not.</summary>
    /// <exception cref="CommandValidationException">Missing or out-of-range altitude.</exception>
    public static double ValidateAltitude(double? altitudeFt)
    {
        if (altitudeFt is null)
        {
            throw new CommandValidationException("altitudeFt is required, e.g. { \"kind\": \"da\", \"altitudeFt\": 740 }.");
        }

        var value = altitudeFt.Value;
        if (!double.IsFinite(value) || value < 0 || value > MaxAltitudeFt)
        {
            throw new CommandValidationException(
                FormattableString.Invariant($"altitudeFt must be between 0 and {MaxAltitudeFt} feet."));
        }

        return value;
    }

    private static CommandResult Set(ArrivalMinimaStore? minima, MinimaSetRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (minima is null)
        {
            return CommandResult.Unavailable("The arrival minima store is not running.");
        }

        var kind = ParseKind(request.Kind);
        var altitude = ValidateAltitude(request.AltitudeFt);

        minima.Set(new ArrivalMinima(kind, altitude));
        return CommandResult.Ok(string.Create(
            CultureInfo.InvariantCulture, $"Arrival minima set: {kind} {altitude:0} ft."));
    }

    private static CommandResult Clear(ArrivalMinimaStore? minima)
    {
        if (minima is null)
        {
            return CommandResult.Unavailable("The arrival minima store is not running.");
        }

        if (minima.Current is null)
        {
            return CommandResult.AlreadySatisfied("No arrival minima are set.");
        }

        minima.Clear();
        return CommandResult.Ok("Arrival minima cleared — minima callouts are disabled until re-entered.");
    }
}
