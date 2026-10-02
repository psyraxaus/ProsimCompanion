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

    /// <summary>
    /// Every number in the utterance, in order (issue #148 read-backs: "one four one, one four
    /// four, one four seven"). A run of digit-words is one number until a non-number word ends
    /// it; "hundred" / "thousand" scale the digits before them ("two hundred" = 200, "two
    /// thousand five hundred" = 2500); Arabic numerals are one number each. A digit-word run
    /// of six or more digits in a multiple of three — three speeds said without a pause — is
    /// split into three-digit numbers, since no aviation read-back figure has six digits.
    /// </summary>
    public static IReadOnlyList<double> ExtractAll(string utterance)
    {
        if (string.IsNullOrWhiteSpace(utterance))
        {
            return [];
        }

        var numbers = new List<double>();
        var run = new StringBuilder();
        var scaled = 0.0;
        var any = false;

        void Flush()
        {
            if (!any)
            {
                return;
            }

            var s = run.ToString();
            var tail = s.Any(c => c is >= '0' and <= '9')
                && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : 0;
            if (scaled == 0 && s.Length >= 6 && s.Length % 3 == 0 && !s.Contains('.', StringComparison.Ordinal))
            {
                for (var i = 0; i < s.Length; i += 3)
                {
                    numbers.Add(double.Parse(s.AsSpan(i, 3), CultureInfo.InvariantCulture));
                }
            }
            else
            {
                numbers.Add(scaled + tail);
            }

            run.Clear();
            scaled = 0;
            any = false;
        }

        foreach (var raw in utterance.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var word = raw.TrimEnd(TrailingPunct).ToLowerInvariant();
            if (word.Length == 0)
            {
                continue;
            }

            if (DigitWords.TryGetValue(word, out var digit))
            {
                run.Append((char)('0' + digit));
                any = true;
                continue;
            }

            if (word is "hundred" or "thousand")
            {
                var factor = word == "hundred" ? 100 : 1000;
                var digitsBefore = run.Length == 0 ? 1 : double.Parse(run.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture);
                scaled += digitsBefore * factor;
                run.Clear();
                any = true;
                continue;
            }

            if (word is "decimal" or "point")
            {
                if (any && !run.ToString().Contains('.', StringComparison.Ordinal))
                {
                    run.Append('.');
                }

                continue;
            }

            Flush();
            var token = word.Replace(",", "", StringComparison.Ordinal);
            if (LooksNumeric(token) && double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeral))
            {
                numbers.Add(numeral);
            }
        }

        Flush();
        return numbers;
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
