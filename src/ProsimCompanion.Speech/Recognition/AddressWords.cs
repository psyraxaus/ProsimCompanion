namespace ProsimCompanion.Speech.Recognition;

/// <summary>
/// Strips the person a captain addresses from the front of a normalized utterance — "ground,
/// start boarding", "cabin crew, commence boarding", "let's start the fuel" — so the request
/// matches the phrase table as "start boarding" (owner request 2026-11-07: natural wordings
/// earned a did-you-mean round trip). Only the front, only when words remain, and the caller
/// keeps the unstripped form as a candidate too: "ground from cockpit" is a hail, not an
/// address plus "from cockpit". Pure and deterministic.
/// </summary>
public static class AddressWords
{
    // Longest first. Every entry is already in CommandMatcher.Normalize form (no punctuation).
    private static readonly string[] Leading =
    [
        "ground crew", "cabin crew", "flight deck to ground", "cockpit to ground", "cockpit to crew",
        "ground", "cabin", "purser", "crew",
        // Normalize turns an apostrophe into a space: "let's" arrives as "let s", "I'd" as "i d".
        "let s", "lets", "let us", "can we", "could we", "shall we", "we need to", "we need", "i need to", "i need",
        "i would like to", "i would like", "i d like to", "i d like", "id like to", "id like",
        "go ahead and", "please go ahead and",
    ];

    /// <summary>The utterance without its leading address (repeatedly: "ground, please, let's
    /// start boarding" → "start boarding"); unchanged when nothing would be left.</summary>
    public static string Strip(string normalized)
    {
        ArgumentNullException.ThrowIfNull(normalized);

        var text = normalized;
        var changed = true;
        while (changed)
        {
            changed = false;
            text = CourtesyWords.Strip(text);
            foreach (var word in Leading)
            {
                if (text.StartsWith(word, StringComparison.Ordinal)
                    && text.Length > word.Length
                    && text[word.Length] == ' ')
                {
                    text = text[(word.Length + 1)..].TrimStart();
                    changed = true;
                    break;
                }
            }
        }

        return text.Length == 0 ? normalized : text;
    }
}
