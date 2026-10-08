using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Speech.Llm;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Cabin;

/// <summary>What the captain's reply to the purser's cruise query amounted to.</summary>
public enum CruiseReplyKind
{
    /// <summary>The window closed with nothing heard.</summary>
    Silence,

    /// <summary>A time or arrival estimate ("about forty minutes", "on time", "ten late").</summary>
    Eta,

    /// <summary>A word about the ride ("smooth", "light chop", "expect some bumps").</summary>
    Ride,

    /// <summary>Both a time and the ride in one breath.</summary>
    Both,

    /// <summary>Something else — acknowledged generically.</summary>
    Generic,
}

/// <summary>
/// The pure half of the purser's cruise query: the reply classification and the line the
/// purser answers with. Keyword-based on purpose — the reply is a free sentence from the LAN
/// transcriber, and a word list is what a purser listening for "when" and "how bumpy" does
/// too. Nothing here is parsed into the aircraft: the kind only picks an acknowledgement.
/// </summary>
public static class CabinCruiseQueryCore
{
    /// <summary>Words that make a reply about the ride.</summary>
    private static readonly string[] RideWords =
    [
        "smooth", "bumpy", "bump", "bumps", "chop", "choppy", "turbulence", "turbulent", "rough", "ride",
        "shaky", "rocky", "seatbelt", "seat belt", "weather ahead", "calm", "no weather", "clear air",
    ];

    /// <summary>Words that make a reply about the arrival time.</summary>
    private static readonly string[] EtaWords =
    [
        "on time", "on schedule", "late", "early", "delay", "delayed", "minute", "minutes", "hour", "hours",
        "eta", "arrival", "arriving", "arrive", "landing", "land", "touchdown", "half an hour", "quarter",
        "zulu", "local", "o clock", "oclock", "ahead of schedule", "behind schedule",
    ];

    /// <summary>The closed grammar for the offline Windows engine, which cannot hear free
    /// text: the replies a captain is likeliest to give, so the window still works without
    /// the LAN transcriber (then "about forty minutes" is not an option, "on time" is).</summary>
    public static readonly IReadOnlyList<string> OfflineGrammar =
    [
        "on time", "on schedule", "running late", "ten minutes late", "twenty minutes late", "ten minutes early",
        "about thirty minutes", "about forty minutes", "about an hour", "half an hour",
        "smooth", "smooth ride", "light chop", "expect turbulence", "expect some bumps", "bumpy ahead",
        "nothing to report", "no change", "no update", "stand by", "thanks",
    ];

    /// <summary>Classifies the reply. Null or blank is <see cref="CruiseReplyKind.Silence"/>.
    /// A figure on its own ("forty minutes" without the word — or just "forty") reads as a
    /// time: a purser who asked about the ETA hears a number as minutes.</summary>
    public static CruiseReplyKind Classify(string? heard)
    {
        if (string.IsNullOrWhiteSpace(heard))
        {
            return CruiseReplyKind.Silence;
        }

        // Whole words only: "through" holds "rough", "pride" holds "ride".
        var text = " " + CommandMatcher.Normalize(heard) + " ";
        var ride = RideWords.Any(w => text.Contains(" " + w + " ", StringComparison.Ordinal));
        var eta = EtaWords.Any(w => text.Contains(" " + w + " ", StringComparison.Ordinal))
            || SpokenNumberText.ToDigits(heard).Any(char.IsAsciiDigit);

        return (eta, ride) switch
        {
            (true, true) => CruiseReplyKind.Both,
            (true, false) => CruiseReplyKind.Eta,
            (false, true) => CruiseReplyKind.Ride,
            _ => CruiseReplyKind.Generic,
        };
    }

    /// <summary>The purser's line for a reply kind, from the wording options.</summary>
    public static string Acknowledgement(CruiseReplyKind kind, CabinOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return kind switch
        {
            CruiseReplyKind.Silence => options.CruiseQueryNoReplyText,
            CruiseReplyKind.Eta => options.CruiseQueryEtaAckText,
            CruiseReplyKind.Ride => options.CruiseQueryRideAckText,
            CruiseReplyKind.Both => options.CruiseQueryEtaAckText,
            _ => options.CruiseQueryGenericAckText,
        };
    }

    /// <summary>The session-event spelling of a kind.</summary>
    public static string Name(CruiseReplyKind kind) => kind switch
    {
        CruiseReplyKind.Silence => "silence",
        CruiseReplyKind.Eta => "eta",
        CruiseReplyKind.Ride => "ride",
        CruiseReplyKind.Both => "eta+ride",
        _ => "generic",
    };
}
