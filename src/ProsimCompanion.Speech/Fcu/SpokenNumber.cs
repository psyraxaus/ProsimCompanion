namespace ProsimCompanion.Speech.Fcu;

/// <summary>
/// Spoken-number parsing for ATC phraseology (Prosim2FO rules, homophone-tolerant for ASR),
/// PLUS the Arabic-numeral path the predecessor never closed — whisper commonly emits
/// "descend flight level 120", which its parser rejected; here <see cref="Recognition.NumberExtractor"/>
/// backstops both entry points.
/// </summary>
public static class SpokenNumber
{
    private static readonly Dictionary<string, int> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"] = 0, ["oh"] = 0, ["o"] = 0,
        ["one"] = 1,
        ["two"] = 2, ["to"] = 2, ["too"] = 2,
        ["three"] = 3, ["tree"] = 3,
        ["four"] = 4, ["fower"] = 4, ["for"] = 4,
        ["five"] = 5, ["fife"] = 5,
        ["six"] = 6,
        ["seven"] = 7,
        ["eight"] = 8, ["ate"] = 8,
        ["nine"] = 9, ["niner"] = 9,
    };

    private static readonly Dictionary<string, int> TensTeens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14,
        ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19,
        ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fourty"] = 40, ["fifty"] = 50,
        ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
    };

    /// <summary>Digit concatenation ("two five zero" → 250); headings and flight levels.</summary>
    public static bool TryDigits(IReadOnlyList<string> words, out int value)
    {
        ArgumentNullException.ThrowIfNull(words);
        value = 0;
        if (words.Count is 0 or > 6)
        {
            return false;
        }

        long acc = 0;
        foreach (var word in words)
        {
            if (!Units.TryGetValue(word, out var digit))
            {
                return false;
            }

            acc = acc * 10 + digit;
        }

        value = (int)acc;
        return true;
    }

    /// <summary>Magnitude form ("eleven thousand", "two hundred fifty"); altitudes/speeds/VS.</summary>
    public static bool TryMagnitude(IReadOnlyList<string> words, out int value)
    {
        ArgumentNullException.ThrowIfNull(words);
        value = 0;
        long total = 0;
        long current = 0;
        var any = false;

        foreach (var word in words)
        {
            if (Units.TryGetValue(word, out var unit))
            {
                current += unit;
                any = true;
            }
            else if (TensTeens.TryGetValue(word, out var tens))
            {
                current += tens;
                any = true;
            }
            else if (word.Equals("hundred", StringComparison.OrdinalIgnoreCase))
            {
                current = (current == 0 ? 1 : current) * 100;
                any = true;
            }
            else if (word.Equals("thousand", StringComparison.OrdinalIgnoreCase))
            {
                total += (current == 0 ? 1 : current) * 1000;
                current = 0;
                any = true;
            }
            else if (!word.Equals("and", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        value = (int)(total + current);
        return any;
    }

    /// <summary>"to"/"too"/"for" are digits only in digit-string speech ("one to tree" = 123).
    /// In magnitude speech they are almost always the English words — "descend to two thousand"
    /// must not become 5000, nor "two thousand for traffic" 2004 — so magnitude parsing trims
    /// them off the run's ends.</summary>
    private static readonly HashSet<string> AmbiguousHomophones =
        new(StringComparer.OrdinalIgnoreCase) { "to", "too", "for" };

    /// <summary>Auto-selects magnitude vs digits by vocabulary present.</summary>
    public static bool TryParse(IReadOnlyList<string> words, out int value)
    {
        ArgumentNullException.ThrowIfNull(words);
        var magnitude = words.Any(w => TensTeens.ContainsKey(w)
            || w.Equals("hundred", StringComparison.OrdinalIgnoreCase)
            || w.Equals("thousand", StringComparison.OrdinalIgnoreCase));
        return magnitude
            ? TryMagnitude(TrimAmbiguousEnds(words), out value)
            : TryDigits(words, out value);
    }

    private static IReadOnlyList<string> TrimAmbiguousEnds(IReadOnlyList<string> words)
    {
        var start = 0;
        var end = words.Count;
        while (start < end && AmbiguousHomophones.Contains(words[start]))
        {
            start++;
        }

        while (end > start && AmbiguousHomophones.Contains(words[end - 1]))
        {
            end--;
        }

        return start == 0 && end == words.Count ? words : [.. words.Skip(start).Take(end - start)];
    }

    /// <summary>Takes the first contiguous run of number words from an utterance;
    /// "and"/"point"/"decimal" bridge but never extend the run.</summary>
    public static IReadOnlyList<string> ExtractNumberRun(string utterance)
    {
        ArgumentNullException.ThrowIfNull(utterance);
        var run = new List<string>();
        var inRun = false;
        foreach (var raw in utterance.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var word = raw.Trim('.', ',', '?', '!');
            var isNumber = Units.ContainsKey(word) || TensTeens.ContainsKey(word)
                || word.Equals("hundred", StringComparison.OrdinalIgnoreCase)
                || word.Equals("thousand", StringComparison.OrdinalIgnoreCase);
            if (isNumber)
            {
                run.Add(word);
                inRun = true;
            }
            else if (inRun && word is "and" or "point" or "decimal")
            {
                continue;
            }
            else if (inRun)
            {
                break;
            }
        }

        return run;
    }
}
