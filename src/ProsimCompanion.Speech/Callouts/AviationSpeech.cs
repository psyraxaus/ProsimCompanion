using System.Text.RegularExpressions;

namespace ProsimCompanion.Speech.Callouts;

/// <summary>Digit-word rendering for spoken aviation numbers (rules carried from Prosim2FO).</summary>
public static class Aviation
{
    /// <summary>ICAO digit words — note 9 is "niner". Aliases the canonical Core table so the
    /// recognition grammars here and Core's identifier phonetics can never drift apart.</summary>
    public static readonly string[] DigitWords = Core.Speech.NatoPhonetics.DigitWords;

    /// <summary>"1013" → "one zero one three"; "29.92" → "two niner decimal niner two".
    /// Non-digit characters other than '.' are silently dropped.</summary>
    public static string ToDigits(string s)
    {
        ArgumentNullException.ThrowIfNull(s);

        var parts = new List<string>(s.Length);
        foreach (var c in s)
        {
            if (c is >= '0' and <= '9')
            {
                parts.Add(DigitWords[c - '0']);
            }
            else if (c == '.')
            {
                parts.Add("decimal");
            }
        }

        return string.Join(" ", parts);
    }

    /// <summary>Words a closed digit grammar accepts (digit words + decimal/point).</summary>
    public static IReadOnlyList<string> NumberGrammarWords { get; } =
        [.. DigitWords, "decimal", "point"];

    /// <summary>"one zero one three" → 1013; "two niner decimal niner two" → 29.92. Strict:
    /// the WHOLE utterance must be a number (use NumberExtractor for embedded numbers).</summary>
    public static bool TryParseSpoken(string spoken, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(spoken))
        {
            return false;
        }

        var digits = new System.Text.StringBuilder();
        foreach (var word in spoken.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = Array.IndexOf(DigitWords, word.ToLowerInvariant());
            if (index >= 0)
            {
                digits.Append((char)('0' + index));
                continue;
            }

            if (word.Equals("decimal", StringComparison.OrdinalIgnoreCase)
                || word.Equals("point", StringComparison.OrdinalIgnoreCase))
            {
                digits.Append('.');
                continue;
            }

            return false; // a non-number word → not a pure number
        }

        return digits.Length > 0 && double.TryParse(
            digits.ToString(), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out value);
    }
}

/// <summary>
/// Normalizes text for TTS so aviation shorthand is spoken correctly ("FL350" was being read
/// as "Florida 350"). Conservative, whole-word matching only; idempotent by construction — its
/// own output contains no digits/abbreviations to re-match ("Q N H" has spaces, so \bQNH\b
/// misses it). Applied once in the render path BEFORE the TTS cache key is computed, so cached
/// and prewarmed phrases hit. Rules carried verbatim from Prosim2FO.
/// </summary>
public static class AviationSpeech
{
    private static Regex Rx(string pattern)
        => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string Digits(string s) => Aviation.ToDigits(s);

    private static string Side(string s) => s.ToUpperInvariant() switch
    {
        "L" => " left",
        "R" => " right",
        "C" => " center",
        _ => "",
    };

    // Pass 1 — numbers. Order matters: FL### runs first and consumes "FL350"; the bare-FL
    // abbreviation below only catches leftovers. Leading zeros are stripped for FL/QNH/QFE;
    // headings are zero-padded to three digits instead.
    private static readonly (Regex Rx, MatchEvaluator Evaluate)[] NumberRules =
    [
        (Rx(@"\bFL\s*0*(\d{2,3})\b"), m => "flight level " + Digits(m.Groups[1].Value)),
        (Rx(@"\b(transition level|flight level)\s+0*(\d{2,3})\b"),
            m => m.Groups[1].Value + " " + Digits(m.Groups[2].Value)),
        (Rx(@"\bheading\s+(\d{1,3})\b"), m => "heading " + Digits(m.Groups[1].Value.PadLeft(3, '0'))),
        (Rx(@"\b(squawk|transponder)\s+(\d{3,4})\b"), m => m.Groups[1].Value + " " + Digits(m.Groups[2].Value)),
        // Singular and plural: LLM briefing prose says "winds 270" as often as "wind 270"
        // (issue #69). The matched word is preserved so the plural respelling below applies.
        (Rx(@"\b(winds?)\s+(\d{3})\b"), m => m.Groups[1].Value + " " + Digits(m.Groups[2].Value)),
        (Rx(@"\bQNH\s+0*(\d{3,4})\b"), m => "Q N H " + Digits(m.Groups[1].Value)),
        (Rx(@"\bQFE\s+0*(\d{3,4})\b"), m => "Q F E " + Digits(m.Groups[1].Value)),
        (Rx(@"\b(\d{3}\.\d{1,3})\b"), m => Digits(m.Groups[1].Value)), // frequencies (e.g. 110.30)
        (Rx(@"\brunway\s+(\d{1,2})\s*([LRC])?\b"),
            m => "runway " + Digits(m.Groups[1].Value) + Side(m.Groups[2].Value)),
    ];

    // Pass 2 — abbreviations, whole-word only.
    private static readonly (Regex Rx, string Replacement)[] Abbreviations =
    [
        (Rx(@"\bILS\b"), "I L S"),
        (Rx(@"\bRVR\b"), "R V R"),
        (Rx(@"\bQNH\b"), "Q N H"),
        (Rx(@"\bQFE\b"), "Q F E"),
        (Rx(@"\bVOR\b"), "V O R"),
        (Rx(@"\bDME\b"), "D M E"),
        (Rx(@"\bNDB\b"), "N D B"),
        (Rx(@"\bADF\b"), "A D F"),
        (Rx(@"\bLOC\b"), "localizer"),
        (Rx(@"\bATIS\b"), "A T I S"),
        (Rx(@"\bMSA\b"), "M S A"),
        (Rx(@"\bMSL\b"), "mean sea level"),
        (Rx(@"\bAGL\b"), "above ground level"),
        (Rx(@"\bMDA\b"), "M D A"),
        (Rx(@"\bECAM\b"), "E CAM"),
        (Rx(@"\bMCDU\b"), "M C D U"),
        // kokoro reads "winds" as the verb /waɪndz/ (issue #69). "windz" is the most
        // phonetically explicit respelling that still reads as one word — "wind z" risks the
        // synthesizer spelling out a letter zed. Chosen without an audition; may need an
        // ear-check on the sim PC.
        (Rx(@"\bwinds\b"), "windz"),
        (Rx(@"\bFL\b"), "flight level"), // bare FL not followed by a number
        (Rx(@"\bft\b"), "feet"),
        (Rx(@"\bkts?\b"), "knots"),
        (Rx(@"\bnm\b"), "nautical miles"),
        (Rx(@"\bfpm\b"), "feet per minute"),
        (Rx(@"\bhPa\b"), "hectopascals"),
    ];

    public static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var s = text;
        foreach (var (rx, evaluate) in NumberRules)
        {
            s = rx.Replace(s, evaluate);
        }

        foreach (var (rx, replacement) in Abbreviations)
        {
            s = rx.Replace(s, replacement);
        }

        return Regex.Replace(s, @"[ \t]{2,}", " ").Trim();
    }
}
