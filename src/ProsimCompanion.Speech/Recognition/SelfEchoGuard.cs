using System.Text;

namespace ProsimCompanion.Speech.Recognition;

/// <summary>
/// Keeps the FO from hearing itself (2026-10-10: a VBAN loop put the FO's voice on the
/// cockpit speakers and the open mic turned every answer into the next question). Two pure
/// checks, both testable without a device:
/// <list type="bullet">
/// <item><see cref="IsSuppressed"/> — the mic is ignored while the arbiter plays and for a
/// tail after it stops (room reverb, device delay).</item>
/// <item><see cref="LooksLikeOwnSpeech"/> — a transcript whose words mostly came out of the
/// FO's mouth moments ago is dropped whatever the timing (a long device delay, a slow ASR
/// server, the tail set too short).</item>
/// </list>
/// </summary>
public sealed class SelfEchoGuard
{
    /// <summary>Token overlap at or above which a transcript counts as the FO's own words.</summary>
    public const double OwnSpeechThreshold = 0.6;

    /// <summary>The window the clock-less overload uses (the recognizer passes
    /// <c>speech.selfEchoWindowSeconds</c> instead, issue #172).</summary>
    public static TimeSpan OwnSpeechWindow { get; } = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private bool _speaking;
    private DateTimeOffset _spokeUntilUtc = DateTimeOffset.MinValue;

    /// <summary>Feed the arbiter's playing state; the tail starts when it goes false.</summary>
    public void Update(bool speaking, DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            if (_speaking && !speaking)
            {
                _spokeUntilUtc = nowUtc;
            }

            _speaking = speaking;
        }
    }

    /// <summary>True while the FO speaks, and for <paramref name="tailMs"/> after it stops.</summary>
    public bool IsSuppressed(DateTimeOffset nowUtc, int tailMs)
    {
        lock (_gate)
        {
            return _speaking || nowUtc < _spokeUntilUtc.AddMilliseconds(Math.Max(0, tailMs));
        }
    }

    /// <summary>Heard transcripts of up to this many words must appear as a whole phrase in
    /// the FO line; longer ones match on word overlap. A short pilot command built from words
    /// the FO just used ("set flaps one" after "Flaps one set") is not an echo.</summary>
    public const int ShortPhraseWords = 4;

    /// <summary>One FO line as the guard sees it (issue #172).</summary>
    /// <param name="Text">What the FO said.</param>
    /// <param name="SpokenAtUtc">When the arbiter disposed of it — the window is measured from here.</param>
    /// <param name="PlayedAloud">True when the line actually went out through the playback
    /// device (arbiter outcome Spoken / Segment, or playing now). A deferred, dropped or
    /// suppressed line never reached a speaker and cannot have been heard back.</param>
    public sealed record SpokenLine(string Text, DateTimeOffset SpokenAtUtc, bool PlayedAloud);

    /// <summary>Convenience for callers (and the original tests) with no clock: every line
    /// counts as played aloud just now, with nothing expected.</summary>
    public static bool LooksLikeOwnSpeech(string heard, IEnumerable<string> recentlySpoken)
    {
        ArgumentNullException.ThrowIfNull(recentlySpoken);
        var now = DateTimeOffset.UtcNow;
        return LooksLikeOwnSpeech(
            heard, recentlySpoken.Select(s => new SpokenLine(s, now, PlayedAloud: true)), now, OwnSpeechWindow, []);
    }

    /// <summary>True when the transcript is the FO's own recent line heard back: a short one
    /// (≤ 4 words) sits inside a line that was played aloud, a longer one shares at least 60 %
    /// of its words with a line — "You too. See you at the next line up." heard back as "See
    /// you at the next line-up. Didn't catch that." counts; a genuine "yes" or "set flaps one"
    /// never does. Lines older than <paramref name="window"/> at <paramref name="nowUtc"/> are
    /// ignored (pruned here, at transcript time — never from a stale list: 2026-10-10 "Taxi
    /// checklist." was dropped 71 s after "Taxi checklist complete."), and a transcript that
    /// is one of the <paramref name="expectedReplies"/> (the pending clarifier's candidate, the
    /// open checklist item's accepted phrases) is the pilot answering, not an echo, however
    /// recently the FO said those very words.</summary>
    public static bool LooksLikeOwnSpeech(
        string heard,
        IEnumerable<SpokenLine> recentlySpoken,
        DateTimeOffset nowUtc,
        TimeSpan window,
        IEnumerable<string> expectedReplies)
    {
        ArgumentNullException.ThrowIfNull(recentlySpoken);
        ArgumentNullException.ThrowIfNull(expectedReplies);
        var heardTokens = Tokens(heard);
        if (heardTokens.Count < 2)
        {
            return false;
        }

        var heardPhrase = " " + string.Join(' ', heardTokens) + " ";
        foreach (var expected in expectedReplies)
        {
            var expectedTokens = Tokens(expected);
            if (expectedTokens.Count == 0)
            {
                continue;
            }

            var expectedPhrase = " " + string.Join(' ', expectedTokens) + " ";
            if (expectedPhrase.Contains(heardPhrase, StringComparison.Ordinal)
                || heardPhrase.Contains(expectedPhrase, StringComparison.Ordinal))
            {
                return false;
            }
        }

        var cutoff = nowUtc - (window < TimeSpan.Zero ? TimeSpan.Zero : window);
        foreach (var line in recentlySpoken)
        {
            if (line.SpokenAtUtc < cutoff)
            {
                continue;
            }

            var spokenTokens = Tokens(line.Text);
            if (spokenTokens.Count == 0)
            {
                continue;
            }

            if (heardTokens.Count <= ShortPhraseWords)
            {
                if (!line.PlayedAloud)
                {
                    continue;
                }

                var spokenPhrase = " " + string.Join(' ', spokenTokens) + " ";
                if (spokenPhrase.Contains(heardPhrase, StringComparison.Ordinal))
                {
                    return true;
                }

                continue;
            }

            var spokenSet = new HashSet<string>(spokenTokens, StringComparer.Ordinal);
            var hits = heardTokens.Count(spokenSet.Contains);
            if ((double)hits / heardTokens.Count >= OwnSpeechThreshold)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Lower-case words in order, punctuation stripped. A hyphen splits ("line-up"
    /// and "line up" agree), an apostrophe joins ("don't" stays one word).</summary>
    private static List<string> Tokens(string? text)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return list;
        }

        var word = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                word.Append(char.ToLowerInvariant(ch));
            }
            else if (ch is '\'' or '’')
            {
                // joins: "don't" stays one token
            }
            else if (word.Length > 0)
            {
                list.Add(word.ToString());
                word.Clear();
            }
        }

        if (word.Length > 0)
        {
            list.Add(word.ToString());
        }

        return list;
    }
}
