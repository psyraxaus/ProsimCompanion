using System.Text;

namespace ProsimCompanion.Speech.Recognition;

/// <summary>The best command match for some recognized text, with a 0..1 score.</summary>
public sealed record CommandMatch(string Command, double Score);

/// <summary>
/// Snaps free-form recognized text (e.g. from neural ASR) onto the nearest valid command in
/// the active vocabulary, combining normalized edit-distance (Levenshtein) with a phonetic
/// match (Double Metaphone) so accent-driven near-misses still resolve. Pure and
/// deterministic: (text, vocabulary, threshold) → match or null when nothing clears the
/// threshold — a wrong command is never fired. Ported from Prosim2FO (hybrid 0.5/0.5 weights).
/// </summary>
public static class CommandMatcher
{
    public static CommandMatch? Snap(string text, IReadOnlyList<string> vocabulary, double threshold)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);
        if (string.IsNullOrWhiteSpace(text) || vocabulary.Count == 0)
        {
            return null;
        }

        var textNorm = Normalize(text);
        if (textNorm.Length == 0)
        {
            return null;
        }

        // Whisper writes numbers either way ("flaps 1" / "flaps one"); the digit form scored
        // almost identically against "flaps one" and "flaps two" and the phonetic half had
        // nothing for a bare "1" — "Flaps 1" matched flaps TWO on 2026-08-29 (issue #119).
        // Score against the spoken-digit form too and keep the best.
        var textDigitsSpoken = SpeakSingleDigits(textNorm);

        CommandMatch? best = null;
        foreach (var phrase in vocabulary)
        {
            var phraseNorm = Normalize(phrase);
            if (phraseNorm.Length == 0)
            {
                continue;
            }

            var score = Score(textNorm, phraseNorm);
            if (!ReferenceEquals(textDigitsSpoken, textNorm))
            {
                score = Math.Max(score, Score(textDigitsSpoken, phraseNorm));
            }

            if (best is null || score > best.Score)
            {
                best = new CommandMatch(phrase, score);
            }
        }

        return best is not null && best.Score >= threshold ? best : null;
    }

    private static double Score(string textNorm, string phraseNorm)
        => 0.5 * LevenshteinSimilarity(textNorm, phraseNorm)
            + 0.5 * PhoneticSimilarity(textNorm, phraseNorm);

    /// <summary>Replaces standalone single-digit tokens with their spoken words
    /// ("flaps 1" → "flaps one"). Multi-digit tokens ("121", "350") are left alone — the
    /// number/frequency extractors depend on them. Returns the SAME instance when nothing
    /// changed, so callers can skip the second scoring pass.</summary>
    internal static string SpeakSingleDigits(string normalized)
    {
        ArgumentNullException.ThrowIfNull(normalized);

        string[] words = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"];
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var changed = false;
        for (var i = 0; i < tokens.Length; i++)
        {
            if (tokens[i].Length == 1 && tokens[i][0] is >= '0' and <= '9')
            {
                tokens[i] = words[tokens[i][0] - '0'];
                changed = true;
            }
        }

        return changed ? string.Join(' ', tokens) : normalized;
    }

    /// <summary>Lower-case, strip punctuation, collapse whitespace.</summary>
    public static string Normalize(string s)
    {
        ArgumentNullException.ThrowIfNull(s);

        var sb = new StringBuilder(s.Length);
        var lastSpace = false;
        foreach (var ch in s.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastSpace = false;
            }
            else if (!lastSpace)
            {
                sb.Append(' ');
                lastSpace = true;
            }
        }

        return sb.ToString().Trim();
    }

    private static double LevenshteinSimilarity(string a, string b)
    {
        var max = Math.Max(a.Length, b.Length);
        return max == 0 ? 1.0 : 1.0 - (double)Levenshtein(a, b) / max;
    }

    private static int Levenshtein(string a, string b)
    {
        int n = a.Length, m = b.Length;
        if (n == 0)
        {
            return m;
        }

        if (m == 0)
        {
            return n;
        }

        var prev = new int[m + 1];
        var curr = new int[m + 1];
        for (var j = 0; j <= m; j++)
        {
            prev[j] = j;
        }

        for (var i = 1; i <= n; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= m; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }

            (prev, curr) = (curr, prev);
        }

        return prev[m];
    }

    private static double PhoneticSimilarity(string a, string b)
    {
        var (ap, aa) = PhoneticKeys(a);
        var (bp, ba) = PhoneticKeys(b);

        double best = 0;
        foreach (var x in new[] { ap, aa })
        {
            foreach (var y in new[] { bp, ba })
            {
                best = Math.Max(best, LevenshteinSimilarity(x, y));
            }
        }

        return best;
    }

    /// <summary>Per-word Double Metaphone, joined — primary keys and alternate keys.</summary>
    private static (string Primary, string Alternate) PhoneticKeys(string normalized)
    {
        var primary = new List<string>();
        var alternate = new List<string>();
        foreach (var word in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var (p, s) = DoubleMetaphone.Encode(word);
            primary.Add(p);
            alternate.Add(string.IsNullOrEmpty(s) ? p : s);
        }

        return (string.Join(" ", primary), string.Join(" ", alternate));
    }
}
