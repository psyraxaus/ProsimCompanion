namespace ProsimCompanion.Speech.Recognition;

/// <summary>
/// Strips politeness from the edges of a normalized utterance before it is matched, so
/// "please run the departure briefing" and "run the departure briefing please" score as
/// "run the departure briefing". Owner report 2026-10-25: the trailing "please" cost seven
/// letters of edit distance (0.79) and the FO asked "did you mean …?" instead of acting; on a
/// short command ("gear up please", 0.5) it would have been rejected outright. Edges only —
/// a courtesy word inside a phrase is part of it — and never down to nothing, so a bare
/// "thank you" still reaches the hallucination filter and the courtesy reply unchanged.
/// Pure and deterministic.
/// </summary>
public static class CourtesyWords
{
    // Longest first so "thank you very much" wins over "thank you" and "could you please"
    // over "please". Every entry is already in CommandMatcher.Normalize form.
    private static readonly string[] Leading =
    [
        "could you please", "can you please", "would you please", "will you please",
        "would you mind", "if you would", "if you could",
        "could you", "can you", "would you", "will you",
        "please", "kindly",
    ];

    private static readonly string[] Trailing =
    [
        "thank you very much", "please and thank you", "if you would", "if you could",
        "thank you", "thanks", "cheers", "please", "for me", "ta",
    ];

    /// <summary>Removes courtesy words from both ends of <paramref name="normalized"/>
    /// (repeatedly, so "please … thanks" loses both). Returns the input unchanged when
    /// nothing else would be left.</summary>
    public static string Strip(string normalized)
    {
        ArgumentNullException.ThrowIfNull(normalized);

        var text = normalized;
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var word in Leading)
            {
                if (StartsWithWord(text, word) && text.Length > word.Length)
                {
                    text = text[word.Length..].TrimStart();
                    changed = true;
                    break;
                }
            }

            foreach (var word in Trailing)
            {
                if (EndsWithWord(text, word) && text.Length > word.Length)
                {
                    text = text[..^word.Length].TrimEnd();
                    changed = true;
                    break;
                }
            }
        }

        return text.Length == 0 ? normalized : text;
    }

    /// <summary>True when <paramref name="normalized"/> is nothing but courtesy — "thank
    /// you", "thanks very much", "cheers" — the shape the FO may answer "you're welcome" to.</summary>
    public static bool IsOnlyCourtesy(string normalized)
    {
        ArgumentNullException.ThrowIfNull(normalized);

        var text = normalized.Trim();
        if (text.Length == 0)
        {
            return false;
        }

        foreach (var word in Trailing)
        {
            if (text == word)
            {
                return true;
            }
        }

        return text is "thanks very much" or "thanks a lot" or "many thanks" or "thank you captain"
            or "thanks captain" or "thank you first officer" or "cheers mate" or "thanks mate";
    }

    private static bool StartsWithWord(string text, string word)
        => text.StartsWith(word, StringComparison.Ordinal)
            && (text.Length == word.Length || text[word.Length] == ' ');

    private static bool EndsWithWord(string text, string word)
        => text.EndsWith(word, StringComparison.Ordinal)
            && (text.Length == word.Length || text[^(word.Length + 1)] == ' ');
}
