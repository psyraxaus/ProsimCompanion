using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Gsx;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Briefings;

/// <summary>
/// The FO's "any other threats, captain?" after the emergency half of the takeoff briefing
/// (issue #180), voiced by the shared <see cref="CaptainsQuestionDialogue"/> from this table.
/// The answer is free text: an EMPTY grammar hands the raw LAN transcription to the table (the
/// closed-grammar offline engine cannot listen without phrases and times out, which the
/// dialogue reads as "no further threats"). "No" / "nothing else" declines; anything else is
/// the captain's threat — noted on the <see cref="ThreatsQuestionStore"/>, read back as
/// "Noted, …" and recorded on the <c>briefing.threats</c> session event. A bare "yes" is not a
/// threat: the FO asks what it is.
/// </summary>
public static class ThreatsQuestionPhrases
{
    /// <summary>The "nothing else" vocabulary, matched as a whole utterance or a lead-in.</summary>
    public static readonly IReadOnlyList<string> NonePhrases =
    [
        "no", "nope", "negative", "none", "nothing", "nothing else", "nothing further", "no further threats",
        "no further", "no other threats", "no others", "no threats", "nil", "that's all", "that is all",
        "thats all", "that's it", "that is it", "we're good", "we are good", "all good",
    ];

    /// <summary>Builds the table over the store and the arbiter (the read-back of a noted
    /// threat is spoken here because the dialogue's Settled path has no read-back of its own).</summary>
    public static CaptainsQuestionPhrases Create(ThreatsQuestionStore store, ISpeechArbiter arbiter)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(arbiter);
        return new(
            Topic: ThreatsQuestionStore.QuestionDefinition.Topic,
            LogName: "Threats question",
            MicOwner: "briefingThreats",
            EventName: "briefing.threats",
            Grammar: _ => [],
            SpokenPrompt: question => question.Prompt,
            Interpret: (heard, question) => Interpret(heard, threat =>
            {
                store.Note(threat, "voice");
                arbiter.SpeakAsync($"Noted, {threat}.", SpeechPriority.High).ContinueWith(
                    static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            }),
            UnclearPrompt: _ => "Say again, captain — any other threats, or nothing further?",
            UnansweredPhrase: (_, _) => "No further threats noted.",
            RaisedPayload: question => new { prompt = question.Prompt, attempt = question.Attempt },
            AnsweredPayload: (_, reply, heard) => reply.Kind == QuestionReplyKind.No
                ? new { outcome = "none", heard }
                : new { outcome = "noted", captainSaid = heard },
            UnansweredPayload: (_, asking) => new { outcome = "unanswered", open = asking.QuestionStands },
            NoReadback: "No further threats, copied.");
    }

    /// <summary>Classifies one utterance: <see cref="QuestionReply.No"/> for the "nothing else"
    /// vocabulary, null for a bare affirmation or an empty line (the FO asks what the threat
    /// is), otherwise a settled reply whose action hands the captain's words to
    /// <paramref name="note"/>. Pure apart from that callback, for tests.</summary>
    public static QuestionReply? Interpret(string heard, Action<string> note)
    {
        ArgumentNullException.ThrowIfNull(heard);
        ArgumentNullException.ThrowIfNull(note);
        var text = CommandMatcher.Normalize(heard);
        if (text.Length == 0)
        {
            return null;
        }

        // The table is normalised the same way as the utterance ("that's all" → "that s all").
        if (NonePhrases.Select(CommandMatcher.Normalize).Any(p => text == p || text.StartsWith(p + " ", StringComparison.Ordinal)))
        {
            return QuestionReply.No;
        }

        if (ConfirmVocabulary.Affirm.Any(a => text == a))
        {
            return null;
        }

        var threat = heard.Trim().TrimEnd('.', '!', ',');
        return QuestionReply.Settled(() => note(threat));
    }
}
