using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Arbiter;

/// <summary>
/// While a guided dialogue holds the microphone (the minima capture, a captain's question, the
/// tech-log raise, the ECAM drill), the other features' prompts wait (owner report 2026-11-01,
/// issue #201: "Decision altitude one eight seven feet, confirm?" was followed by a radio
/// read-back and the purser's "Seat belt signs, captain?", the fifteen-second window ran out
/// under them and the FO asked for the number again). Deferred, not dropped: the arbiter
/// re-judges deferred items and lets them through when the borrow ends, their own TTLs still
/// counting.
///
/// What still goes: High and Critical (callouts, the dialogue's own prompts), untagged lines
/// (the dialogues speak untagged through <c>SpeakAsync</c>), the radio read-backs
/// (<c>sayintentions.*</c> — ATC is waiting), the FO's own answers (<c>fo.*</c>, the
/// "any questions" round), the briefing itself and the checklist (<c>briefing.*</c>,
/// <c>checklist.*</c>), the ECAM drill lines (<c>ecam.*</c>, <c>abnormal.*</c>) and the
/// dialogues' own questions (<c>clarifier</c>, <c>confirm*</c>). The accessor the arbiter
/// wires in is "borrowed AND listening": a dialogue that is still speaking its question is
/// never held by its own borrow (EGLL 2026-11-01 16:01: "did you mean gear down?" was
/// deferred, the dialogue never reached its listen, the mic was never returned, and every
/// voice command for the rest of the approach went to a dialogue that was not there).
/// </summary>
public sealed class DialogueHoldRule : ISpeechSuppressionRule
{
    private static readonly string[] ExemptPrefixes =
        ["sayintentions.", "fo.", "briefing.", "checklist.", "ecam.", "abnormal.", "minima.", "transition.", "clarifier", "confirm"];

    private readonly Func<bool> _micBorrowed;

    /// <param name="micBorrowed">Live "a dialogue owns the microphone" accessor
    /// (<see cref="IMicOwnership.IsBorrowed"/>).</param>
    public DialogueHoldRule(Func<bool> micBorrowed)
    {
        ArgumentNullException.ThrowIfNull(micBorrowed);
        _micBorrowed = micBorrowed;
    }

    public string Name => "dialogueHold";

    public SpeechSuppressionVerdict Evaluate(SpeechRequest request, SpeechContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        return Holds(request) && _micBorrowed() ? SpeechSuppressionVerdict.Defer : SpeechSuppressionVerdict.Allow;
    }

    /// <summary>Whether a request is the kind the hold applies to — pure, for the tests.</summary>
    public static bool Holds(SpeechRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Priority >= SpeechPriority.High || string.IsNullOrEmpty(request.Tag))
        {
            return false;
        }

        foreach (var prefix in ExemptPrefixes)
        {
            if (request.Tag.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
