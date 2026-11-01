using System.Text.RegularExpressions;

namespace ProsimCompanion.Core.Speech;

/// <summary>
/// The phraseology pass (issue #200): the one rule table every line passes through on its way
/// to the TTS engine, so a designator is said the way a crew says it wherever the line was
/// composed. Three passes in a fixed order — letters in context, then numbers, then
/// abbreviations — each a whole-word regex, each idempotent, so a line that already reads
/// "taxiway Golf" or "flight level three five zero" passes through unchanged (the TTS cache
/// text must not drift).
///
/// Letters: a bare designator letter is swallowed or read as a word fragment by the voices
/// (issue #68: "information M" came out "em", the "V" of VOLA3V vanished), so a letter the
/// context marks as a designator becomes its NATO word — after "taxiway", "holding point",
/// a routing word ("via", "hold short of", …), "stand"/"gate", "information" (ATIS) and an
/// approach type ("ILS Y"); a letter glued to digits ("A4", "G5") anywhere. A letter with no
/// such context is left alone: "a runway closed" keeps its article, "V1"/"N2" stay for the
/// V-speed and engine rules. Identifiers that only their source can recognise (a SID, a
/// fix, an ICAO code) are still rendered at the source through <see cref="NatoPhonetics"/>
/// and <see cref="ISpokenText"/>; this pass is the net under them.
///
/// History: the number and abbreviation rules are the former Speech-project
/// <c>AviationSpeech</c> (Prosim2FO lineage); the taxiway rules were the radios composer's
/// own (LKPR/EGLL 2026-10-31), moved here when the departure briefing was heard listing the
/// closed taxiway NOTAMs by their letters (owner report 2026-11-01).
/// </summary>
public static partial class Phraseology
{
    // ------------------------------------------------------------------ pass 0: letters

    // A designator token: a capital letter (two for the explicit taxiway/holding-point
    // context — "NA", "AB" exist at the big fields) with up to two digits. The lookahead keeps
    // "V1", "V 2", "V R" (the V-speeds) as they are, so "rotate at V R" is never "at Victor R";
    // outside an explicit taxiway context "N1"/"N2" are the engine parameters, inside it
    // "holding point N1" is a real name.
    private const string VGuard = @"(?!V\s?[12R]\b)";
    private const string Guard = @"(?!V\s?[12R]\b|N[12]\b)";

    // A letter followed by another spaced capital is a letter-spelled abbreviation the
    // abbreviation pass made ("via I L S", "at Q N H", "on E CAM") — never a taxiway, which
    // also keeps the pass idempotent.
    private const string Spaced = @"(?!\s[A-Z]\b|\sCAM\b)";

    // "taxiway G and G5", "taxiways A, B and C", "holding point N1": the context names the
    // thing, so two-letter designators are allowed.
    [GeneratedRegex(@"(?<lead>\b(?i:taxiways?|holding point)\s+)" + @"(?<list>\b" + VGuard + @"[A-Z]{1,2}\d{0,2}\b(?:\s*(?:,|/)\s*\b" + VGuard + @"[A-Z]{1,2}\d{0,2}\b|\s+(?i:and|then)\s+\b" + VGuard + @"[A-Z]{1,2}\d{0,2}\b)*)", RegexOptions.CultureInvariant)]
    private static partial Regex NamedTaxiways();

    // Routing words (the radios composer's 2026-10-31 list): after one of these every single
    // letter token up to the end of the list is a taxiway. One letter only — "on ND" and
    // "at FL" are prose, not taxiways.
    [GeneratedRegex(@"(?<lead>\b(?i:via|hold short of|join|onto|exit at|exit|cross|on|at)\s+)" + @"(?<list>\b" + Guard + @"[A-Z]\d{0,2}\b" + Spaced + @"(?:\s*(?:,|/)\s*\b" + Guard + @"[A-Z]\d{0,2}\b" + Spaced + @"|\s+(?i:and|then)\s+\b" + Guard + @"[A-Z]\d{0,2}\b" + Spaced + @")*)", RegexOptions.CultureInvariant)]
    private static partial Regex RoutedTaxiways();

    // One token inside a matched list.
    [GeneratedRegex(@"\b[A-Z]{1,2}\d{0,2}\b", RegexOptions.CultureInvariant)]
    private static partial Regex ListToken();

    // A letter glued to one or two digits anywhere ("A4", "B2") is a designator; three digits
    // ("A320") is a type and stays.
    [GeneratedRegex(@"\b" + Guard + @"([A-Z])(\d{1,2})\b", RegexOptions.CultureInvariant)]
    private static partial Regex GluedDesignator();

    // "stand 546R" / "gate 23 A" → the trailing letter; "gate B12" / "stand B 16" → the leading
    // letter. The number itself is left to the source (the radios composer spells it for the
    // radio; the FO says "gate twenty-three").
    [GeneratedRegex(@"\b(?<word>(?i:stand|gate|bay))\s+(?<number>\d{1,3})\s?(?<letter>[A-Z])\b", RegexOptions.CultureInvariant)]
    private static partial Regex StandTrailingLetter();

    [GeneratedRegex(@"\b(?<word>(?i:stand|gate|bay))\s+(?<letter>[A-Z])\s?(?<number>\d{1,3})\b", RegexOptions.CultureInvariant)]
    private static partial Regex StandLeadingLetter();

    // ATIS "information M"; an approach variant "ILS Y", "RNP Z".
    [GeneratedRegex(@"\b(?<word>(?i:information))\s+(?<letter>[A-Z])\b", RegexOptions.CultureInvariant)]
    private static partial Regex AtisLetter();

    [GeneratedRegex(@"\b(?<word>ILS|LOC|RNP|RNAV|VOR|NDB|GLS|LDA)\s+(?<letter>[XYZ])\b", RegexOptions.CultureInvariant)]
    private static partial Regex ApproachVariant();

    // NOTAM shorthand the briefing lifts verbatim.
    [GeneratedRegex(@"\bRWY\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RwyAbbreviation();

    [GeneratedRegex(@"\bTWYS?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TwyAbbreviation();

    // ------------------------------------------------------------------ pass 1: numbers

    private static Regex Rx(string pattern)
        => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string Side(string s) => s.ToUpperInvariant() switch
    {
        "L" => " left",
        "R" => " right",
        "C" => " center",
        _ => "",
    };

    // Order matters: FL### runs first and consumes "FL350"; the bare-FL abbreviation below
    // only catches leftovers. Leading zeros are stripped for FL/QNH/QFE; headings are
    // zero-padded to three digits instead.
    private static readonly (Regex Rx, MatchEvaluator Evaluate)[] NumberRules =
    [
        (Rx(@"\bFL\s*0*(\d{2,3})\b"), m => "flight level " + Digits(m.Groups[1].Value)),
        (Rx(@"\b(transition level|flight level)\s+0*(\d{2,3})\b"),
            m => m.Groups[1].Value + " " + Digits(m.Groups[2].Value)),
        (Rx(@"\bheading\s+(\d{1,3})\b"), m => "heading " + Digits(m.Groups[1].Value.PadLeft(3, '0'))),
        (Rx(@"\b(squawk|transponder)\s+(\d{3,4})\b"), m => m.Groups[1].Value + " " + Digits(m.Groups[2].Value)),
        // Singular and plural: LLM briefing prose says "winds 270" as often as "wind 270"
        // (issue #69). The matched word is preserved so the plural respelling below applies.
        (Rx(@"\b(winds?)\s+(\d{3})\b"), m => m.Groups[1].Value + " " + Digits(m.Groups[2].Value)),
        (Rx(@"\bQNH\s+0*(\d{3,4})\b"), m => "Q N H " + Digits(m.Groups[1].Value)),
        (Rx(@"\bQFE\s+0*(\d{3,4})\b"), m => "Q F E " + Digits(m.Groups[1].Value)),
        (Rx(@"\b(\d{3}\.\d{1,3})\b"), m => Digits(m.Groups[1].Value)), // frequencies (e.g. 110.30)
        (Rx(@"\brunway\s+(\d{1,2})\s*([LRC])?\b"),
            m => "runway " + Digits(m.Groups[1].Value) + Side(m.Groups[2].Value)),
    ];

    // ------------------------------------------------------------------ pass 2: abbreviations

    private static readonly (Regex Rx, string Replacement)[] Abbreviations =
    [
        (Rx(@"\bILS\b"), "I L S"),
        (Rx(@"\bRVR\b"), "R V R"),
        (Rx(@"\bQNH\b"), "Q N H"),
        (Rx(@"\bQFE\b"), "Q F E"),
        (Rx(@"\bVOR\b"), "V O R"),
        (Rx(@"\bDME\b"), "D M E"),
        (Rx(@"\bNDB\b"), "N D B"),
        (Rx(@"\bADF\b"), "A D F"),
        (Rx(@"\bLOC\b"), "localizer"),
        (Rx(@"\bATIS\b"), "A T I S"),
        (Rx(@"\bMSA\b"), "M S A"),
        (Rx(@"\bMSL\b"), "mean sea level"),
        (Rx(@"\bAGL\b"), "above ground level"),
        (Rx(@"\bMDA\b"), "M D A"),
        (Rx(@"\bECAM\b"), "E CAM"),
        (Rx(@"\bMCDU\b"), "M C D U"),
        // V-speeds (owner report 2026-10-31, LKPR→EGLL): the taxi checklist's "V1, V R, V2"
        // came out as "vone" / "vee-two-ish" — kokoro reads a letter glued to a digit as one
        // syllable. Spelled the way pilots say them; "Vee R" keeps the R a letter. The
        // optional space covers the "V 1" form the confirm callout uses.
        (Rx(@"\bV\s?1\b"), "Vee one"),
        (Rx(@"\bV\s?2\b"), "Vee two"),
        (Rx(@"\bV\s?R\b"), "Vee R"),
        (Rx(@"\bVAPP\b"), "Vee app"),
        (Rx(@"\bVREF\b"), "Vee ref"),
        (Rx(@"\bVLS\b"), "V L S"),
        (Rx(@"\bVFE\b"), "V F E"),
        (Rx(@"\bVMO\b"), "V M O"),
        (Rx(@"\bMMO\b"), "M M O"),
        // kokoro reads "winds" as the verb /waɪndz/ (issue #69). "windz" is the most
        // phonetically explicit respelling that still reads as one word — "wind z" risks the
        // synthesizer spelling out a letter zed. Chosen without an audition; may need an
        // ear-check on the sim PC.
        (Rx(@"\bwinds\b"), "windz"),
        (Rx(@"\bFL\b"), "flight level"), // bare FL not followed by a number
        (Rx(@"\bft\b"), "feet"),
        (Rx(@"\bkts?\b"), "knots"),
        (Rx(@"\bnm\b"), "nautical miles"),
        (Rx(@"\bfpm\b"), "feet per minute"),
        (Rx(@"\bhPa\b"), "hectopascals"),
    ];

    /// <summary>The TTS-ready form of one line: letters in context, numbers, abbreviations,
    /// whitespace collapsed. Blank input is returned as given.</summary>
    public static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var s = Letters(text, " ");
        foreach (var (rx, evaluate) in NumberRules)
        {
            s = rx.Replace(s, evaluate);
        }

        foreach (var (rx, replacement) in Abbreviations)
        {
            s = rx.Replace(s, replacement);
        }

        return Regex.Replace(s, @"[ \t]{2,}", " ").Trim();
    }

    /// <summary>Pass 0 on its own, with the word joiner of the caller's house style: " " for
    /// the FO voice, "-" for a SayIntentions read-back ("Alpha-one", SI's hyphenated callsign
    /// style). The radios composer uses this so its transmitted text and the spoken text come
    /// from the same table.</summary>
    public static string Letters(string text, string join)
    {
        ArgumentNullException.ThrowIfNull(join);
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var s = RwyAbbreviation().Replace(text, "runway");
        s = TwyAbbreviation().Replace(s, m => m.Value.EndsWith('S') || m.Value.EndsWith('s') ? "taxiways" : "taxiway");
        s = NamedTaxiways().Replace(s, m => m.Groups["lead"].Value + SpellList(m.Groups["list"].Value, join));
        s = RoutedTaxiways().Replace(s, m => m.Groups["lead"].Value + SpellList(m.Groups["list"].Value, join));
        s = StandTrailingLetter().Replace(s, m => m.Groups["word"].Value + " " + m.Groups["number"].Value + " " + NatoPhonetics.Word(m.Groups["letter"].Value[0]));
        s = StandLeadingLetter().Replace(s, m => m.Groups["word"].Value + " " + NatoPhonetics.Word(m.Groups["letter"].Value[0]) + " " + m.Groups["number"].Value);
        s = AtisLetter().Replace(s, m => m.Groups["word"].Value + " " + NatoPhonetics.Word(m.Groups["letter"].Value[0]));
        s = ApproachVariant().Replace(s, m => m.Groups["word"].Value + " " + NatoPhonetics.Word(m.Groups["letter"].Value[0]));
        s = GluedDesignator().Replace(s, m => Spell(m.Value, join));
        return s;
    }

    /// <summary>A compact designator spelled out: "A" → "Alpha", "G5" → "Golf five" (or
    /// "Golf-five" with "-"), "546R" → "five four six Romeo", "B16" → "Bravo one six". Every
    /// letter is a NATO word and every digit an ICAO digit word; anything else is dropped.</summary>
    public static string Spell(string designator, string join)
    {
        ArgumentNullException.ThrowIfNull(designator);
        ArgumentNullException.ThrowIfNull(join);
        var words = new List<string>(designator.Length);
        foreach (var c in designator)
        {
            if (c is >= '0' and <= '9')
            {
                words.Add(NatoPhonetics.DigitWords[c - '0']);
            }
            else if (NatoPhonetics.Word(c) is { } word)
            {
                words.Add(word);
            }
        }

        return string.Join(join, words);
    }

    /// <summary>"1013" → "one zero one three"; "29.92" → "two niner decimal niner two".
    /// Non-digit characters other than '.' are silently dropped.</summary>
    public static string Digits(string s)
    {
        ArgumentNullException.ThrowIfNull(s);

        var parts = new List<string>(s.Length);
        foreach (var c in s)
        {
            if (c is >= '0' and <= '9')
            {
                parts.Add(NatoPhonetics.DigitWords[c - '0']);
            }
            else if (c == '.')
            {
                parts.Add("decimal");
            }
        }

        return string.Join(" ", parts);
    }

    private static string SpellList(string list, string join)
        => ListToken().Replace(list, m => Spell(m.Value, join));
}
