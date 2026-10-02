using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Speech;
using ProsimCompanion.Speech.Llm;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Questions;

/// <summary>
/// The pure half of the FO questions (issue #149): what counts as a question, the prompts,
/// and the fixed lines. The service around it owns the clock, the model and the arbiter.
/// </summary>
public static class FoQuestionCore
{
    /// <summary>Spoken when the facts do not cover the question (the model is told to say exactly this).</summary>
    public const string DontHaveThat = "I don't have that.";

    /// <summary>Spoken when the model's answer could not be verified, or did not arrive in time.</summary>
    public const string NoVerifiedAnswer = "I don't have a verified answer for that.";

    /// <summary>Spoken when the time budget passes its first marker with nothing said yet.</summary>
    public const string StandBy = "Stand by.";

    /// <summary>Spoken when the LLM is known-unhealthy (the same advisory the idle miss gives).</summary>
    public const string LlmOffline = IdleMissPolicy.LlmOfflineAdvisory;

    /// <summary>True when the utterance starts with one of the lead-ins (after normalization)
    /// and has at least <see cref="FoQuestionOptions.MinimumWords"/> words. "What's the fuel"
    /// is three words — not a question by the default rule, by design: short utterances are
    /// the exact phrases other features own, misheard.</summary>
    public static bool IsQuestion(string utterance, FoQuestionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(utterance))
        {
            return false;
        }

        var normalized = CommandMatcher.Normalize(utterance);
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < Math.Max(1, options.MinimumWords))
        {
            return false;
        }

        foreach (var leadIn in options.LeadInList())
        {
            var lead = CommandMatcher.Normalize(leadIn ?? "");
            if (lead.Length == 0)
            {
                continue;
            }

            if (normalized.Equals(lead, StringComparison.Ordinal)
                || normalized.StartsWith(lead + " ", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The question with a leading "question" lead-in word removed ("question, what is
    /// our fuel" → "what is our fuel") — the model need not see the wake word.</summary>
    public static string StripWakeWord(string utterance)
    {
        var text = (utterance ?? "").Trim();
        foreach (var wake in new[] { "question,", "question:", "question" })
        {
            if (text.StartsWith(wake, StringComparison.OrdinalIgnoreCase) && text.Length > wake.Length && text[wake.Length] == ' ')
            {
                return text[(wake.Length + 1)..].TrimStart();
            }
        }

        return text;
    }

    public static string SystemPrompt(string personaFragment)
        => (personaFragment ?? "")
            + "You are the First Officer of an Airbus A320 answering the Captain's question out loud. "
            + "Answer from the FACTS list below and from nothing else — never invent, estimate or "
            + "recall figures that are not in the list. If the facts do not answer the question, say "
            + $"exactly: \"{DontHaveThat}\" and nothing more. One or two short spoken sentences, plain "
            + "English for text-to-speech: no markdown, no lists, no headings, no preamble. Read "
            + "flight levels, headings, frequencies, runway numbers and QNH digit by digit; read "
            + "weights, distances, times and speeds normally ('six point two tonnes', 'one hundred "
            + "and forty knots'). Spell single letters as NATO words ('information Bravo'). Never "
            + "tell the Captain to do anything and never give an instruction to any system.";

    public static string UserPrompt(FoFactSheet sheet, string question)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        return "FACTS:\n" + (sheet.IsEmpty ? "(none available)" : sheet.Text)
            + "\n\nCAPTAIN ASKS: " + question + "\n\nAnswer now.";
    }

    public static string StrictUserPrompt(FoFactSheet sheet, string question)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        return UserPrompt(sheet, question).Replace("\n\nAnswer now.", "", StringComparison.Ordinal)
            + "\n\nUse ONLY these numbers, exactly as written, and no others: "
            + NumberVerifier.DescribeAllowed(sheet.AllowedNumbers)
            + $"\nIf you cannot answer with only those numbers, say exactly: \"{DontHaveThat}\"\n\nAnswer now.";
    }
}
