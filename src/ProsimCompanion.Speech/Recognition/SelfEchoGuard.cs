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
    /// <param name="StillPlaying">True for the line the arbiter is playing at transcript time:
    /// its stamp is then not a playback end, so the timing rules of <see cref="HeardUtterance"/>
    /// do not apply to it — it stays an echo candidate.</param>
    public sealed record SpokenLine(string Text, DateTimeOffset SpokenAtUtc, bool PlayedAloud, bool StillPlaying = false);

    /// <summary>How long after the mic reopens a short transcript can still be the end of the
    /// FO's own line (issue #172, 2026-11-14). An echo that slips past the mic gate is audible
    /// the moment the gate opens — playback end plus <c>speech.micTailAfterSpeakingMs</c> — so
    /// the captured utterance begins there; this margin on top covers a playback device that
    /// finishes late, the VAD's onset and the 50 ms capture buffers. A judgement, not a
    /// measurement: no flight since the gate (2026-10-10) has recorded a true echo to time.</summary>
    public static TimeSpan EchoOnsetMargin { get; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>The tail after a line's playback end inside which an utterance must have begun
    /// for a short transcript to be an echo candidate: the mic gate's own tail plus
    /// <see cref="EchoOnsetMargin"/> (2.2 s at the default 700 ms).</summary>
    public static TimeSpan EchoTailFor(int micTailMs)
        => TimeSpan.FromMilliseconds(Math.Max(0, micTailMs)) + EchoOnsetMargin;

    /// <summary>What the recognizer knows about the utterance behind a transcript (issue #172,
    /// EGLL–LOWW 2026-11-14: "Descent checklist." asked 0.8 s and again 6.6 s after the FO's
    /// "Descent checklist cancelled." was dropped twice as the FO's own voice).</summary>
    /// <param name="BeganUtc">When the captured utterance began — its close time minus the
    /// length of its audio, pre-roll included.</param>
    /// <param name="EchoTail">See <see cref="EchoTailFor"/>.</param>
    /// <param name="CommandPhrases">The phrases the live listening window is open for (checklist
    /// starts, global commands, feature phrases). A transcript that IS one of them is a command
    /// unless it began before the matching line's playback ended.</param>
    public sealed record HeardUtterance(DateTimeOffset BeganUtc, TimeSpan EchoTail, IReadOnlyList<string> CommandPhrases);

    /// <summary>Convenience for callers (and the original tests) with no clock: every line
    /// counts as played aloud just now, with nothing expected.</summary>
    public static bool LooksLikeOwnSpeech(string heard, IEnumerable<string> recentlySpoken)
    {
        ArgumentNullException.ThrowIfNull(recentlySpoken);
        var now = DateTimeOffset.UtcNow;
        return LooksLikeOwnSpeech(
            heard, recentlySpoken.Select(s => new SpokenLine(s, now, PlayedAloud: true)), now, OwnSpeechWindow, []);
    }

    /// <summary>The text-only test, for callers that do not know when the utterance began:
    /// see <see cref="MatchOwnSpeech"/> with no <see cref="HeardUtterance"/>.</summary>
    public static bool LooksLikeOwnSpeech(
        string heard,
        IEnumerable<SpokenLine> recentlySpoken,
        DateTimeOffset nowUtc,
        TimeSpan window,
        IEnumerable<string> expectedReplies)
        => MatchOwnSpeech(heard, recentlySpoken, nowUtc, window, expectedReplies, utterance: null) is not null;

    /// <summary>True when the transcript is the FO's own recent line heard back: a short one
    /// (≤ 4 words) sits inside a line that was played aloud, a longer one shares at least 60 %
    /// of its words with a line — "You too. See you at the next line up." heard back as "See
    /// you at the next line-up. Didn't catch that." counts; a genuine "yes" or "set flaps one"
    /// never does. Lines older than <paramref name="window"/> at <paramref name="nowUtc"/> are
    /// ignored (pruned here, at transcript time — never from a stale list: 2026-10-10 "Taxi
    /// checklist." was dropped 71 s after "Taxi checklist complete."), and a transcript that
    /// is one of the <paramref name="expectedReplies"/> (the pending clarifier's candidate, the
    /// open checklist item's accepted phrases) is the pilot answering, not an echo, however
    /// recently the FO said those very words.
    /// <para>With an <paramref name="utterance"/> (issue #172, 2026-11-14) two timing rules
    /// join the text test, each against the line's playback end: a short transcript is an echo
    /// candidate of a line only when the utterance began no later than
    /// <see cref="HeardUtterance.EchoTail"/> after it (an echo overlaps the playback or trails
    /// it at once; a pilot repeating the FO's words speaks later), and a transcript that is
    /// exactly one of the <see cref="HeardUtterance.CommandPhrases"/> is an echo candidate only
    /// when the utterance began before the playback ended. The longer-transcript overlap test
    /// keeps the whole window — it is the last defence against the 2026-10-10 answer loop.</para>
    /// Returns the line the transcript was matched to, or null when it is not the FO's own.</summary>
    public static SpokenLine? MatchOwnSpeech(
        string heard,
        IEnumerable<SpokenLine> recentlySpoken,
        DateTimeOffset nowUtc,
        TimeSpan window,
        IEnumerable<string> expectedReplies,
        HeardUtterance? utterance)
    {
        ArgumentNullException.ThrowIfNull(recentlySpoken);
        ArgumentNullException.ThrowIfNull(expectedReplies);
        var heardTokens = Tokens(heard);
        if (heardTokens.Count < 2)
        {
            return null;
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
                return null;
            }
        }

        var isCommand = utterance is not null
            && utterance.CommandPhrases.Any(phrase => " " + string.Join(' ', Tokens(phrase)) + " " == heardPhrase);

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

            // A command of the open window, begun after this line had finished playing: the
            // pilot, whatever words the two share ("Descent checklist." 0.8 s after "Descent
            // checklist cancelled.").
            var timed = utterance is not null && !line.StillPlaying;
            if (timed && isCommand && utterance!.BeganUtc >= line.SpokenAtUtc)
            {
                continue;
            }

            if (heardTokens.Count <= ShortPhraseWords)
            {
                if (!line.PlayedAloud)
                {
                    continue;
                }

                // Begun after the echo tail: too late to be this line heard back.
                if (timed && utterance!.BeganUtc > line.SpokenAtUtc + utterance.EchoTail)
                {
                    continue;
                }

                var spokenPhrase = " " + string.Join(' ', spokenTokens) + " ";
                if (spokenPhrase.Contains(heardPhrase, StringComparison.Ordinal))
                {
                    return line;
                }

                continue;
            }

            var spokenSet = new HashSet<string>(spokenTokens, StringComparer.Ordinal);
            var hits = heardTokens.Count(spokenSet.Contains);
            if ((double)hits / heardTokens.Count >= OwnSpeechThreshold)
            {
                return line;
            }
        }

        return null;
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
