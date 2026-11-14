using ProsimCompanion.Core.Checklists;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Checklists;

/// <summary>
/// The value read-back on a "set" / "checked" line (issue #205, EGLL–LOWW 2026-11-14): on the
/// Descent checklist's "Landing Elevation" line the pilot read the elevation instead of saying
/// "set", three runs in a row. The number matched none of the line's accepted phrases, so the
/// utterance went on to the voice features, where the FCU parser took "… feet" for an
/// instruction without a field and asked "Say again — which field?" while the line waited.
/// <para>The rule is the engine's, not the shipped JSON's — a pilot's edited checklist files are
/// kept across updates and would never receive a new accepted phrase. A line that itself
/// declares "set" or "checked" as an accepted answer is a line about a value or a state the
/// pilot has just looked at; reading that value back says at least as much as the word, and
/// the line's verify condition (when it has one) still cross-checks the aircraft. Lines that
/// expect a number or a flap configuration keep their own parsing.</para>
/// </summary>
public static class ChecklistValueAnswer
{
    /// <summary>The declared answers that make a line a value line.</summary>
    private static readonly string[] ValueLineAnswers = ["set", "checked"];

    private static readonly HashSet<string> NumberWords = new(StringComparer.Ordinal)
    {
        "zero", "oh", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "niner",
        "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen",
        "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety",
        "hundred", "thousand",
    };

    /// <summary>Words that may stand around the number without making it something else.</summary>
    private static readonly HashSet<string> Fillers = new(StringComparer.Ordinal)
    {
        "and", "decimal", "point", "minus",
        "feet", "foot", "ft", "meters", "metres", "degrees", "knots", "hectopascals", "millibars",
        "inches", "percent", "kilos", "kilograms", "kg", "tonnes", "tons", "pounds",
    };

    /// <summary>True when the line takes a value read-back as its answer: nothing special
    /// expected (<c>expects</c> none) and "set" or "checked" among its OWN accepted phrases —
    /// the universal "checked" every line accepts does not count.</summary>
    public static bool LineTakesAValue(ChecklistItemDefinition item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.Expects.Equals("none", StringComparison.OrdinalIgnoreCase)
            && item.AcceptedPhrases.Any(phrase => ValueLineAnswers.Contains(CommandMatcher.Normalize(phrase), StringComparer.Ordinal));
    }

    /// <summary>True when the text is a number and nothing else: digits or number words, at
    /// most with a unit ("six hundred feet", "600 ft.", "eight"), and optionally the line's
    /// own challenge said back before it ("landing elevation six hundred feet"). Any other
    /// word — "descend flight level one seven zero", "set heading two two zero" — makes it
    /// something said to somebody else, and it is not the line's answer by this rule.</summary>
    public static bool IsValueReadBack(ChecklistItemDefinition item, string text)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(text);

        var challenge = CommandMatcher.Normalize(item.Say).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hasNumber = false;
        foreach (var token in CommandMatcher.Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (NumberWords.Contains(token) || token.All(char.IsAsciiDigit))
            {
                hasNumber = true;
            }
            else if (!Fillers.Contains(token) && !challenge.Contains(token, StringComparer.Ordinal))
            {
                return false;
            }
        }

        return hasNumber;
    }

    /// <summary>The rule in one call: a value read-back on a line that takes one.</summary>
    public static bool Satisfies(ChecklistItemDefinition item, string text)
        => LineTakesAValue(item) && IsValueReadBack(item, text);
}
