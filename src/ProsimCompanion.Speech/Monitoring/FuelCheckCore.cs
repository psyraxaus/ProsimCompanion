using System.Globalization;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Speech.Arbiter;

namespace ProsimCompanion.Speech.Monitoring;

/// <summary>Everything one fuel check reads.</summary>
/// <param name="FuelOnBoardKg">Live total fuel; null when ProSim has not pushed it.</param>
/// <param name="FuelFlowKgPerHour">Both engines' fuel flow; 0 when unknown.</param>
/// <param name="Position">Aircraft position; null = no position (the navlog cannot be used).</param>
/// <param name="Navlog">The OFP navlog (may be empty).</param>
/// <param name="PlannedLandingKg">OFP planned landing fuel; 0 when unknown.</param>
/// <param name="EtaUtc">Estimated arrival (the flight-progress store's), for the fallback.</param>
public sealed record FuelCheckInputs(
    double? FuelOnBoardKg,
    double FuelFlowKgPerHour,
    GeoPoint? Position,
    IReadOnlyList<OfpFix> Navlog,
    double PlannedLandingKg,
    DateTimeOffset NowUtc,
    DateTimeOffset? EtaUtc);

/// <summary>One fuel check, ready to speak and to log.</summary>
public sealed record FuelCheckResult(
    string Text,
    SpeechPriority Priority,
    string Method,
    string? Fix,
    double FuelOnBoardKg,
    double? PlannedFuelOnBoardKg,
    double? DifferenceKg,
    double? EstimatedLandingKg,
    double? PlannedLandingKg,
    bool Shortfall);

/// <summary>
/// The pure fuel check (issue #148). With a navlog and a position: find the last fix passed,
/// interpolate the planned fuel on board to where the aircraft is on the leg, and compare the
/// live figure with it; the estimated landing fuel is then the planned landing fuel plus that
/// difference (the plan's burn from here is the plan's business). Without either: live fuel
/// minus fuel flow times the time to the ETA, against the planned landing fuel. Nothing to
/// compare against at all = null, and the caller says so.
/// </summary>
public static class FuelCheckCore
{
    /// <summary>How far off the direct leg the aircraft may be and still count as on it.</summary>
    public const double CorridorNm = NavlogProgress.CorridorNm;

    public static FuelCheckResult? Compute(FuelCheckInputs inputs, FuelCheckOptions options)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(options);

        if (inputs.FuelOnBoardKg is not { } fob || fob <= 0)
        {
            return null;
        }

        var plannedLanding = inputs.PlannedLandingKg > 0 ? inputs.PlannedLandingKg : (double?)null;

        if (inputs.Position is { } position && LastFixPassed(inputs.Navlog, position) is { } passed
            && PlannedFuelAt(inputs.Navlog, passed.Index, passed.Fraction) is { } plannedHere)
        {
            var difference = fob - plannedHere;
            var estimated = plannedLanding is { } landing ? landing + difference : (double?)null;
            var shortfall = -difference >= options.ShortfallMarginKg;
            var fix = inputs.Navlog[passed.Index].Ident;
            var where = passed.Fraction <= 0.1 ? $"Passing {Spoken(fix)}" : $"Past {Spoken(fix)}";
            var text = $"Fuel check. {where}, fuel on board {Tonnes(fob)} tonnes, {Difference(difference, options)}."
                + Landing(estimated, plannedLanding);
            return new FuelCheckResult(
                text, shortfall ? SpeechPriority.High : SpeechPriority.Normal, "navlog", fix,
                fob, plannedHere, difference, estimated, plannedLanding, shortfall);
        }

        // Fallback: burn to the ETA at the present flow.
        double? estimate = null;
        if (inputs.EtaUtc is { } eta && inputs.FuelFlowKgPerHour > 0 && eta > inputs.NowUtc)
        {
            estimate = Math.Max(0, fob - (inputs.FuelFlowKgPerHour * (eta - inputs.NowUtc).TotalHours));
        }

        if (estimate is null && plannedLanding is null)
        {
            return null;
        }

        var fallbackDifference = estimate is { } e && plannedLanding is { } p ? e - p : (double?)null;
        var fallbackShort = fallbackDifference is { } d && -d >= options.ShortfallMarginKg;
        var fallbackText = $"Fuel check. Fuel on board {Tonnes(fob)} tonnes." + Landing(estimate, plannedLanding);
        return new FuelCheckResult(
            fallbackText, fallbackShort ? SpeechPriority.High : SpeechPriority.Normal, "fallback", null,
            fob, null, fallbackDifference, estimate, plannedLanding, fallbackShort);
    }

    /// <summary>Moved to <see cref="NavlogProgress"/> for the Fuel Log page (#154); kept here so
    /// the fuel check reads exactly as before.</summary>
    public static (int Index, double Fraction)? LastFixPassed(IReadOnlyList<OfpFix> navlog, GeoPoint position)
        => NavlogProgress.LastFixPassed(navlog, position);

    /// <summary>See <see cref="NavlogProgress.PlannedFuelAt"/>.</summary>
    public static double? PlannedFuelAt(IReadOnlyList<OfpFix> navlog, int index, double fraction)
        => NavlogProgress.PlannedFuelAt(navlog, index, fraction);

    /// <summary>"300 kilos below plan" / "1.2 tonnes above plan" / "on plan".</summary>
    public static string Difference(double differenceKg, FuelCheckOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var magnitude = Math.Abs(differenceKg);
        if (magnitude < options.OnPlanWithinKg)
        {
            return "on plan";
        }

        var side = differenceKg < 0 ? "below" : "above";
        return magnitude >= 1000
            ? $"{Tonnes(magnitude)} tonnes {side} plan"
            : $"{Math.Round(magnitude / 100) * 100:0} kilos {side} plan";
    }

    private static string Landing(double? estimated, double? planned)
        => (estimated, planned) switch
        {
            ({ } e, { } p) => $" Estimated landing fuel {Tonnes(e)} tonnes, planned {Tonnes(p)}.",
            ({ } e, null) => $" Estimated landing fuel {Tonnes(e)} tonnes.",
            (null, { } p) => $" Planned landing fuel {Tonnes(p)} tonnes.",
            _ => "",
        };

    private static string Tonnes(double kg) => (kg / 1000.0).ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>Waypoint names are words ("MOGOP"); TOC / TOD are the plan's own markers.</summary>
    private static string Spoken(string ident) => ident.ToUpperInvariant() switch
    {
        "TOC" => "top of climb",
        "TOD" => "top of descent",
        _ => Core.Speech.NatoPhonetics.SpeakIdentifier(ident),
    };
}
