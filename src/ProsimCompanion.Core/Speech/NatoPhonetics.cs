namespace ProsimCompanion.Core.Speech;

/// <summary>
/// The single letter↔NATO-word table (issue #68). Kokoro reads a bare letter as an English
/// word fragment ("M" in an ATIS line came out "em"; the trailing "V" of VOLA3V vanished), so
/// anything spoken must render letters as their phonetic words. Three private copies of this
/// table existed before (approach variants, tech-log categories, ATIS parsing) — they now all
/// come from here so the spellings can never drift apart.
/// </summary>
public static class NatoPhonetics
{
    /// <summary>ICAO digit words — note 9 is "niner". Canonical copy; the Speech project's
    /// <c>Aviation.DigitWords</c> aliases this array so grammars and identifier rendering
    /// share one table (Core cannot reference Speech, so the table lives here).</summary>
    public static readonly string[] DigitWords =
        ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "niner"];

    // "Juliet"/"X-ray" (not ICAO "Juliett"/"Xray") — these are the spellings the TTS engines
    // pronounce correctly, carried from the pre-refactor ApproachOption table.
    private static readonly string[] LetterWords =
    [
        "Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot", "Golf", "Hotel", "India",
        "Juliet", "Kilo", "Lima", "Mike", "November", "Oscar", "Papa", "Quebec", "Romeo",
        "Sierra", "Tango", "Uniform", "Victor", "Whiskey", "X-ray", "Yankee", "Zulu",
    ];

    // Word → letter accepts the spelling variants seen in live data: ICAO "alfa"/"juliett",
    // and "xray"/"x-ray" both ways.
    private static readonly Dictionary<string, char> WordToLetter = BuildWordToLetter();

    private static Dictionary<string, char> BuildWordToLetter()
    {
        var map = new Dictionary<string, char>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < LetterWords.Length; i++)
        {
            map[LetterWords[i]] = (char)('A' + i);
        }

        map["alfa"] = 'A';
        map["juliett"] = 'J';
        map["xray"] = 'X';
        return map;
    }

    /// <summary>NATO word for a letter ("V" → "Victor"), or null for anything that is not an
    /// ASCII letter — callers decide their own fallback.</summary>
    public static string? Word(char letter)
    {
        var upper = char.ToUpperInvariant(letter);
        return upper is >= 'A' and <= 'Z' ? LetterWords[upper - 'A'] : null;
    }

    /// <summary>Spoken form of a single-letter string ("M" → "Mike"). Null stays null (so
    /// omit-blank fact lines keep omitting); anything longer than one letter passes through
    /// unchanged rather than being mangled.</summary>
    public static string? Letter(string? letter)
    {
        if (string.IsNullOrWhiteSpace(letter))
        {
            return null;
        }

        var trimmed = letter.Trim();
        return trimmed.Length == 1 ? Word(trimmed[0]) ?? trimmed : trimmed;
    }

    /// <summary>Phonetic word → letter ("mike"/"Juliett"/"xray" → 'M'/'J'/'X'), tolerant of
    /// the common spelling variants. False when the token is not a NATO word.</summary>
    public static bool TryParseWord(string? word, out char letter)
    {
        letter = default;
        return !string.IsNullOrWhiteSpace(word) && WordToLetter.TryGetValue(word.Trim(), out letter);
    }

    /// <summary>
    /// TTS rendering for a procedure/airway-style identifier: "VOLA3V" → "VOLA three Victor",
    /// "KODAP1A" → "KODAP one Alpha", "ILS09L" → "ILS zero niner Lima". A token-leading run of
    /// three or more letters is kept as-is (pronounceable base or known abbreviation — the
    /// downstream <c>AviationSpeech</c> pass still expands "ILS" etc.); every other letter
    /// becomes its NATO word, because kokoro swallows or mispronounces bare designator letters
    /// (issue #68). Digits use the ICAO digit words. Whitespace splits tokens ("ILS 16R");
    /// other punctuation is dropped, matching <c>Aviation.ToDigits</c>.
    /// </summary>
    public static string SpeakIdentifier(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return "";
        }

        var parts = new List<string>();
        foreach (var token in identifier.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var i = 0;
            var leadingLetters = 0;
            while (leadingLetters < token.Length && char.IsAsciiLetter(token[leadingLetters]))
            {
                leadingLetters++;
            }

            if (leadingLetters >= 3)
            {
                parts.Add(token[..leadingLetters]);
                i = leadingLetters;
            }

            for (; i < token.Length; i++)
            {
                var c = token[i];
                if (c is >= '0' and <= '9')
                {
                    parts.Add(DigitWords[c - '0']);
                }
                else if (Word(c) is { } word)
                {
                    parts.Add(word);
                }

                // Punctuation is silently dropped.
            }
        }

        return string.Join(" ", parts);
    }
}
