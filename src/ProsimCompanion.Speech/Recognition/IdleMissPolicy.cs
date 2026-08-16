using ProsimCompanion.Core.State;

namespace ProsimCompanion.Speech.Recognition;

/// <summary>What the FO says to an utterance nothing routed (arbiter tag included).</summary>
/// <param name="Text">The spoken line.</param>
/// <param name="Tag">Arbiter tag — "advisory" or "reject"; every spoken line must carry one
/// (issue #66: the untagged clarifier lines are what hid the misroute for a whole flight).</param>
/// <param name="IsLlmOfflineAdvisory">True when this response consumed the one-per-session
/// LLM-offline advisory — the caller latches it.</param>
public sealed record IdleMissResponse(string Text, string Tag, bool IsLlmOfflineAdvisory);

/// <summary>
/// The idle-miss decision (issue #66), pure so it is testable without the checklist engine:
/// an unmatched free-form utterance OUTSIDE any dialogue gets the normal did-not-catch line —
/// except the very first one while the LLM is known-unhealthy, which instead tells the pilot
/// WHY free-form phrasing is falling flat ("my free-form understanding is offline"), once per
/// session rather than on every miss.
/// </summary>
public static class IdleMissPolicy
{
    /// <summary>Spoken once per session on the first free-form miss while the LLM is down.</summary>
    public const string LlmOfflineAdvisory =
        "Captain, my free-form understanding is offline — exact phrases only.";

    public static IdleMissResponse Decide(
        LlmHealthState llmState, bool advisoryAlreadyGiven, string didNotCatchPhrase)
    {
        ArgumentNullException.ThrowIfNull(didNotCatchPhrase);

        if (!advisoryAlreadyGiven
            && llmState is LlmHealthState.AuthFailed or LlmHealthState.Unreachable)
        {
            return new IdleMissResponse(LlmOfflineAdvisory, "advisory", IsLlmOfflineAdvisory: true);
        }

        return new IdleMissResponse(didNotCatchPhrase, "reject", IsLlmOfflineAdvisory: false);
    }
}
