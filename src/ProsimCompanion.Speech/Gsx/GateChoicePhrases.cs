using System.Text.RegularExpressions;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Gsx;

/// <summary>
/// The FO's "which one?" question for an arrival gate GSX split into suffix variants
/// (issue #156, 2026-10-11: EFHK W36 → 36A / 36B). The <see cref="GateChoiceStore"/> raises
/// the question; the shared dialogue voices it from this table — "W36 is 36 Alpha or 36 Bravo
/// in this scenery — which?" — listens for "36 alpha" / "alpha" / "the first" / "36A", picks
/// the choice and reads it back. Timeout, two misses or "your call" leave the question open
/// with a word: the GSX pillar takes the first variant at taxi-in. The interpretation adapter
/// here is the gate's own: NATO spelling of the suffix, ordinals, a lone letter.
/// </summary>
public static class GateChoicePhrases
{
    private static readonly IReadOnlyList<string> HandBack = ["your call", "leave it", "you pick", "cancel"];

    private static readonly Dictionary<char, string> Nato = new()
    {
        ['A'] = "alpha", ['B'] = "bravo", ['C'] = "charlie", ['D'] = "delta", ['E'] = "echo", ['F'] = "foxtrot",
        ['G'] = "golf", ['H'] = "hotel", ['I'] = "india", ['J'] = "juliet", ['K'] = "kilo", ['L'] = "lima",
        ['M'] = "mike", ['N'] = "november", ['O'] = "oscar", ['P'] = "papa", ['Q'] = "quebec", ['R'] = "romeo",
        ['S'] = "sierra", ['T'] = "tango", ['U'] = "uniform", ['V'] = "victor", ['W'] = "whiskey", ['X'] = "x-ray",
        ['Y'] = "yankee", ['Z'] = "zulu",
    };

    private static readonly Regex LabelShape = new(@"^(?<number>\d{1,4})(?<suffix>[A-Za-z]{1,2})$", RegexOptions.Compiled);

    /// <summary>The table the shared dialogue voices the gate question from.</summary>
    public static CaptainsQuestionPhrases Table { get; } = new(
        Topic: GateChoiceStore.QuestionDefinition.Topic,
        LogName: "Gate choice question",
        MicOwner: "gateChoice",
        EventName: "gate-choice-question",
        Grammar: Grammar,
        SpokenPrompt: SpokenPrompt,
        Interpret: (heard, question) =>
        {
            var text = CommandMatcher.Normalize(heard);
            if (HandBack.Any(h => text.Contains(h, StringComparison.Ordinal)))
            {
                return QuestionReply.HandBack;
            }

            return Interpret(heard, question.Choices) is { } choice ? QuestionReply.Pick(choice) : null;
        },
        UnclearPrompt: question => $"Say again — {SpokenList(question)}?",
        UnansweredPhrase: (question, _) => $"Your call — I'll take {SpokenLabel(question.Choices[0].Label)} at taxi-in unless you say otherwise.",
        RaisedPayload: question => new { prompt = SpokenPrompt(question), gate = question.Subject, options = question.Choices.Select(c => c.Label).ToList() },
        AnsweredPayload: (question, reply, heard) => new { outcome = "answered", gate = question.Subject, chosen = reply.Choice?.Label, heard },
        UnansweredPayload: (question, _) => new { outcome = "unanswered", gate = question.Subject, fallback = question.Choices[0].Label },
        PickReadback: choice => $"{SpokenLabel(choice.Label)} it is.");

    /// <summary>"36A" → "36 Alpha" — how the FO says a suffixed stand; other labels as they are.</summary>
    public static string SpokenLabel(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        var match = LabelShape.Match(label.Trim());
        if (!match.Success)
        {
            return label;
        }

        var letters = match.Groups["suffix"].Value.ToUpperInvariant()
            .Select(c => Nato.TryGetValue(c, out var word) ? char.ToUpperInvariant(word[0]) + word[1..] : c.ToString());
        return $"{match.Groups["number"].Value} {string.Join(" ", letters)}";
    }

    /// <summary>"W36 is 36 Alpha or 36 Bravo in this scenery — which?" Public for tests.</summary>
    public static string SpokenPrompt(CaptainsQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);
        return $"{question.Subject} is {SpokenList(question)} in this scenery — which?";
    }

    /// <summary>
    /// Which choice the pilot named: the label ("36A", "36 alpha"), the suffix alone
    /// ("alpha", a lone "a"), or an ordinal ("the first", "second"). Null when nothing or more
    /// than one choice matches — the FO asks again rather than guessing.
    /// </summary>
    public static QuestionChoice? Interpret(string heard, IReadOnlyList<QuestionChoice> candidates)
    {
        ArgumentNullException.ThrowIfNull(heard);
        ArgumentNullException.ThrowIfNull(candidates);
        var text = CommandMatcher.Normalize(heard);
        if (text.Length == 0)
        {
            return null;
        }

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = new List<QuestionChoice>();
        foreach (var candidate in candidates)
        {
            var label = CommandMatcher.Normalize(candidate.Label);
            var spoken = CommandMatcher.Normalize(SpokenLabel(candidate.Label));
            if (ContainsPhrase(text, label) || ContainsPhrase(text, spoken) || text.Replace(" ", "", StringComparison.Ordinal) == label.Replace(" ", "", StringComparison.Ordinal))
            {
                matches.Add(candidate);
                continue;
            }

            var shape = LabelShape.Match(candidate.Label.Trim());
            if (!shape.Success)
            {
                continue;
            }

            var suffix = shape.Groups["suffix"].Value.ToUpperInvariant();
            var natoWords = suffix.Select(c => Nato.TryGetValue(c, out var w) ? w : c.ToString().ToLowerInvariant()).ToList();
            if (natoWords.All(words.Contains) || (words.Length == 1 && words[0].Equals(suffix, StringComparison.OrdinalIgnoreCase)))
            {
                matches.Add(candidate);
            }
        }

        if (matches.Count == 0)
        {
            var ordinal = text switch
            {
                _ when ContainsPhrase(text, "first") || text == "one" || text == "1" => 0,
                _ when ContainsPhrase(text, "second") || text == "two" || text == "2" => 1,
                _ when ContainsPhrase(text, "third") || text == "three" || text == "3" => 2,
                _ => -1,
            };
            if (ordinal >= 0 && ordinal < candidates.Count)
            {
                return candidates[ordinal];
            }
        }

        return matches.Count == 1 ? matches[0] : null;
    }

    private static List<string> Grammar(CaptainsQuestion question)
        => question.Choices
            .SelectMany(c => new[] { c.Label, SpokenLabel(c.Label) })
            .Concat(["first", "the first", "second", "the second", "third"])
            .Concat(ConfirmVocabulary.SayAgain)
            .Concat(HandBack)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool ContainsPhrase(string text, string phrase)
        => phrase.Length > 0 && Regex.IsMatch(text, $@"(?<![a-z0-9]){Regex.Escape(phrase)}(?![a-z0-9])");

    private static string SpokenList(CaptainsQuestion question)
    {
        var spoken = question.Choices.Select(c => SpokenLabel(c.Label)).ToList();
        return spoken.Count <= 1 ? string.Join("", spoken) : $"{string.Join(", ", spoken.Take(spoken.Count - 1))} or {spoken[^1]}";
    }
}
