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

        // "Where are we?" is three words and starts with no lead-in, yet it is unmistakably a
        // place question (issue #153): the place phrases stand on their own.
        if (options.WhereAreWe && IsPlaceQuestion(normalized))
        {
            return true;
        }

        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < Math.Max(1, options.MinimumWords))
        {
            return false;
        }

        // Small talk (issue #152): long enough is enough — "who is better, Chelsea or Arsenal"
        // has no lead-in. The known phrases still win: the router asks here only after
        // every feature, drill and checklist start declined.
        if (options.SmallTalk)
        {
            return true;
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

    /// <summary>Spoken by the chat path when the model realises the question is about the
    /// flight after all; the service then re-runs it on the strict path.</summary>
    public const string LetMeCheck = "Let me check.";

    /// <summary>Phrases that make a question about the place below (issue #153). Checked
    /// before the flight words — "where are we" contains "are we", a flight word — and
    /// matched inside the normalized text, so "what are we flying over right now" and
    /// "flying over anything interesting" both count.</summary>
    private static readonly string[] PlacePhrases =
    [
        "flying over", "fly over", "flying above", "over right now", "below us", "beneath us", "under us", "underneath us",
        "down there", "down below", "where are we", "where we are", "whereabouts are we", "where exactly are we",
        "what country", "which country", "what city", "which city", "what town", "which town", "that city", "that town",
        "what sea", "which sea", "what ocean", "what lake", "what island", "which island", "what mountains", "those mountains",
        "that mountain", "what river", "that river", "what desert", "out the window", "out of the window", "out my window",
        "out your window", "on the left", "on the right", "to the left", "to the right", "off the left", "off the right",
        "left side", "right side", "what is that", "what s that", "what place", "nearest city", "nearest town", "closest city",
        "closest town", "big city", "any cities", "anything interesting",
    ];

    /// <summary>True when the (normalized) question is about the place below.</summary>
    public static bool IsPlaceQuestion(string normalizedQuestion)
    {
        if (string.IsNullOrWhiteSpace(normalizedQuestion))
        {
            return false;
        }

        var text = " " + normalizedQuestion.Trim() + " ";
        foreach (var phrase in PlacePhrases)
        {
            if (text.Contains(" " + phrase + " ", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The place path's instructions: the position is already spoken (the deterministic
    /// line from the atlas); the model adds one or two facts about the named places — from the
    /// SOURCE text when there is one, else from general knowledge — and nothing about the flight.</summary>
    public static string PlaceSystemPrompt(string personaFragment, bool hasSource)
        => (personaFragment ?? "")
            + "You are the First Officer of an Airbus A320. The Captain asked what we are flying over. You have "
            + "ALREADY told the Captain where we are (the WE ARE line below) — do not repeat it and do not give "
            + "any distance, direction, position or coordinate. Add one or two short spoken sentences with "
            + "something interesting about the PLACES named: history, a landmark, what the place is known for. "
            + (hasSource
                ? "Use ONLY the SOURCE text for facts; if it has nothing interesting, say one sentence about the place from the source. "
                : "Use general knowledge; prefer well-known facts over precise figures. ")
            + "Friendly, in character, plain English for text-to-speech: no markdown, no lists, no emoji. "
            + "Never say anything about this flight's fuel, weights, speeds, altitudes, times, weather or route, "
            + "and never tell the Captain to do anything.";

    public static string PlaceUserPrompt(string spokenPosition, string placeNames, string? source, string question)
        => "WE ARE: " + spokenPosition
            + "\nPLACES: " + placeNames
            + (string.IsNullOrWhiteSpace(source) ? "" : "\nSOURCE: " + source)
            + "\n\nCAPTAIN ASKED: " + question + "\n\nAdd the facts now.";

    /// <summary>Words that mark a question as being about THIS flight (issue #152). A
    /// question with any of them takes the strict, fact-sheet-only path whatever the small-talk
    /// switch says; one without them is conversation. Local and instant — no model round trip
    /// to sort the question.</summary>
    private static readonly string[] FlightWords =
    [
        "fuel", "fob", "kilo", "tonne", "weight", "zfw", "tow", "gross", "payload", "cargo", "passenger", "pax",
        "altitude", "flight level", "level", "climb", "descent", "descend", "top of", "tod", "cruise",
        "speed", "knot", "mach", "heading", "track", "distance", "mile", "nautical", "eta", "arrival", "arrive",
        "land", "touchdown", "takeoff", "take off", "departure", "depart", "block", "flight time", "how long",
        "runway", "gate", "stand", "taxi", "pushback", "push back", "boarding", "board", "door",
        "weather", "metar", "atis", "wind", "visibility", "ceiling", "cloud", "temperature", "qnh", "altimeter",
        "minimum", "minima", "decision", "approach", "loadsheet", "load sheet", "ofp", "flight plan", "route",
        "destination", "alternate", "origin", "callsign", "flight number", "tech log", "defect", "mel",
        "engine", "apu", "gear", "flap", "fms", "mcdu", "fcu", "gsx", "prosim", "phase", "time now", "zulu", "utc",
        "we have", "do we", "are we", "our ",
    ];

    /// <summary>True when the question is about this flight (strict path); false = small talk.
    /// Only meaningful with the small-talk switch on — without it every question is strict.</summary>
    public static bool IsFlightQuestion(string question)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return true;
        }

        var text = " " + CommandMatcher.Normalize(question) + " ";
        foreach (var word in FlightWords)
        {
            // Three-letter tokens (fob, eta, pax, apu…) and the trailing-space ones ("our ")
            // match whole words only; longer ones match inside words ("land" → "landing").
            var needle = word.Trim();
            var whole = word.EndsWith(' ') || needle.Length <= 3;
            if (whole ? text.Contains(" " + needle + " ", StringComparison.Ordinal) : text.Contains(needle, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Words that, together with a figure, make a small-talk sentence look like flight
    /// data — such a sentence is refused on the chat path (the guard).</summary>
    private static readonly string[] FlightDataWords =
    [
        "fuel", "tonne", "kilo", "feet", "knot", "flight level", "heading", "runway", "qnh", "altitude",
        "passenger", "zulu", "eta", "descent", "weight", "nautical", "mile",
    ];

    /// <summary>The chat path's per-sentence guard: a sentence about this flight with a figure
    /// in it is refused — the strict path is the only place figures about the flight come from.</summary>
    public static bool ChatSentenceAllowed(string sentence)
    {
        if (string.IsNullOrWhiteSpace(sentence))
        {
            return true;
        }

        var digits = Llm.SpokenNumberText.ToDigits(sentence);
        if (!digits.Any(char.IsAsciiDigit))
        {
            return true;
        }

        var text = " " + CommandMatcher.Normalize(sentence) + " ";
        return !FlightDataWords.Any(w => text.Contains(w, StringComparison.Ordinal));
    }

    /// <summary>The chat path's instructions: in character, light, general knowledge allowed,
    /// nothing about this flight — and a fixed escape line when the model finds the question
    /// is about the flight after all.</summary>
    public static string ChatSystemPrompt(string personaFragment)
        => (personaFragment ?? "")
            + "You are the First Officer of an Airbus A320, making conversation with the Captain on the "
            + "flight deck. The Captain asked something that is NOT about this flight: small talk, trivia, "
            + "a fun fact, a joke, an opinion. Answer in one or two short spoken sentences — friendly, a "
            + "little dry humour is fine, stay in character. You may use general knowledge. Plain English "
            + "for text-to-speech: no markdown, no lists, no emoji. Never say anything about this flight's "
            + "fuel, weights, speeds, altitudes, times, weather, route or passengers, and never tell the "
            + "Captain to do anything. If the question turns out to be about this flight or the aircraft "
            + $"state, reply with exactly: \"{LetMeCheck}\" and nothing more.";

    public static string ChatUserPrompt(string question) => "CAPTAIN SAYS: " + question + "\n\nReply now.";

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
