namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Free-form questions to the First Officer (issue #149), <c>speech.foQuestions</c>. A
/// read-only "ask the FO" path: an utterance nothing else matched, beginning with one of the
/// <see cref="LeadIns"/> and long enough to be a question, is answered by the LLM from the
/// live fact sheet — spoken only, never acted on. Needs the LAN transcription engine; the
/// offline closed-grammar engine cannot hear free text.
/// </summary>
public sealed class FoQuestionOptions
{
    /// <summary>Master switch. Off: questions fall through to the ordinary did-not-catch line.</summary>
    public bool Enabled { get; set; }

    /// <summary>First word(s) an utterance must start with to be considered a question, as one
    /// comma-separated line (a string, not a list: nested list defaults are barred by the
    /// option-section guard). Matched after normalization, so "What's" and "what is" both
    /// start with "what".</summary>
    public string LeadIns { get; set; } =
        "question, tell me, what, how, when, where, which, why, are we, do we, is the, is there, have we, did we, can you tell me";

    /// <summary>The lead-ins split and trimmed.</summary>
    public IReadOnlyList<string> LeadInList()
        => LeadIns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Fewer words than this is never a question — the short utterances are the
    /// exact phrases other features own, misheard.</summary>
    public int MinimumWords { get; set; } = 4;

    /// <summary>Small talk and fun facts (issue #152). On: any free-form sentence of
    /// <see cref="MinimumWords"/> or more that matched nothing else goes to the FO — no
    /// lead-in needed — and a question that is NOT about the flight is answered from general
    /// knowledge, in character, one or two light sentences. Flight questions keep the strict,
    /// fact-sheet-only, number-verified path. Off: lead-ins and the fact sheet only.</summary>
    public bool SmallTalk { get; set; }

    /// <summary>"What are we flying over?" (issue #153). On: a question about the place below
    /// — where are we, what country / city is that, what is out the left window — is answered
    /// at once from the shipped offline atlas (country, region or sea, the nearest notable
    /// towns with distance and side), then the LLM adds a fact or two about those places.
    /// Works with the small-talk switch off; no lead-in or minimum word count needed for the
    /// place phrases. Off: such questions take the ordinary path (fact sheet: "I don't have that").</summary>
    public bool WhereAreWe { get; set; }

    /// <summary>With <see cref="WhereAreWe"/>: fetch a short Wikipedia summary of the nearest
    /// town (one small request, three-second ceiling, cached) and give the model THAT as the
    /// source of its facts instead of its own memory. Off, or on failure: the facts are the
    /// model's general knowledge, as for small talk.</summary>
    public bool WikipediaFacts { get; set; }

    /// <summary>Seconds from the question to the first spoken word of the answer before the FO
    /// gives up ("I don't have a verified answer for that").</summary>
    public int TimeBudgetSeconds { get; set; } = 6;

    /// <summary>Seconds of silence after the question before the FO says "Stand by" (0 = never).</summary>
    public int StandBySeconds { get; set; } = 2;
}
