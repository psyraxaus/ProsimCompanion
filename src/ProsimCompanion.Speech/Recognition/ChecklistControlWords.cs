namespace ProsimCompanion.Speech.Recognition;

/// <summary>
/// The checklist control words when they arrive glued to other speech (owner flight
/// 2026-10-25: the cabin PA was playing while the Descent checklist waited on "Minimum", the
/// transcript read "… 80 degrees Celsius, skip," and the pilot's "skip" at its end was lost
/// because a control word had to be the whole utterance). A word at the START or END of a
/// longer transcript now counts while a checklist line is waiting. Deliberately short list:
/// "continue", "repeat" and "standby" are everyday PA words and stay whole-utterance only.
/// Pure.
/// </summary>
public static class ChecklistControlWords
{
    /// <summary>(as the ASR may write it, the canonical router word).</summary>
    private static readonly (string Heard, string Word)[] Words =
    [
        ("skip item", "skip"),
        ("skip", "skip"),
        ("say again", "say again"),
        ("hold the checklist", "hold the checklist"),
        ("cancel the checklist", "cancel checklist"),
        ("cancel checklist", "cancel checklist"),
        ("resume checklist", "resume checklist"),
        ("restart checklist", "restart checklist"),
    ];

    /// <summary>The control word at the edge of <paramref name="normalized"/> when the
    /// transcript is LONGER than the word itself (an exact match takes the ordinary path);
    /// null otherwise.</summary>
    public static string? AtEdge(string normalized)
    {
        ArgumentNullException.ThrowIfNull(normalized);

        var text = normalized.Trim();
        foreach (var (heard, word) in Words)
        {
            if (text.Length <= heard.Length)
            {
                continue;
            }

            if (text.StartsWith(heard + " ", StringComparison.Ordinal)
                || text.EndsWith(" " + heard, StringComparison.Ordinal))
            {
                return word;
            }
        }

        return null;
    }
}
