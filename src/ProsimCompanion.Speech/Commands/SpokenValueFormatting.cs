using ProsimCompanion.Speech.Callouts;

namespace ProsimCompanion.Speech.Commands;

/// <summary>Live values behind the spoken-query tokens, already rendered as speakable text
/// ("one zero one three", "standard", "not set", "unavailable"). Built by the feature from
/// cached dataref subscriptions; the formatting itself is pure (see
/// <see cref="SpokenValueFormatting"/>) so it tests without ProSim.</summary>
public sealed record CommandTokenValues(
    string Altimeter,
    string Qnh,
    string V1,
    string Vr,
    string V2,
    string Flex,
    string Runway,
    string FlightLevel,
    string TakeoffConfig = "unavailable",
    string FuelQuantity = "unavailable")
{
    public static CommandTokenValues Unavailable { get; } = new(
        "unavailable", "unavailable", "unavailable", "unavailable",
        "unavailable", "unavailable", "unavailable", "unavailable");
}

/// <summary>
/// Pure rendering for the spoken-query tokens carried from Prosim2FO's CalloutValues:
/// {altimeter}/{qnh} (F/O FCU baro, aviation digits, "standard" on STD), {v1}/{vr}/{v2}/{flex}
/// (MCDU take-off perf) and {runway} ("16R" → "one six right").
/// </summary>
public static class SpokenValueFormatting
{
    /// <summary>Replaces every known {token} in the text. Unknown braces pass through.</summary>
    public static string ApplyTokens(string text, CommandTokenValues values)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(values);

        if (!text.Contains('{'))
        {
            return text;
        }

        return text
            .Replace("{altimeter}", values.Altimeter, StringComparison.Ordinal)
            .Replace("{qnh}", values.Qnh, StringComparison.Ordinal)
            .Replace("{v1}", values.V1, StringComparison.Ordinal)
            .Replace("{vr}", values.Vr, StringComparison.Ordinal)
            .Replace("{v2}", values.V2, StringComparison.Ordinal)
            .Replace("{flex}", values.Flex, StringComparison.Ordinal)
            .Replace("{runway}", values.Runway, StringComparison.Ordinal)
            .Replace("{flightLevel}", values.FlightLevel, StringComparison.Ordinal)
            .Replace("{takeoffConfig}", values.TakeoffConfig, StringComparison.Ordinal)
            .Replace("{fuelQuantity}", values.FuelQuantity, StringComparison.Ordinal);
    }

    /// <summary>Exact avoirdupois factor shared with the display-unit service.</summary>
    private const double LbPerKg = Core.State.DisplayUnitService.LbPerKg;

    /// <summary>Total fuel on board as a spoken figure with its unit (issue #129):
    /// "8540 kilograms" / "18830 pounds". The unit follows ProSim's configured weight unit
    /// (<c>system.config.Units.Weight</c>, "LBS" ⇒ pounds — the same rule the display-unit
    /// service applies) so the FO reads what the ECAM shows. Rounded to the nearest 10 —
    /// nobody reads back single kilograms. Left as plain digits (not aviation digit words):
    /// a fuel figure is a quantity, spoken "eight thousand five hundred forty", never
    /// "eight five four zero". Null (dataref not yet pushed) reads "unavailable".</summary>
    public static string FuelQuantity(double? kg, string? weightUnit)
    {
        if (kg is null || !double.IsFinite(kg.Value) || kg.Value < 0)
        {
            return "unavailable";
        }

        var pounds = !string.IsNullOrWhiteSpace(weightUnit)
            && weightUnit.Contains("LB", StringComparison.OrdinalIgnoreCase);
        var figure = pounds ? kg.Value * LbPerKg : kg.Value;
        var rounded = (int)(Math.Round(figure / 10) * 10);
        return rounded.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + (pounds ? " pounds" : " kilograms");
    }

    /// <summary>F/O baro as spoken digits. <paramref name="std"/> null = no data yet
    /// ("unavailable"); true = "standard". hPa reads whole ("one zero one three"); inHg reads
    /// hundredths (29.92 → "two niner niner two"), matching the predecessor.</summary>
    public static string Altimeter(bool? std, bool hpaMode, double hpa, double inches)
    {
        if (std is null)
        {
            return "unavailable";
        }

        if (std.Value)
        {
            return "standard";
        }

        return hpaMode
            ? Aviation.ToDigits(((int)Math.Round(hpa)).ToString(System.Globalization.CultureInfo.InvariantCulture))
            : Aviation.ToDigits(((int)Math.Round(inches * 100)).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>MCDU perf value as digits; 0/negative = "not set"; null = "unavailable".</summary>
    public static string Speed(double? value)
    {
        if (value is null)
        {
            return "unavailable";
        }

        var v = (int)Math.Round(value.Value);
        return v > 0 ? Aviation.ToDigits(v.ToString(System.Globalization.CultureInfo.InvariantCulture)) : "not set";
    }

    /// <summary>"16R" → "one six right"; null/blank → "unavailable". Pronunciation is the
    /// shared spoken-text module's (campaign #81); the absence wording is this caller's.</summary>
    public static string Runway(string? runway)
        => Core.Speech.SpokenText.RunwayOrNull(runway) ?? "unavailable";

    /// <summary>Altitude (ft) → flight-level digits: 7 500 → "seven five" so the template's
    /// "passing flight level {flightLevel}" reads per SOP (issue #49). The words "flight
    /// level" stay in the template — the token is only the number, like every other token.
    /// Null or on-the-deck values read "unavailable".</summary>
    public static string FlightLevel(double? altitudeFt)
    {
        if (altitudeFt is null)
        {
            return "unavailable";
        }

        var fl = (int)Math.Round(altitudeFt.Value / 100);
        return fl > 0
            ? Aviation.ToDigits(fl.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : "unavailable";
    }
}
