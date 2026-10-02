using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ProsimCompanion.Speech.Llm;

/// <summary>
/// Turns numbers a model wrote as WORDS back into digits, so <see cref="NumberVerifier"/> can
/// see them (issue #147). The briefing prompt tells the model to spell many numbers for the
/// synthesizer — "heading one six three", "one one eight decimal one zero" — and the verifier
/// only reads digit tokens, so without this pass a spelled number was never checked at all.
/// <para>
/// Two forms are read: digit-by-digit runs ("one six three" → 163, "zero nine zero" → 090,
/// "one one eight decimal one zero" → 118.10) and magnitudes ("five thousand" → 5000,
/// "one hundred and fifty two" → 152, "fifteen hundred" → 1500, "three point five" → 3.5).
/// The vocabulary is strict on purpose: this reads prose, not speech recognition, so the
/// homophones the ASR parser tolerates ("to", "for", "oh") are ordinary words here.
/// A lone "one" … "nine" stays a word unless <c>includeSingles</c> is set — "one of the"
/// is not a number, and the verifier ignores one- and two-digit values anyway.
/// </para>
/// Only the text handed to the verifier is rewritten; what is spoken is the model's own text.
/// </summary>
public static partial class SpokenNumberText
{
    private static readonly Dictionary<string, int> Digits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5,
        ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["niner"] = 9,
    };

    private static readonly Dictionary<string, int> TensTeens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14,
        ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19,
        ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50,
        ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
    };

    [GeneratedRegex(@"[A-Za-z]+|[^A-Za-z]+")]
    private static partial Regex TokenPattern();

    /// <summary>Rewrites every spelled number in <paramref name="text"/> as digits.</summary>
    /// <param name="includeSingles">Also rewrite a lone digit word ("one defect" → "1 defect").
    /// For matching what was said, never for verification.</param>
    public static string ToDigits(string text, bool includeSingles = false)
    {
        ArgumentNullException.ThrowIfNull(text);

        var tokens = TokenPattern().Matches(text).Select(m => m.Value).ToList();
        var output = new StringBuilder(text.Length);
        var i = 0;
        while (i < tokens.Count)
        {
            if (!IsNumberWord(tokens[i]))
            {
                output.Append(tokens[i]);
                i++;
                continue;
            }

            var run = ReadRun(tokens, i);
            var digits = Render(run.Whole, run.Fraction, includeSingles);
            if (digits is null)
            {
                output.Append(tokens[i]);
                i++;
                continue;
            }

            output.Append(digits);
            i = run.NextToken;
        }

        return output.ToString();
    }

    private sealed record Run(List<string> Whole, List<string> Fraction, int NextToken);

    /// <summary>Collects the number words from <paramref name="start"/>: joined by spaces or
    /// hyphens only, "and" bridging inside a magnitude, one "decimal"/"point" before digits.</summary>
    private static Run ReadRun(List<string> tokens, int start)
    {
        var whole = new List<string> { tokens[start] };
        var fraction = new List<string>();
        var inFraction = false;
        var last = start;
        var i = start + 1;

        while (i + 1 < tokens.Count && IsJoiner(tokens[i]))
        {
            var word = tokens[i + 1];
            if (inFraction)
            {
                if (!Digits.ContainsKey(word))
                {
                    break;
                }

                fraction.Add(word);
            }
            else if (IsNumberWord(word))
            {
                whole.Add(word);
            }
            else if (IsDecimalWord(word) && NextWord(tokens, i + 1) is { } after && Digits.ContainsKey(after))
            {
                inFraction = true;
            }
            else if (word.Equals("and", StringComparison.OrdinalIgnoreCase)
                && IsScale(whole[^1])
                && NextWord(tokens, i + 1) is { } following && IsNumberWord(following))
            {
                // "one hundred and fifty": the bridge is dropped, the run goes on.
            }
            else
            {
                break;
            }

            last = i + 1;
            i += 2;
        }

        return new Run(whole, fraction, last + 1);
    }

    private static string? Render(List<string> whole, List<string> fraction, bool includeSingles)
    {
        string integer;
        if (whole.Any(w => TensTeens.ContainsKey(w) || IsScale(w)))
        {
            if (Magnitude(whole) is not { } value)
            {
                return null;
            }

            integer = value.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            if (whole.Count == 1 && fraction.Count == 0 && !includeSingles)
            {
                return null;
            }

            // Digit by digit: leading zeros are part of what was said ("zero nine zero").
            integer = string.Concat(whole.Select(w => Digits[w].ToString(CultureInfo.InvariantCulture)));
        }

        return fraction.Count == 0
            ? integer
            : integer + "." + string.Concat(fraction.Select(w => Digits[w].ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>"two thousand five hundred" → 2500, "fifteen hundred" → 1500, "twenty five"
    /// → 25. Null when the words do not make one number ("hundred hundred").</summary>
    private static long? Magnitude(List<string> words)
    {
        long total = 0;
        long current = 0;
        foreach (var word in words)
        {
            if (Digits.TryGetValue(word, out var digit))
            {
                current += digit;
            }
            else if (TensTeens.TryGetValue(word, out var tens))
            {
                current += tens;
            }
            else if (word.Equals("hundred", StringComparison.OrdinalIgnoreCase))
            {
                if (current >= 100)
                {
                    return null;
                }

                current = (current == 0 ? 1 : current) * 100;
            }
            else
            {
                // thousand
                total += (current == 0 ? 1 : current) * 1000;
                current = 0;
            }
        }

        return total + current;
    }

    private static bool IsNumberWord(string token)
        => Digits.ContainsKey(token) || TensTeens.ContainsKey(token) || IsScale(token);

    private static bool IsScale(string token)
        => token.Equals("hundred", StringComparison.OrdinalIgnoreCase)
            || token.Equals("thousand", StringComparison.OrdinalIgnoreCase);

    private static bool IsDecimalWord(string token)
        => token.Equals("decimal", StringComparison.OrdinalIgnoreCase)
            || token.Equals("point", StringComparison.OrdinalIgnoreCase);

    /// <summary>Spaces and hyphens join number words; any other punctuation ends the number.</summary>
    private static bool IsJoiner(string token)
        => token.Length > 0 && token.All(c => c is ' ' or '-' or ' ');

    private static string? NextWord(List<string> tokens, int wordIndex)
        => wordIndex + 2 < tokens.Count && IsJoiner(tokens[wordIndex + 1]) ? tokens[wordIndex + 2] : null;
}
