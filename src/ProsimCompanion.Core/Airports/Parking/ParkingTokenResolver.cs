using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ProsimCompanion.Core.Airports.Parking;

/// <summary>Text helpers shared by every parking matcher.</summary>
public static class ParkingText
{
    /// <summary>Strip everything but letters and digits, uppercase — the comparison form for all
    /// parking tokens (so " Gate  313", "Gate 313" and "GATE313" are one string).</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToUpperInvariant(c));
            }
        }

        return builder.ToString();
    }
}

/// <summary>How a pilot's token was read: "W40" → letter W, number 40, no suffix.</summary>
/// <param name="Letter">Leading gate letter, or null ("313", "Stand 313").</param>
/// <param name="Number">The number.</param>
/// <param name="Suffix">Trailing letter(s) ("B" in "34B", "R" in "545R"), or "".</param>
/// <param name="FacilityWord">A leading facility word the pilot included ("Gate", "Stand",
/// "Parking", "Dock"), or null — informative only, GSX profiles do not carry it.</param>
public sealed record ParkingToken(char? Letter, int Number, string Suffix, string? FacilityWord)
{
    private static readonly Regex Shape = new(
        @"^\s*(?:(?<word>gate|stand|parking|park|dock|bay|position|pos|apron|ramp)\s*)?(?<letter>[A-Za-z])?\s*(?<number>\d{1,4})\s*(?<suffix>[A-Za-z]{1,2})?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Parses "W40", "w 40", "Gate W40", "Stand 313", "34B", "545R", "D-27".
    /// Null when the text is not a designator (e.g. a full "Terminal 1 | Gate 5" name).</summary>
    public static ParkingToken? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var cleaned = text.Replace("-", " ", StringComparison.Ordinal).Replace("_", " ", StringComparison.Ordinal);
        var match = Shape.Match(cleaned);
        if (!match.Success)
        {
            return null;
        }

        var number = int.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture);
        var letter = match.Groups["letter"].Success ? char.ToUpperInvariant(match.Groups["letter"].Value[0]) : (char?)null;
        var suffix = match.Groups["suffix"].Success ? match.Groups["suffix"].Value.ToUpperInvariant() : "";
        var word = match.Groups["word"].Success ? match.Groups["word"].Value : null;
        return new ParkingToken(letter, number, suffix, word);
    }
}

/// <summary>How sure the resolver is that a parking is the one the pilot meant.</summary>
public enum ParkingMatchConfidence
{
    /// <summary>Letter, number and suffix all agree (or the token names a plain-numbered stand
    /// and exactly one stand carries that number).</summary>
    Exact,

    /// <summary>The token's letter/number agree but the suffix was not given and the stand has
    /// one, or the stand is a lettered gate the pilot typed without its letter — one candidate.</summary>
    Likely,

    /// <summary>Several stands fit; the first is the best guess, the rest are alternatives.</summary>
    Ambiguous,
}

/// <summary>The resolver's answer for one token.</summary>
/// <param name="Parking">The best match.</param>
/// <param name="Confidence">How sure.</param>
/// <param name="Alternatives">Other stands that fit the token (empty unless ambiguous).</param>
/// <param name="GsxTokens">The identities to send to <c>gate.select</c>, best first: the parking
/// NUMBER as an integer (GSX's most robust key — the display template cannot hide it), then
/// GSX's gate name when the profile knows it, then the default scenery name.</param>
public sealed record ParkingMatch(
    AirportParking Parking,
    ParkingMatchConfidence Confidence,
    IReadOnlyList<AirportParking> Alternatives,
    IReadOnlyList<object> GsxTokens);

/// <summary>
/// Pure: maps what the pilot typed or said ("W40") to a stand of the catalogue, by scenery
/// identity rather than by GSX's display text. This is the fix for the EFHK W40 refusals of
/// 2026-10-03/04: the profile template there prints Gate W40 as "Gate 40", so the display
/// text never contained the W; the identity (GATE_W, 40) always did.
/// </summary>
public static class ParkingTokenResolver
{
    public static ParkingMatch? Resolve(AirportParkings? catalogue, string? token)
    {
        if (catalogue is null || catalogue.Parkings.Count == 0)
        {
            return null;
        }

        // 1. The pilot may have typed GSX's own name ("Gate 40", "Apron 1W (…) | Gate 40").
        var normalized = ParkingText.Normalize(token);
        if (normalized.Length > 0)
        {
            var byName = catalogue.Parkings
                .Where(p => ParkingText.Normalize(p.GsxUiName) == normalized
                    || ParkingText.Normalize(p.GsxGateName) == normalized
                    || ParkingText.Normalize(p.Identity.DefaultDisplayName) == normalized)
                .ToList();
            if (byName.Count == 1)
            {
                return Found(byName[0], ParkingMatchConfidence.Exact, []);
            }
        }

        // 2. The designator shape.
        if (ParkingToken.Parse(token) is not { } parsed)
        {
            return null;
        }

        var sameNumber = catalogue.Parkings.Where(p => p.Identity.Number == parsed.Number).ToList();
        if (sameNumber.Count == 0)
        {
            return null;
        }

        if (parsed.Letter is { } letter)
        {
            var lettered = sameNumber.Where(p => p.Identity.GateLetter == letter).ToList();
            var exact = lettered.Where(p => SuffixEquals(p, parsed.Suffix)).ToList();
            if (exact.Count == 1)
            {
                return Found(exact[0], ParkingMatchConfidence.Exact, []);
            }
            if (exact.Count > 1)
            {
                return Found(exact[0], ParkingMatchConfidence.Ambiguous, exact.Skip(1).ToList());
            }
            if (parsed.Suffix.Length == 0 && lettered.Count == 1)
            {
                // "W34" for a stand that only exists as W34B.
                return Found(lettered[0], ParkingMatchConfidence.Likely, []);
            }
            if (lettered.Count > 1)
            {
                return Found(lettered[0], ParkingMatchConfidence.Ambiguous, lettered.Skip(1).ToList());
            }

            // The letter may have been a facility hint the pilot added ("S 49" at a stand that
            // is plain GATE 49); fall through to the unlettered search.
        }

        var unlettered = sameNumber.Where(p => SuffixEquals(p, parsed.Suffix)).ToList();
        if (unlettered.Count == 0 && parsed.Suffix.Length == 0)
        {
            unlettered = sameNumber;
        }

        if (unlettered.Count == 0)
        {
            return null;
        }

        if (unlettered.Count == 1)
        {
            var only = unlettered[0];
            var confidence = only.Identity.GateLetter is null && SuffixEquals(only, parsed.Suffix)
                ? ParkingMatchConfidence.Exact
                : ParkingMatchConfidence.Likely;
            return Found(only, confidence, []);
        }

        // Several groups share the number: prefer the plain (unlettered) group, then a GATE over
        // a PARKING — a pilot who types a bare "40" at an airport with Gate 40 and Stand 40
        // almost always means the gate.
        var ranked = unlettered
            .OrderBy(p => p.Identity.GateLetter is null ? 0 : 1)
            .ThenBy(p => p.Identity.Name == ParkingName.Gate ? 0 : 1)
            .ToList();
        return Found(ranked[0], ParkingMatchConfidence.Ambiguous, ranked.Skip(1).ToList());
    }

    /// <summary>The <c>gate.select</c> identities for a stand, best first (see
    /// <see cref="ParkingMatch.GsxTokens"/>).</summary>
    public static IReadOnlyList<object> GsxTokensFor(AirportParking parking)
    {
        ArgumentNullException.ThrowIfNull(parking);
        var tokens = new List<object> { parking.Identity.Number };
        if (!string.IsNullOrWhiteSpace(parking.GsxGateName))
        {
            tokens.Add(parking.GsxGateName.Trim());
        }
        if (!string.IsNullOrWhiteSpace(parking.GsxUiName))
        {
            tokens.Add(parking.GsxUiName.Trim());
        }
        tokens.Add(parking.Identity.DefaultDisplayName);
        return tokens;
    }

    private static bool SuffixEquals(AirportParking parking, string suffix)
        => string.Equals(parking.Identity.Suffix, suffix, StringComparison.OrdinalIgnoreCase);

    private static ParkingMatch Found(AirportParking parking, ParkingMatchConfidence confidence, IReadOnlyList<AirportParking> alternatives)
        => new(parking, confidence, alternatives, GsxTokensFor(parking));
}
