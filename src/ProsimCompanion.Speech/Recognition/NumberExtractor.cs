using System.Globalization;
using System.Text;

namespace ProsimCompanion.Speech.Recognition;

/// <summary>
/// Extracts a number from anywhere within a spoken/transcribed utterance, so a readback like
/// "QNH 1017 set" or "one zero one seven set" yields 1017 for verification. Handles BOTH
/// Arabic numerals (what faster-whisper emits) and aviation digit-words, ignoring surrounding
/// words and trailing punctuation. Digit-word runs are read digit-by-digit (aviation
/// convention: "one zero one seven" = 1017, not one-thousand-seventeen). Pure and
/// deterministic; ported verbatim from Prosim2FO.
/// </summary>
public static class NumberExtractor
{
    private static readonly Dictionary<string, int> DigitWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"] = 0,
        ["oh"] = 0,
        ["one"] = 1,
        ["two"] = 2,
        ["three"] = 3,
        ["four"] = 4,
        ["five"] = 5,
        ["six"] = 6,
        ["seven"] = 7,
        ["eight"] = 8,
        ["nine"] = 9,
        ["niner"] = 9,
    };

    private static readonly char[] TrailingPunct = ['.', ',', '!', '?', ';', ':'];

    /// <summary>Words a closed number grammar should accept.</summary>
    public static IReadOnlyList<string> GrammarWords { get; } =
        [.. DigitWords.Keys.Distinct(StringComparer.OrdinalIgnoreCase), "decimal", "point"];

    /// <summary>Finds the first number in the utterance. Returns false if none.</summary>
    public static bool TryExtract(string utterance, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(utterance))
        {
            return false;
        }

        var tokens = utterance.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        // Pass 1: a bare Arabic numeral anywhere ("1017", "121.5", "29.92") — the whisper case.
        foreach (var raw in tokens)
        {
            var token = raw.TrimEnd(TrailingPunct).Replace(",", "", StringComparison.Ordinal);
            if (token.Length == 0)
            {
                continue;
            }

            if (LooksNumeric(token)
                && double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }
        }

        // Pass 2: the first run of aviation digit-words, read digit-by-digit.
        var digits = new StringBuilder();
        var inRun = false;
        foreach (var raw in tokens)
        {
            var word = raw.TrimEnd(TrailingPunct).ToLowerInvariant();
            if (DigitWords.TryGetValue(word, out var digit))
            {
                digits.Append((char)('0' + digit));
                inRun = true;
                continue;
            }

            if (word is "decimal" or "point")
            {
                if (inRun && !digits.ToString().Contains('.', StringComparison.Ordinal))
                {
                    digits.Append('.');
                }

                continue;
            }

            if (inRun)
            {
                break; // a non-number word ends the run
            }
        }

        var s = digits.ToString();
        return s.Any(c => c is >= '0' and <= '9')
            && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool LooksNumeric(string token)
    {
        var hasDigit = false;
        foreach (var c in token)
        {
            if (c is >= '0' and <= '9')
            {
                hasDigit = true;
                continue;
            }

            if (c is '.' or '-' or '+')
            {
                continue;
            }

            return false;
        }

        return hasDigit;
    }
}
