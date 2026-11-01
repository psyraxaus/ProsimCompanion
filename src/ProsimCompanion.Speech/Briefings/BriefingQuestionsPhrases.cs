using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Gsx;
using ProsimCompanion.Speech.Questions;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Briefings;

/// <summary>
/// The FO's "Any questions, captain?" after every briefing (owner request 2026-11-01, issue
/// #201), voiced by the shared <see cref="CaptainsQuestionDialogue"/> from this table. The
/// answer is free text (an EMPTY grammar hands the raw transcription to the table): "no
/// questions" / "no" / "nothing" closes the round with "Briefing complete."; a bare "yes" gets
/// "Go ahead, captain."; anything else is the captain's question — handed to the FO question
/// service (the LLM over the fact sheet) and, once that answer has been spoken, followed by
/// "Anything else, captain?" up to <see cref="BriefingQuestionsStore.MaxQuestions"/> times. A
/// timeout closes the round the same way: the briefing is complete whatever happens.
/// </summary>
public static class BriefingQuestionsPhrases
{
    /// <summary>The "no questions" vocabulary, matched as a whole utterance or a lead-in.</summary>
    public static readonly IReadOnlyList<string> NonePhrases =
    [
        "no", "nope", "negative", "none", "nothing", "no questions", "no question", "no further questions",
        "no further", "nothing further", "nothing else", "no thanks", "no thank you", "that's all", "that is all",
        "thats all", "that's it", "that is it", "we're good", "we are good", "all good", "all clear", "clear",
        "copied", "copy", "roger", "understood", "thank you", "thanks",
    ];

    public const string GoAhead = "Go ahead, captain.";

    /// <summary>Builds the table over the store, the arbiter and the FO question service.
    /// <paramref name="questions"/> null (tests, or the feature off) answers every question
    /// with the offline line and still asks again.</summary>
    public static CaptainsQuestionPhrases Create(BriefingQuestionsStore store, ISpeechArbiter arbiter, FoQuestionService? questions)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(arbiter);
        return new(
            Topic: BriefingQuestionsStore.QuestionDefinition.Topic,
            LogName: "Briefing questions",
            MicOwner: "briefingQuestions",
            EventName: "briefing.questions",
            Grammar: _ => [],
            SpokenPrompt: question => question.Prompt,
            Interpret: (heard, question) => Interpret(heard, captainAsked =>
            {
                // The answer is the question service's (streamed, verified); the follow-up
                // prompt waits for it so the FO never asks over their own answer.
                var taken = questions?.TryAsk(captainAsked) ?? false;
                var answer = taken ? questions?.InFlight : null;
                _ = FollowUpAsync(store, arbiter, answer, taken);
            }),
            UnclearPrompt: _ => GoAhead,
            UnansweredPhrase: (_, _) => BriefingQuestionsStore.CompleteLine,
            RaisedPayload: question => new { prompt = question.Prompt, attempt = question.Attempt, departure = store.Departure },
            AnsweredPayload: (_, reply, heard) => reply.Kind == QuestionReplyKind.No
                ? new { outcome = "complete", heard, questionsAsked = store.QuestionsAsked }
                : new { outcome = "question", captainAsked = heard, questionsAsked = store.QuestionsAsked + 1 },
            UnansweredPayload: (_, asking) => new { outcome = "complete", reason = "unanswered", open = asking.QuestionStands },
            NoReadback: BriefingQuestionsStore.CompleteLine);
    }

    /// <summary>Classifies one utterance: <see cref="QuestionReply.No"/> for the "no questions"
    /// vocabulary, null for a bare affirmation or an empty line (the FO says "go ahead"),
    /// otherwise a settled reply whose action hands the captain's words to
    /// <paramref name="ask"/>. Pure apart from that callback, for tests.</summary>
    public static QuestionReply? Interpret(string heard, Action<string> ask)
    {
        ArgumentNullException.ThrowIfNull(heard);
        ArgumentNullException.ThrowIfNull(ask);
        var text = CommandMatcher.Normalize(heard);
        if (text.Length == 0)
        {
            return null;
        }

        if (NonePhrases.Select(CommandMatcher.Normalize).Any(p => text == p || text.StartsWith(p + " ", StringComparison.Ordinal)))
        {
            return QuestionReply.No;
        }

        if (ConfirmVocabulary.Affirm.Any(a => text == a) || text is "yes please" or "i do" or "i have one" or "one question")
        {
            return null;
        }

        var captainAsked = heard.Trim();
        return QuestionReply.Settled(() => ask(captainAsked));
    }

    /// <summary>After the FO's answer: counts the question and raises "Anything else,
    /// captain?" (the dialogue voices it), or closes with "Briefing complete." when the round
    /// is at its limit. Never throws into the dialogue.</summary>
    internal static async Task FollowUpAsync(BriefingQuestionsStore store, ISpeechArbiter arbiter, Task? answer, bool taken)
    {
        try
        {
            if (answer is not null)
            {
                await answer.ConfigureAwait(false);
            }
            else if (!taken)
            {
                await arbiter.SpeakAsync(Questions.FoQuestionCore.LlmOffline, SpeechPriority.Normal).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // The question service logged its own failure; the round still closes properly.
        }

        if (!store.Asked("voice"))
        {
            await arbiter.SpeakAsync(BriefingQuestionsStore.CompleteLine, SpeechPriority.Normal).ConfigureAwait(false);
        }
    }
}
