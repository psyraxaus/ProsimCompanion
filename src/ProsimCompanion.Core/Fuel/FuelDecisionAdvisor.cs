using System.Globalization;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Weather;

namespace ProsimCompanion.Core.Fuel;

/// <summary>What the advisor recommends.</summary>
public enum FuelRecommendation
{
    /// <summary>The plan's fuel is enough as far as the weather says.</summary>
    None,

    /// <summary>Take holding fuel for the given minutes.</summary>
    Holding,
}

/// <summary>What one airport looks like at the ETA: the TAF reading when there is one, else the
/// current METAR — never both mixed.</summary>
/// <param name="Icao">The station.</param>
/// <param name="VisibilityM">Visibility at the ETA.</param>
/// <param name="CeilingFt">Ceiling at the ETA; null = none.</param>
/// <param name="Thunderstorm">TS forecast or reported.</param>
/// <param name="FromTaf">True when read from the TAF, false from the METAR.</param>
public sealed record ArrivalOutlook(string Icao, int? VisibilityM, int? CeilingFt, bool Thunderstorm, bool FromTaf)
{
    /// <summary>The outlook from the TAF at the ETA when the facts carry one, else the METAR now.</summary>
    public static ArrivalOutlook? From(string? icao, WxFacts? facts, DateTimeOffset etaUtc)
    {
        if (string.IsNullOrWhiteSpace(icao) || facts is null || !MetarConditionsLight.HasObservation(facts))
        {
            return null;
        }

        if (TafParser.ForecastAt(facts.RawTaf, etaUtc) is { } taf)
        {
            return new ArrivalOutlook(icao, taf.VisibilityM, taf.CeilingFt, taf.Thunderstorm, FromTaf: true);
        }

        return new ArrivalOutlook(icao, facts.VisibilityMeters, facts.CeilingFt, facts.Precip == PrecipKind.Thunderstorm, FromTaf: false);
    }
}

/// <summary>The minima the outlook is judged against: the crew's entry when it is a height, else
/// the category-1 defaults.</summary>
public sealed record LandingMinima(int CeilingFt, int VisibilityM, string Source)
{
    public const int Cat1CeilingFt = 200;
    public const int Cat1VisibilityM = 550;

    public static LandingMinima Cat1 { get; } = new(Cat1CeilingFt, Cat1VisibilityM, "category 1 defaults");

    /// <summary>A decision height on file is a ceiling; a DA/MDA is MSL and cannot be compared
    /// with a TAF ceiling without the elevation, so the defaults stand for it.</summary>
    public static LandingMinima From(ArrivalMinima? minima)
        => minima is { Kind: ArrivalMinimumKind.DecisionHeight } dh
            ? new LandingMinima((int)Math.Round(dh.AltitudeFt), Cat1VisibilityM, "briefed decision height")
            : Cat1;
}

/// <summary>One fuel decision (issue #183).</summary>
public sealed record FuelDecision(
    FuelRecommendation Recommendation,
    int HoldingMinutes,
    double ExtraKg,
    IReadOnlyList<string> Reasons,
    bool DestinationBelowMinima,
    bool DestinationMarginal,
    bool AlternateBelowMinima,
    bool AlternateMarginal,
    string? LegalityLine,
    LandingMinima Minima,
    ArrivalOutlook? Destination,
    ArrivalOutlook? Alternate);

/// <summary>
/// The captain's extra-fuel advice at dispatch (issue #183), pure: the destination outlook at
/// the ETA (TAF, else METAR) and the alternate's against the landing minima. Thunderstorms,
/// marginal weather or below-minima weather each carry a holding figure from the options; the
/// recommendation is the largest, converted to kilos at the holding burn and rounded up to 100.
/// The legality line is advice, never a block: "destination below minima at ETA — an alternate
/// is required", with the alternate's state when it is also below.
/// </summary>
public static class FuelDecisionAdvisor
{
    /// <summary>A ceiling within this of the minima is marginal.</summary>
    public const int MarginalCeilingMarginFt = 500;

    /// <summary>A visibility within this of the minima is marginal.</summary>
    public const int MarginalVisibilityMarginM = 1000;

    public static FuelDecision Advise(ArrivalOutlook? destination, ArrivalOutlook? alternate, ArrivalMinima? briefedMinima, FuelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var minima = LandingMinima.From(briefedMinima);
        var reasons = new List<string>();
        var minutes = 0;

        var (destBelow, destMarginal) = Judge(destination, minima);
        var (altBelow, altMarginal) = Judge(alternate, minima);

        if (destination is { Thunderstorm: true })
        {
            minutes = Math.Max(minutes, options.ThunderstormHoldingMinutes);
            reasons.Add(destination.FromTaf
                ? "destination TAF has thunderstorms around our arrival"
                : "thunderstorms reported at the destination");
        }

        if (destBelow)
        {
            minutes = Math.Max(minutes, options.BelowMinimaHoldingMinutes);
            reasons.Add($"destination below minima at the ETA ({Describe(destination!)})");
        }
        else if (destMarginal)
        {
            minutes = Math.Max(minutes, options.MarginalHoldingMinutes);
            reasons.Add($"destination marginal at the ETA ({Describe(destination!)})");
        }

        if (altBelow)
        {
            reasons.Add("the alternate is also below minima");
        }
        else if (altMarginal)
        {
            reasons.Add("alternate weather also marginal");
        }

        string? legality = null;
        if (destBelow)
        {
            legality = altBelow
                ? "Destination below minima at ETA — an alternate is required, and the alternate is also below minima."
                : "Destination below minima at ETA — an alternate is required.";
        }

        var extraKg = minutes > 0 ? Math.Ceiling(minutes / 60.0 * options.HoldingBurnKgPerHour / 100.0) * 100 : 0;
        return new FuelDecision(
            minutes > 0 ? FuelRecommendation.Holding : FuelRecommendation.None,
            minutes, extraKg, reasons, destBelow, destMarginal, altBelow, altMarginal, legality, minima, destination, alternate);
    }

    /// <summary>The captain's question: "Destination TAF has thunderstorms around our arrival —
    /// recommend 20 minutes holding, 700 kilos. Take it?"</summary>
    public static string QuestionPrompt(FuelDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var reason = decision.Reasons.Count == 0 ? "The weather" : Capitalize(decision.Reasons[0]);
        return $"{reason} — recommend {decision.HoldingMinutes} minutes holding, {Kilos(decision.ExtraKg)} kilos. Take it?";
    }

    /// <summary>The on-request answer ("fuel decision"): the recommendation or "no extra fuel
    /// recommended", then the legality line when there is one.</summary>
    public static string Spoken(FuelDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var text = decision.Recommendation == FuelRecommendation.None
            ? decision.Destination is null
                ? "No destination weather to base a fuel decision on."
                : $"Destination {(decision.Destination.FromTaf ? "forecast" : "weather")} fine at the ETA — no extra fuel recommended."
            : $"{Capitalize(string.Join(", ", decision.Reasons))} — recommend {decision.HoldingMinutes} minutes holding, {Kilos(decision.ExtraKg)} kilos.";
        return decision.LegalityLine is { } legality ? $"{text} {legality}" : text;
    }

    /// <summary>Every figure the spoken lines carry.</summary>
    public static IEnumerable<double> Numbers(FuelDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        yield return decision.HoldingMinutes;
        yield return decision.ExtraKg;
        yield return decision.ExtraKg / 1000.0;
        foreach (var outlook in new[] { decision.Destination, decision.Alternate })
        {
            if (outlook?.VisibilityM is { } v)
            {
                yield return v;
            }

            if (outlook?.CeilingFt is { } c)
            {
                yield return c;
            }
        }
    }

    private static (bool Below, bool Marginal) Judge(ArrivalOutlook? outlook, LandingMinima minima)
    {
        if (outlook is null)
        {
            return (false, false);
        }

        var below = (outlook.CeilingFt is { } c && c < minima.CeilingFt) || (outlook.VisibilityM is { } v && v < minima.VisibilityM);
        var marginal = !below
            && ((outlook.CeilingFt is { } c2 && c2 < minima.CeilingFt + MarginalCeilingMarginFt)
                || (outlook.VisibilityM is { } v2 && v2 < minima.VisibilityM + MarginalVisibilityMarginM));
        return (below, marginal);
    }

    private static string Describe(ArrivalOutlook outlook)
    {
        var parts = new List<string>();
        if (outlook.CeilingFt is { } c)
        {
            parts.Add($"ceiling {c} feet");
        }

        if (outlook.VisibilityM is { } v)
        {
            parts.Add($"visibility {v} metres");
        }

        return parts.Count == 0 ? "no figures" : string.Join(", ", parts);
    }

    private static string Kilos(double kg) => Math.Round(kg).ToString("0", CultureInfo.InvariantCulture);

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}

/// <summary>Observation test without taking a dependency on the briefing's conditions module
/// (which lives on another branch of the November batch).</summary>
internal static class MetarConditionsLight
{
    public static bool HasObservation(WxFacts weather)
        => weather.RawMetar is not null || weather.WindDirDeg is not null || weather.TemperatureC is not null
            || weather.VisibilityMeters is not null || weather.QnhHpa is not null || weather.RawTaf is not null;
}
