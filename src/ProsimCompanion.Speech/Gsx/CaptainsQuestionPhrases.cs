using ProsimCompanion.Core.State;

namespace ProsimCompanion.Speech.Gsx;

/// <summary>What one heard utterance meant for an open captain's question.</summary>
public enum QuestionReplyKind
{
    /// <summary>Yes — the store accepts, the FO reads the yes line back.</summary>
    Yes,

    /// <summary>No — the store declines with "captain said '…'", the FO reads the no line back.</summary>
    No,

    /// <summary>One of the question's choices — the store picks it, the FO reads it back.</summary>
    Pick,

    /// <summary>"Your call" — the captain hands the decision back; the question stays open
    /// (the web card, a command, GSX's own menu or the TTL settle it).</summary>
    HandBack,

    /// <summary>An odd shape the table settles itself ("push back facing north" is not one of
    /// GSX's menu lines): <see cref="QuestionReply.Apply"/> stores and reads back.</summary>
    Settled,
}

/// <summary>A phrase table's reading of one utterance; null from the table = not an answer.</summary>
public sealed record QuestionReply(QuestionReplyKind Kind, QuestionChoice? Choice = null, Action? Apply = null)
{
    public static QuestionReply Yes { get; } = new(QuestionReplyKind.Yes);

    public static QuestionReply No { get; } = new(QuestionReplyKind.No);

    public static QuestionReply HandBack { get; } = new(QuestionReplyKind.HandBack);

    public static QuestionReply Pick(QuestionChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        return new(QuestionReplyKind.Pick, choice);
    }

    public static QuestionReply Settled(Action apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        return new(QuestionReplyKind.Settled, Apply: apply);
    }
}

/// <summary>
/// One captain's question's voice: everything the shared <see cref="CaptainsQuestionDialogue"/>
/// must know to ask it, hear the answer and talk about it — and nothing about how to listen
/// (that is the dialogue's). Four tables replace four dialogues that shared one skeleton
/// (architecture review C2). The session event name and the payload shapes are part of the
/// table because the probes in <c>docs/agents/verification-probes.json</c> read them: they must
/// stay byte-identical per question.
/// </summary>
/// <param name="Topic">The Core store's topic this table voices.</param>
/// <param name="LogName">The question as the CMTrace log names it ("De-ice question").</param>
/// <param name="MicOwner">The owner name the microphone is borrowed under.</param>
/// <param name="EventName">The session event type ("deice-question").</param>
/// <param name="Grammar">The closed listening grammar for one question (the gate's carries its labels).</param>
/// <param name="SpokenPrompt">What the FO says to ask it (the gate spells "36 Alpha"; the
/// de-icing re-ask says it is the last item before pushback).</param>
/// <param name="Interpret">The interpretation hook: the utterance and the question → a reply,
/// or null for "not an answer" (the FO asks again).</param>
/// <param name="UnclearPrompt">What the FO says after a non-answer.</param>
/// <param name="UnansweredPhrase">What the FO says when nobody answered — the question stands.</param>
/// <param name="RaisedPayload">The session event payload when the question is voiced.</param>
/// <param name="AnsweredPayload">The payload when it is answered (null = no event; the
/// pushback feature records its own <c>pushback-choice</c>).</param>
/// <param name="UnansweredPayload">The payload when nobody answered.</param>
/// <param name="YesReadback">The FO's yes line (yes/no questions).</param>
/// <param name="NoReadback">The FO's no line (yes/no questions).</param>
/// <param name="PickReadback">The FO's read-back of a pick ("36 Bravo it is.").</param>
/// <param name="RepeatPrompt">What "say again" repeats; null = the spoken prompt.</param>
public sealed record CaptainsQuestionPhrases(
    string Topic,
    string LogName,
    string MicOwner,
    string EventName,
    Func<CaptainsQuestion, IReadOnlyList<string>> Grammar,
    Func<CaptainsQuestion, string> SpokenPrompt,
    Func<string, CaptainsQuestion, QuestionReply?> Interpret,
    Func<CaptainsQuestion, string> UnclearPrompt,
    Func<CaptainsQuestion, CaptainsQuestionState, string> UnansweredPhrase,
    Func<CaptainsQuestion, object> RaisedPayload,
    Func<CaptainsQuestion, QuestionReply, string, object?> AnsweredPayload,
    Func<CaptainsQuestion, CaptainsQuestionState, object> UnansweredPayload,
    string? YesReadback = null,
    string? NoReadback = null,
    Func<QuestionChoice, string>? PickReadback = null,
    Func<CaptainsQuestion, string>? RepeatPrompt = null);
