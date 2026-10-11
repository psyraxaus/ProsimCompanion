using System.Globalization;
using System.Text.RegularExpressions;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Monitoring;

/// <summary>Which figure the captain read back.</summary>
public enum ReadbackKind
{
    Altimeter,
    VSpeeds,
    Runway,
    Minimums,
}

/// <summary>A parsed read-back: the figures the captain said.</summary>
/// <param name="Numbers">Altimeter / minimums: one value; V-speeds: up to three (V1, VR, V2).</param>
/// <param name="Runway">Runway designator as said ("27R"), runway read-backs only.</param>
/// <param name="Standard">Altimeter only: the captain said "standard" / "STD" rather than a
/// figure (issue #148) — <see cref="StandardHpa"/> is the number compared.</param>
public sealed record ReadbackRequest(ReadbackKind Kind, IReadOnlyList<double> Numbers, string? Runway, bool Standard = false);

/// <summary>What the aircraft says. Nulls / zeros = not available.</summary>
public sealed record ReadbackActuals(
    double? BaroHpa,
    int FmsV1,
    int FmsVr,
    int FmsV2,
    string? FmsRunway,
    ArrivalMinima? Minima);

/// <summary>The FO's reply. <paramref name="Ok"/> = the read-back matched.</summary>
public sealed record ReadbackAnswer(bool Ok, string Text, string Outcome);

/// <summary>
/// Standalone read-backs (issue #148): the captain states a figure — "altimeter one zero one
/// three", "V speeds one four one, one four four, one four seven", "runway two seven right",
/// "minimums two one zero" — and the FO checks it against the aircraft the way a checklist
/// number item does (said vs. actual within a tolerance; <see cref="NumberExtractor"/> reads
/// the figure), answering "checked" or the value it reads. Pure: parse, then answer.
/// </summary>
public static partial class ReadbackCore
{
    /// <summary>Standard pressure, what "altimeter standard" / "STD" means (issue #148,
    /// 2026-10-10: "Read back altimeter standard." had no figure and went to the model).</summary>
    public const double StandardHpa = 1013.2;

    // An optional lead-in before every kind (issue #148, 2026-10-10: "Read back Altimeter
    // 1013." was anchored out and became a question to the language model). The text is
    // normalized first, so "read-back" and "readback" arrive as "read back" / "readback".
    private const string Prefix = @"^(?:(?:read\s*back|check|confirm)\s+)?";

    [GeneratedRegex(Prefix + @"(?:altimeter|qnh|q\s*n\s*h|baro)\b(?<rest>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AltimeterPattern();

    [GeneratedRegex(Prefix + @"(?:v\s*speeds?|vee\s*speeds?|v\s*one|vee\s*one)\b(?<rest>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VSpeedsPattern();

    [GeneratedRegex(Prefix + @"runway\b(?<rest>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RunwayPattern();

    [GeneratedRegex(Prefix + @"(?:minimums?|minima|decision altitude|decision height|minimum descent altitude)\b(?<rest>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MinimumsPattern();

    [GeneratedRegex(@"^\s*(?:standard|std|standard pressure)\s*(?:set)?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StandardPattern();

    [GeneratedRegex(@"^read\s*back\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReadBackPrefixPattern();

    /// <summary>True when the utterance opens with "read back" (any spelling) — the router
    /// sends such an utterance to the read-back feature and never to the question handler.</summary>
    public static bool HasReadBackPrefix(string? utterance)
        => !string.IsNullOrWhiteSpace(utterance) && ReadBackPrefixPattern().IsMatch(CommandMatcher.Normalize(utterance));

    [GeneratedRegex(@",|\band\b|\brotate\b|\bv\s*(?:r|two|2)\b|\bvee\s*(?:r|two)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VSpeedSeparators();

    [GeneratedRegex(@"\b(left|right|center|centre)\b|(?<=\d)\s*([lrc])\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RunwaySidePattern();

    /// <summary>The lead-in words the recognizer's closed grammar gets; the figures come from
    /// its digit grammar.</summary>
    public static IReadOnlyList<string> LeadIns { get; } =
    [
        "altimeter", "qnh", "v speeds", "v one", "runway", "minimums", "decision altitude", "decision height",
        // The "read back" forms and the figure-less standard setting (issue #148).
        "read back altimeter", "read back v speeds", "read back runway", "read back minimums",
        "altimeter standard", "read back altimeter standard",
    ];

    /// <summary>A read-back when the utterance is one — a lead-in followed by a figure. Null
    /// for anything else (including a bare lead-in such as "minimums check", which another
    /// feature owns).</summary>
    public static ReadbackRequest? Parse(string utterance)
    {
        if (string.IsNullOrWhiteSpace(utterance))
        {
            return null;
        }

        var text = CommandMatcher.Normalize(utterance);

        if (AltimeterPattern().Match(text) is { Success: true } altimeter)
        {
            if (StandardPattern().IsMatch(altimeter.Groups["rest"].Value))
            {
                return new ReadbackRequest(ReadbackKind.Altimeter, [StandardHpa], null, Standard: true);
            }

            if (First(altimeter.Groups["rest"].Value) is { } hpa)
            {
                return new ReadbackRequest(ReadbackKind.Altimeter, [hpa], null);
            }
        }

        if (VSpeedsPattern().Match(text) is { Success: true } speeds)
        {
            var numbers = VSpeedSeparators().Split(speeds.Groups["rest"].Value)
                .SelectMany(NumberExtractor.ExtractAll)
                .Take(3)
                .ToList();
            return numbers.Count > 0 ? new ReadbackRequest(ReadbackKind.VSpeeds, numbers, null) : null;
        }

        if (RunwayPattern().Match(text) is { Success: true } runway
            && First(runway.Groups["rest"].Value) is { } number
            && number is >= 1 and <= 36)
        {
            var side = RunwaySidePattern().Match(runway.Groups["rest"].Value);
            var letter = side.Success
                ? (side.Groups[1].Success ? side.Groups[1].Value : side.Groups[2].Value).ToUpperInvariant()[0] switch
                {
                    'L' => "L",
                    'R' => "R",
                    _ => "C",
                }
                : "";
            return new ReadbackRequest(ReadbackKind.Runway, [number], ((int)number).ToString("00", CultureInfo.InvariantCulture) + letter);
        }

        if (MinimumsPattern().Match(text) is { Success: true } minimums
            && First(minimums.Groups["rest"].Value) is { } feet)
        {
            return new ReadbackRequest(ReadbackKind.Minimums, [feet], null);
        }

        return null;
    }

    public static ReadbackAnswer Answer(ReadbackRequest request, ReadbackActuals actuals, ReadbackOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actuals);
        ArgumentNullException.ThrowIfNull(options);

        switch (request.Kind)
        {
            case ReadbackKind.Altimeter:
            {
                if (actuals.BaroHpa is not { } baro || baro <= 0)
                {
                    return new ReadbackAnswer(false, "I can't read the altimeter setting.", "unavailable");
                }

                var spoken = Callouts.Aviation.ToDigits(Math.Round(baro).ToString("0", CultureInfo.InvariantCulture));
                var ok = Math.Abs(request.Numbers[0] - baro) <= options.AltimeterToleranceHpa;
                if (request.Standard)
                {
                    // "Standard" is read against 1013.2 with the same tolerance (a baro of
                    // 1013 or 1013.25 both pass); the mismatch quotes the setting read.
                    return ok
                        ? new ReadbackAnswer(true, "Standard, checked.", "checked")
                        : new ReadbackAnswer(false, $"Negative. I read QNH {spoken}, not standard.", "mismatch");
                }

                return ok
                    ? new ReadbackAnswer(true, $"QNH {spoken}, checked.", "checked")
                    : new ReadbackAnswer(false, $"Negative. I read QNH {spoken}.", "mismatch");
            }

            case ReadbackKind.VSpeeds:
            {
                if (actuals.FmsV1 <= 0 || actuals.FmsVr <= 0 || actuals.FmsV2 <= 0)
                {
                    return new ReadbackAnswer(false, "V speeds are not in the FMS.", "unavailable");
                }

                if (request.Numbers.Count < 3)
                {
                    return new ReadbackAnswer(false, "Say V one, rotate and V two.", "incomplete");
                }

                var fms = $"V one {Digits(actuals.FmsV1)}, rotate {Digits(actuals.FmsVr)}, V two {Digits(actuals.FmsV2)}";
                var ok = Math.Abs(request.Numbers[0] - actuals.FmsV1) <= options.VSpeedToleranceKt
                    && Math.Abs(request.Numbers[1] - actuals.FmsVr) <= options.VSpeedToleranceKt
                    && Math.Abs(request.Numbers[2] - actuals.FmsV2) <= options.VSpeedToleranceKt;
                return ok
                    ? new ReadbackAnswer(true, $"{fms}, checked.", "checked")
                    : new ReadbackAnswer(false, $"Negative. FMS has {fms}.", "mismatch");
            }

            case ReadbackKind.Runway:
            {
                var fmsRunway = NormalizeRunway(actuals.FmsRunway);
                if (fmsRunway is null)
                {
                    return new ReadbackAnswer(false, "No runway in the FMS.", "unavailable");
                }

                var spoken = Core.Speech.SpokenText.RunwayOrNull(fmsRunway) ?? fmsRunway;
                return string.Equals(NormalizeRunway(request.Runway), fmsRunway, StringComparison.Ordinal)
                    ? new ReadbackAnswer(true, $"Runway {spoken}, checked.", "checked")
                    : new ReadbackAnswer(false, $"Negative. FMS has runway {spoken}.", "mismatch");
            }

            default:
            {
                if (actuals.Minima is not { } minima)
                {
                    return new ReadbackAnswer(false, "Minimums not briefed.", "unavailable");
                }

                var callout = Briefings.BriefingComposer.MinimaCallout(minima);
                return Math.Abs(request.Numbers[0] - minima.AltitudeFt) <= options.MinimumsToleranceFt
                    ? new ReadbackAnswer(true, $"Minimums, {callout}, checked.", "checked")
                    : new ReadbackAnswer(false, $"Negative. Briefed minimums are {callout}.", "mismatch");
            }
        }
    }

    /// <summary>"RW27R" / "27r" / "7R" → "27R" / "07R"; null when there is no number.</summary>
    public static string? NormalizeRunway(string? runway)
    {
        if (string.IsNullOrWhiteSpace(runway))
        {
            return null;
        }

        var text = runway.Trim().ToUpperInvariant();
        if (text.StartsWith("RW", StringComparison.Ordinal))
        {
            text = text[2..];
        }

        var digits = new string(text.TakeWhile(char.IsAsciiDigit).ToArray());
        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }

        var side = text.Skip(digits.Length).FirstOrDefault(c => c is 'L' or 'R' or 'C');
        return number.ToString("00", CultureInfo.InvariantCulture) + (side == default ? "" : side.ToString());
    }

    private static string Digits(int value) => Callouts.Aviation.ToDigits(value.ToString(CultureInfo.InvariantCulture));

    private static double? First(string text) => NumberExtractor.ExtractAll(text) is [var first, ..] ? first : null;
}
