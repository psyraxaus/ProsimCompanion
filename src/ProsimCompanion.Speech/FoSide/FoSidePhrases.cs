using System.Globalization;
using System.Text.RegularExpressions;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.FoSide;

/// <summary>
/// The pilot's words for the FO's own-side duties (issue #188), pure. A QNH phrase is this
/// feature's only when it CARRIES A VALUE — the bare "set qnh" / "set standard" belong to the
/// file commands in commands.json and must never be taken here. EFIS phrases need "your",
/// "your side" or "nd" so the captain's own "range forty" is never taken.
/// </summary>
public static partial class FoSidePhrases
{
    /// <summary>ND range index → nm (0 = 10 … 5 = 320).</summary>
    public static readonly IReadOnlyList<int> RangesNm = [10, 20, 40, 80, 160, 320];

    /// <summary>ND mode index → name (0 ILS, 1 VOR, 2 NAV, 3 ARC, 4 PLAN).</summary>
    public static readonly IReadOnlyList<string> ModeNames = ["ILS", "VOR", "NAV", "ARC", "PLAN"];

    private static readonly string[] YourSideMarkers = ["your side", "your", "nd", "f o side", "fo side", "first officer"];

    private static readonly (string Words, int Index)[] ModeWords =
    [
        ("rose ils", 0), ("ils", 0), ("rose vor", 1), ("vor", 1), ("rose nav", 2), ("nav", 2), ("arc", 3), ("plan", 4),
    ];

    private static readonly (string Words, int Index)[] RangeWords =
    [
        ("three twenty", 5), ("three two zero", 5), ("320", 5), ("one sixty", 4), ("one six zero", 4), ("160", 4),
        ("eighty", 3), ("80", 3), ("forty", 2), ("40", 2), ("twenty", 1), ("20", 1), ("ten", 0), ("10", 0),
    ];

    /// <summary>"qnh one zero one three", "set qnh 1013 on your side" → 1013. False for a bare
    /// "set qnh" (no value) or a value outside 900–1100.</summary>
    public static bool TryParseQnh(string utterance, out int hpa)
    {
        ArgumentNullException.ThrowIfNull(utterance);
        hpa = 0;
        var text = Normalize(utterance);
        if (!text.Contains("qnh", StringComparison.Ordinal) && !text.Contains("q n h", StringComparison.Ordinal))
        {
            return false;
        }

        var digits = Llm.SpokenNumberText.ToDigits(text, includeSingles: true);
        var match = QnhNumber().Match(digits);
        if (!match.Success)
        {
            return false;
        }

        // "one zero one three" comes out as "1 0 1 3" or "1013" depending on the speller: join the digit groups.
        var joined = string.Concat(match.Value.Where(char.IsAsciiDigit));
        if (!int.TryParse(joined, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value is < 900 or > 1100)
        {
            return false;
        }

        hpa = value;
        return true;
    }

    /// <summary>"your side standard", "standard on your side", "your standard" — never the bare "set standard".</summary>
    public static bool IsYourSideStandard(string utterance)
    {
        ArgumentNullException.ThrowIfNull(utterance);
        var text = Normalize(utterance);
        return text.Contains("standard", StringComparison.Ordinal) && text.Contains("your", StringComparison.Ordinal);
    }

    /// <summary>"your range forty", "range eighty on your side" → the zoom index; false without a
    /// your-side marker or with a range that is not on the knob.</summary>
    public static bool TryParseRange(string utterance, out int index, out bool unknownRange)
    {
        ArgumentNullException.ThrowIfNull(utterance);
        index = -1;
        unknownRange = false;
        var text = Normalize(utterance);
        if (!text.Contains("range", StringComparison.Ordinal) || !HasYourSideMarker(text))
        {
            return false;
        }

        foreach (var (words, i) in RangeWords)
        {
            if (ContainsPhrase(text, words))
            {
                index = i;
                return true;
            }
        }

        unknownRange = true;
        return false;
    }

    /// <summary>"nd to arc", "your nd rose nav", "plan on your side" → the mode index.</summary>
    public static bool TryParseMode(string utterance, out int index)
    {
        ArgumentNullException.ThrowIfNull(utterance);
        index = -1;
        var text = Normalize(utterance);
        if (!HasYourSideMarker(text) || text.Contains("range", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var (words, i) in ModeWords)
        {
            if (ContainsPhrase(text, words))
            {
                index = i;
                return true;
            }
        }

        return false;
    }

    /// <summary>"cross check", "fmgc cross-check", "adirs check".</summary>
    public static bool IsCrossCheck(string utterance)
    {
        ArgumentNullException.ThrowIfNull(utterance);
        var text = Normalize(utterance);
        return ContainsPhrase(text, "cross check") || ContainsPhrase(text, "crosscheck")
            || ContainsPhrase(text, "adirs check") || ContainsPhrase(text, "fmgc check");
    }

    private static bool HasYourSideMarker(string text) => YourSideMarkers.Any(m => ContainsPhrase(text, m));

    private static bool ContainsPhrase(string text, string phrase) => (" " + text + " ").Contains(" " + phrase + " ", StringComparison.Ordinal);

    private static string Normalize(string utterance) => CommandMatcher.Normalize(utterance).Replace("-", " ", StringComparison.Ordinal);

    [GeneratedRegex(@"\b(?:\d\s?){3,4}\b")]
    private static partial Regex QnhNumber();
}
