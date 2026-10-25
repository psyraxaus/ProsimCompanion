namespace ProsimCompanion.Speech.Recognition;

/// <summary>What the router does with a bare courtesy ("thank you", "thanks", "cheers").</summary>
public enum CourtesyDecision
{
    /// <summary>The words are not a bare courtesy — route them as usual.</summary>
    NotCourtesy,

    /// <summary>A courtesy, but no reply: absorbed in silence (never a "say again").</summary>
    Absorb,

    /// <summary>Answer it ("You're welcome, Captain.").</summary>
    Reply,
}

/// <summary>
/// The gate in front of the FO's "you're welcome" (owner request 2026-10-25). "Thank you." is
/// Whisper's classic silence hallucination (issue #120: eight of them in one cruise), so a
/// courtesy is answered ONLY when all of these hold: the switch is on, the FO spoke or acted
/// within <see cref="ReplyWindow"/>, the engine's acoustic confidence (when it gives one) is at
/// least <see cref="MinAcousticConfidence"/>, and the moment is not sterile. Everything else
/// is absorbed without a word. Pure and deterministic — the router supplies the clock.
/// </summary>
public static class CourtesyReplyCore
{
    /// <summary>How long after the FO's last word or action a "thank you" is still about it.</summary>
    public static TimeSpan ReplyWindow { get; } = TimeSpan.FromSeconds(20);

    /// <summary>Acoustic confidence floor for a reply — a breath transcribed as "Thank you."
    /// scores low; a real one does not.</summary>
    public const double MinAcousticConfidence = 0.7;

    private static readonly string[] Replies =
    [
        "You're welcome, Captain.",
        "My pleasure, Captain.",
        "Any time, Captain.",
    ];

    /// <summary>Decides for one utterance. <paramref name="normalized"/> is the heard text in
    /// <see cref="CommandMatcher.Normalize"/> form.</summary>
    public static (CourtesyDecision Decision, string Reason) Decide(
        string normalized,
        bool enabled,
        double? acousticConfidence,
        DateTimeOffset nowUtc,
        DateTimeOffset? lastFoActivityUtc,
        bool sterile)
    {
        ArgumentNullException.ThrowIfNull(normalized);

        if (!CourtesyWords.IsOnlyCourtesy(normalized))
        {
            return (CourtesyDecision.NotCourtesy, "not-courtesy");
        }

        if (!enabled)
        {
            return (CourtesyDecision.Absorb, "switched-off");
        }

        if (sterile)
        {
            return (CourtesyDecision.Absorb, "sterile");
        }

        if (lastFoActivityUtc is null || nowUtc - lastFoActivityUtc.Value > ReplyWindow || nowUtc < lastFoActivityUtc.Value)
        {
            return (CourtesyDecision.Absorb, "nothing-recent");
        }

        if (acousticConfidence is { } confidence && confidence < MinAcousticConfidence)
        {
            return (CourtesyDecision.Absorb, "low-confidence");
        }

        return (CourtesyDecision.Reply, "replied");
    }

    /// <summary>The n-th reply, cycling — three wordings so the FO does not sound like a doorbell.</summary>
    public static string ReplyText(int index) => Replies[Math.Abs(index) % Replies.Length];
}
