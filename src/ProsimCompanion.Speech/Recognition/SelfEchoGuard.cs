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

    /// <summary>How long a spoken line stays eligible for the overlap check.</summary>
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

    /// <summary>True when the transcript is the FO's own recent line heard back: a short one
    /// (≤ 4 words) sits inside the line as a phrase, a longer one shares at least 60 % of its
    /// words with it — "You too. See you at the next line up." heard back as "See you at the
    /// next line-up. Didn't catch that." counts; a genuine "yes" or "set flaps one" never does.</summary>
    public static bool LooksLikeOwnSpeech(string heard, IEnumerable<string> recentlySpoken)
    {
        ArgumentNullException.ThrowIfNull(recentlySpoken);
        var heardTokens = Tokens(heard);
        if (heardTokens.Count < 2)
        {
            return false;
        }

        var heardPhrase = " " + string.Join(' ', heardTokens) + " ";
        foreach (var spoken in recentlySpoken)
        {
            var spokenTokens = Tokens(spoken);
            if (spokenTokens.Count == 0)
            {
                continue;
            }

            if (heardTokens.Count <= ShortPhraseWords)
            {
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
