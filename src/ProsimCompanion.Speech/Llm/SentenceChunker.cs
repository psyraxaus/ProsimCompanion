using System.Text;

namespace ProsimCompanion.Speech.Llm;

/// <summary>
/// Cuts a stream of LLM text deltas into speakable sentences (issue #147). Pure and
/// incremental: feed deltas with <see cref="Push"/>, take what is complete, and call
/// <see cref="Flush"/> when the stream ends.
/// <para>
/// A sentence ends at <c>.</c>, <c>!</c> or <c>?</c> — but only once the text AFTER it has
/// arrived, because that is what tells an end from a look-alike. No cut is made:
/// </para>
/// <list type="bullet">
/// <item>inside a number — "118.10", "3.0 degrees" (a digit follows the point);</item>
/// <item>after an abbreviation — "approx.", "e.g.", "ft.", "No.";</item>
/// <item>after a single letter — "I. L. S.", "runway 16 L.", initials;</item>
/// <item>when the next word starts in lower case — the point was not an end;</item>
/// <item>when the piece would be a fragment (under two words) — "1." joins what follows.</item>
/// </list>
/// A line break ends a sentence that has no terminator (models write lists). Text that runs
/// past <see cref="MaxLength"/> with no end in sight is cut at the last clause break, else the
/// last space: a sentence that never ends must not hold the speech back for ever.
/// </summary>
public sealed class SentenceChunker
{
    /// <summary>Longest piece handed on without a sentence end, characters.</summary>
    public const int MaxLength = 320;

    /// <summary>A piece shorter than this many words is a fragment and joins the next.</summary>
    private const int MinWords = 2;

    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "approx", "e.g", "i.e", "etc", "vs", "mr", "mrs", "ms", "dr", "st", "no", "nos",
        "ft", "kt", "kts", "nm", "deg", "min", "max", "alt", "hdg", "freq", "rwy", "dep", "arr",
        "capt", "f/o", "fig", "ref", "est", "incl",
    };

    private readonly StringBuilder _buffer = new();

    /// <summary>Adds a delta and returns every sentence it completed, in order (often none).</summary>
    public IReadOnlyList<string> Push(string? delta)
    {
        if (string.IsNullOrEmpty(delta))
        {
            return [];
        }

        _buffer.Append(delta);
        var sentences = new List<string>();
        while (TryCut(out var sentence))
        {
            sentences.Add(sentence);
        }

        return sentences;
    }

    /// <summary>The end of the stream: whatever is left is the last sentence. Null when
    /// nothing speakable remains.</summary>
    public string? Flush()
    {
        var tail = Clean(_buffer.ToString());
        _buffer.Clear();
        return tail.Length == 0 ? null : tail;
    }

    private bool TryCut(out string sentence)
    {
        sentence = "";

        // Leading blank space (the gap after the previous sentence, blank lines) is dropped
        // first, so every piece cut below starts on real text.
        var lead = 0;
        while (lead < _buffer.Length && char.IsWhiteSpace(_buffer[lead]))
        {
            lead++;
        }

        _buffer.Remove(0, lead);

        var text = _buffer.ToString();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\n' or '\r')
            {
                // A line break ends whatever stands before it, terminator or not.
                if (Take(text, i, i + 1, out sentence))
                {
                    return true;
                }

                continue;
            }

            if (c is not ('.' or '!' or '?'))
            {
                continue;
            }

            // Swallow a run of terminators and closing quotes: "…", "?!", '."'.
            var end = i + 1;
            while (end < text.Length && text[end] is '.' or '!' or '?' or '"' or '\'' or ')' or '”' or '’')
            {
                end++;
            }

            if (end >= text.Length)
            {
                return false; // What follows has not arrived: cannot tell yet.
            }

            if (!char.IsWhiteSpace(text[end]))
            {
                i = end - 1; // "118.10", "3.0", "e.g.x": not an end.
                continue;
            }

            // The first character of the next word decides the rest.
            var next = end;
            while (next < text.Length && char.IsWhiteSpace(text[next]) && text[next] is not ('\n' or '\r'))
            {
                next++;
            }

            if (next >= text.Length)
            {
                return false; // Only spaces so far: wait for the next word.
            }

            if (c == '.' && !IsSentenceEnd(text, i, text[next]))
            {
                i = end - 1;
                continue;
            }

            if (Take(text, end, next, out sentence))
            {
                return true;
            }

            i = end - 1;
        }

        if (text.Length > MaxLength)
        {
            var cut = SafetyCut(text);
            return Take(text, cut, cut, out sentence, force: true);
        }

        return false;
    }

    /// <summary>Is the point at <paramref name="index"/> a sentence end, given the first
    /// character of what follows?</summary>
    private static bool IsSentenceEnd(string text, int index, char nextStart)
    {
        if (char.IsLower(nextStart))
        {
            return false;
        }

        // The word the point is attached to.
        var start = index;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1]))
        {
            start--;
        }

        var word = text[start..index].Trim('(', '"', '\'', '“', '‘');
        if (word.Length == 1 && char.IsLetter(word[0]))
        {
            return false; // "I. L. S.", "16 L.", an initial.
        }

        return !Abbreviations.Contains(word);
    }

    /// <summary>Removes <c>text[..end]</c> as a sentence when it is long enough to stand
    /// alone; <paramref name="resume"/> is where the buffer continues.</summary>
    private bool Take(string text, int end, int resume, out string sentence, bool force = false)
    {
        sentence = Clean(text[..end]);
        if (sentence.Length == 0 || (!force && WordCount(sentence) < MinWords))
        {
            sentence = "";
            return false;
        }

        _buffer.Remove(0, resume);
        return true;
    }

    private static int SafetyCut(string text)
    {
        var window = text[..MaxLength];
        var clause = window.LastIndexOfAny([',', ';', ':']);
        if (clause > MaxLength / 2)
        {
            return clause + 1;
        }

        var space = window.LastIndexOf(' ');
        return space > 0 ? space : MaxLength;
    }

    private static int WordCount(string text)
        => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>Trims, and collapses any run of whitespace (line breaks included) to one space.</summary>
    private static string Clean(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
