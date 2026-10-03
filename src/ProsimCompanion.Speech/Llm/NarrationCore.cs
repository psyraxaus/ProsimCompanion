using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ProsimCompanion.Core.Speech;

namespace ProsimCompanion.Speech.Llm;

/// <summary>Why the deterministic template finished (or replaced) a streamed narration.</summary>
public enum TakeoverReason
{
    /// <summary>The model's text was spoken to its end.</summary>
    None,

    /// <summary>A sentence held a number that is not in the fact set.</summary>
    VerifyFailed,

    /// <summary>The speech ran dry and no new sentence came in time.</summary>
    Stalled,

    /// <summary>No text arrived within the timeout budget.</summary>
    Timeout,

    /// <summary>The endpoint or the stream failed.</summary>
    Error,
}

/// <summary>
/// The pure rules of a streamed LLM narration (issue #147, owner decisions D1-A and D4-1):
/// <list type="bullet">
/// <item>a sentence is released for speech only after EVERY number in it — digits or spelled
/// out — has passed <see cref="NumberVerifier"/> against the fact set;</item>
/// <item>the first sentence that fails is discarded with everything after it, and the
/// template finishes with the sections the pilot has not heard yet
/// (<see cref="Remaining"/>), in template order, never from the top.</item>
/// </list>
/// No clocks, no I/O: the shell (<see cref="StreamingNarrator"/>) owns the stream, the timing
/// and the arbiter.
/// </summary>
public sealed partial class NarrationCore
{
    private readonly IReadOnlyList<double> _allowed;
    private readonly IReadOnlyList<NarrationSection> _sections;
    private readonly List<string> _spoken = [];

    public NarrationCore(IReadOnlyList<double> allowed, IReadOnlyList<NarrationSection> sections)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        ArgumentNullException.ThrowIfNull(sections);

        _allowed = allowed;
        _sections = sections;
    }

    /// <summary>Sentences released so far, in order.</summary>
    public IReadOnlyList<string> Spoken => _spoken;

    /// <summary>The numbers that failed the last rejected sentence (for the log line).</summary>
    public IReadOnlyList<string> LastOffending { get; private set; } = [];

    /// <summary>Verifies one model sentence. True = release it for speech (and it now counts
    /// as said); false = discard it and take over.</summary>
    public bool Accept(string sentence) => Accept(sentence, verifyNumbers: true);

    /// <summary>As <see cref="Accept(string)"/>; <paramref name="verifyNumbers"/> false skips
    /// the number check (small talk, issue #152 — a figure there is trivia, not flight data).</summary>
    public bool Accept(string sentence, bool verifyNumbers)
    {
        ArgumentNullException.ThrowIfNull(sentence);

        if (verifyNumbers)
        {
            // Spelled-out numbers are turned back into digits for the check only; the sentence
            // that is spoken stays the model's own text.
            var check = NumberVerifier.Check(SpokenNumberText.ToDigits(sentence), _allowed);
            if (!check.Ok)
            {
                LastOffending = check.Offending;
                return false;
            }
        }

        _spoken.Add(sentence);
        return true;
    }

    /// <summary>The template sections still to say, given what has been released — in
    /// template order. With nothing released this is the whole template.</summary>
    public IReadOnlyList<NarrationSection> Remaining()
    {
        if (_spoken.Count == 0)
        {
            return _sections;
        }

        var said = new Heard(string.Join(" ", _spoken));
        return [.. _sections.Where(section => !IsCovered(section, said))];
    }

    /// <summary>Has the pilot already heard this section, in whatever words? See
    /// <see cref="NarrationSection"/> for the rule. Public for tests.</summary>
    public static bool IsCovered(NarrationSection section, string spokenText)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(spokenText);
        return spokenText.Trim().Length > 0 && IsCovered(section, new Heard(spokenText));
    }

    private static bool IsCovered(NarrationSection section, Heard said)
    {
        if (section.IsClosing)
        {
            return false;
        }

        if (section.IsOpening)
        {
            return true; // Something was said: the opening line is behind us.
        }

        if (section.Numbers.Count == 0 && section.AnyOf.Count == 0 && section.Runway is null)
        {
            return false; // Nothing to recognise it by: say it.
        }

        return section.Numbers.All(said.HasNumber)
            && (section.AnyOf.Count == 0 || section.AnyOf.Any(said.HasPhrase))
            && (section.Runway is null || said.HasRunway(section.Runway));
    }

    /// <summary>The released text in the three forms the matching needs.</summary>
    private sealed partial class Heard
    {
        private readonly string _digits;
        private readonly string _compact;
        private readonly List<double> _numbers;

        public Heard(string text)
        {
            // Every number as digits, single words included: "four checklists" → "4 checklists".
            _digits = SpokenNumberText.ToDigits(text, includeSingles: true).ToLowerInvariant();
            _compact = Compact(_digits);
            _numbers = [.. NumberPattern().Matches(_digits)
                .Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture))];
        }

        public bool HasNumber(double value)
            => _numbers.Any(n => Math.Abs(n - value) <= NumberVerifier.Tolerance);

        /// <summary>Letters and digits only on both sides, so "I L S Yankee" finds "ILS Yankee".
        /// A short phrase ("ILS", "RNAV") must stand as a word of its own — spelled with
        /// spaces or points between its letters, or not — so "details" never counts as "ILS".</summary>
        public bool HasPhrase(string phrase)
        {
            var needle = Compact(SpokenNumberText.ToDigits(phrase, includeSingles: true).ToLowerInvariant());
            if (needle.Length < 3)
            {
                return false;
            }

            if (needle.Length >= ShortPhrase)
            {
                return _compact.Contains(needle, StringComparison.Ordinal);
            }

            var spelled = string.Join(@"[\s.\-]*", needle.Select(c => Regex.Escape(c.ToString())));
            return Regex.IsMatch(_digits, $@"(?<![a-z0-9]){spelled}(?![a-z0-9])", RegexOptions.CultureInvariant);
        }

        /// <summary>Below this many letters a phrase is matched as a whole word.</summary>
        private const int ShortPhrase = 5;

        /// <summary>"16R" is said when "16 right" / "16R" appears; a runway with no side needs
        /// the word "runway" in front, so a bare 27 elsewhere does not count.</summary>
        public bool HasRunway(string designator)
        {
            var match = RunwayPattern().Match(designator.Trim().ToUpperInvariant());
            if (!match.Success)
            {
                return false;
            }

            var number = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)
                .ToString(CultureInfo.InvariantCulture);
            var side = match.Groups[2].Value switch
            {
                "L" => "(?:l\\b|left)",
                "R" => "(?:r\\b|right)",
                "C" => "(?:c\\b|centre|center)",
                _ => null,
            };
            var pattern = side is null
                ? $@"\brunway\s+0?{number}\b(?!\s*(?:l\b|r\b|c\b|left|right|centre|center))"
                : $@"\b0?{number}\s*{side}";
            return Regex.IsMatch(_digits, pattern, RegexOptions.CultureInvariant);
        }

        private static string Compact(string text)
        {
            var builder = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                if (char.IsLetterOrDigit(c))
                {
                    builder.Append(c);
                }
            }

            return builder.ToString();
        }

        [GeneratedRegex(@"\d+(?:\.\d+)?")]
        private static partial Regex NumberPattern();

        [GeneratedRegex(@"^(?:RW)?(\d{1,2})([LRC]?)$")]
        private static partial Regex RunwayPattern();
    }
}
