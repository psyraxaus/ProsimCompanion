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

        // ---- 2026-11-14 (owner report: fuel read "eighty seven, zero"): the figures that are
        // said digit by digit although no rule above names them. Everything these leave is a
        // quantity and goes to the net below as plain words.
        // "altimeter 29.92" / "altimeter 2992" → "altimeter two niner niner two".
        (Rx(@"\baltimeter\s+(\d{2})\.?(\d{2})\b"), m => "altimeter " + Digits(m.Groups[1].Value + m.Groups[2].Value)),
        // A speed after its name: "V1 140", "V 1 142", "rotate 145", "VAPP 134", "flex 55",
        // "speed 230", "gusting 41".
        (Rx(@"\b(V\s?[12R]|VAPP|VREF|VLS|VFE|rotate|flex|speed|gusting)(\s*,?\s+)(\d{2,3})\b"),
            m => m.Groups[1].Value + m.Groups[2].Value + Digits(m.Groups[3].Value)),
        // A speed limit or a cross-checked figure: "flaps two limit is 215", "limit of 33",
        // "V one: FMS 140, performance 142".
        (Rx(@"\b(limit\s+(?:is|of)|FMS|performance)\s+(\d{2,3})\b"), m => m.Groups[1].Value + " " + Digits(m.Groups[2].Value)),
        // A direction: "163 degrees", "Wind at destination 270 at 8 …" (before the knots
        // rule, which takes the digit this one looks ahead for).
        (Rx(@"\b(\d{3})(?=\s+degrees\b|\s+at\s+\d)"), m => Digits(m.Groups[1].Value)),
        // A speed before its unit: "250 knots", "12 kts", "a 8 knot tailwind".
        (Rx(@"\b(\d{1,3})(\s*-?\s*)(knots?|kts?)\b"),
            m => Digits(m.Groups[1].Value) + m.Groups[2].Value + m.Groups[3].Value),
        // A clock time: "09:00 zulu" → "zero niner zero zero zulu"; "14:05Z" likewise.
        (Rx(@"(?<![\d:])([01]?\d|2[0-3]):([0-5]\d)(?![\d:])(?:\s?Z\b)?"),
            m => Digits(m.Groups[1].Value.PadLeft(2, '0') + m.Groups[2].Value) + (m.Value.EndsWith('Z') || m.Value.EndsWith('z') ? " zulu" : "")),
        // A three-digit stand is digits ("stand five four six", owner 2026-10-29); one or two
        // digits stay a number for the net ("gate twenty three").
        (Rx(@"\b(stand|gate|bay)\s+((?:[A-Z][a-z]+\s+)?)(\d{3})\b"),
            m => m.Groups[1].Value + " " + m.Groups[2].Value + Digits(m.Groups[3].Value)),
        // "door 1L" → "door one left".
        (Rx(@"\b(doors?)\s+(\d)([LR])\b"),
            m => m.Groups[1].Value + " " + Digits(m.Groups[2].Value) + Side(m.Groups[3].Value)),
        // "seat 24C", "row 12" + letter: a number and a NATO letter.
        (Rx(@"\b(seat|row)\s+(\d{1,2})([A-K])\b"),
            m => m.Groups[1].Value + " " + NumberWords.Cardinal(int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)) + " " + NatoPhonetics.Word(m.Groups[3].Value[0])),
    ];

    // A bare runway designator ("ILS 27R", "vacated 09L") — case-sensitive, 01 to 36 only.
    [GeneratedRegex(@"\b(0[1-9]|[12]\d|3[0-6])([LRC])\b", RegexOptions.CultureInvariant)]
    private static partial Regex BareRunway();

    // Any other number glued to one capital letter ("36A", "546R"): number, then NATO word.
    [GeneratedRegex(@"(?<![A-Za-z\d.,])(\d{1,3})([A-Z])\b", RegexOptions.CultureInvariant)]
    private static partial Regex NumberWithLetter();

    // ------------------------------------------------------------------ pass 3: the net
    // Every figure still written in digits is a quantity: "8700 kilograms", "6.8 tonnes",
    // "2,500", "-2". Digits glued to a letter ("A320", "N1", "BA124", "5th") are names and
    // stay. See NumberWords.Number for the leading-zero and long-number exceptions.
    [GeneratedRegex(@"(?<![A-Za-z\d])(?<neg>(?<=^|\s)-)?(?<num>\d{1,3}(?:,\d{3})+|\d+)(?:\.(?<frac>\d+))?(?![A-Za-z\d])", RegexOptions.CultureInvariant)]
    private static partial Regex LeftoverNumber();

    private static string SpeakLeftover(Match m)
    {
        var written = m.Groups["num"].Value + (m.Groups["frac"].Success ? "." + m.Groups["frac"].Value : "");
        var spoken = NumberWords.Number(written);
        return m.Groups["neg"].Success ? "minus " + spoken : spoken;
    }

    // ------------------------------------------------------------------ pass 2: abbreviations

    // The letter-spelled and word-spoken abbreviations ("I L S", "ee-cam") live in the
    // pronunciation table since 2026-11-14 (owner request: "ECAM, not E C A M"); the rules
    // below are the expansions and respellings that are not a plain abbreviation.
    private static volatile PronunciationTable _pronunciations = PronunciationTable.Defaults;

    /// <summary>The table in use — <see cref="PronunciationTable.Defaults"/> until the
    /// composition root hands over the pilot's file at startup.</summary>
    public static PronunciationTable Pronunciations => _pronunciations;

    /// <summary>Replaces the table (startup, after the user config is seeded and before any
    /// line is spoken or prewarmed — the TTS cache text depends on it).</summary>
    public static void UsePronunciations(PronunciationTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        _pronunciations = table;
    }

    private static readonly (Regex Rx, string Replacement)[] Abbreviations =
    [
        (Rx(@"\bLOC\b"), "localizer"),
        (Rx(@"\bMSL\b"), "mean sea level"),
        (Rx(@"\bAGL\b"), "above ground level"),
        // V-speeds (owner report 2026-10-31, LKPR→EGLL): the taxi checklist's "V1, V R, V2"
        // came out as "vone" / "vee-two-ish" — kokoro reads a letter glued to a digit as one
        // syllable. Spelled the way pilots say them; "Vee R" keeps the R a letter. The
        // optional space covers the "V 1" form the confirm callout uses.
        (Rx(@"\bV\s?1\b"), "Vee one"),
        (Rx(@"\bV\s?2\b"), "Vee two"),
        (Rx(@"\bV\s?R\b"), "Vee R"),
        (Rx(@"\bVAPP\b"), "Vee app"),
        (Rx(@"\bVREF\b"), "Vee ref"),
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

        s = BareRunway().Replace(s, m => Digits(m.Groups[1].Value) + Side(m.Groups[2].Value));
        s = NumberWithLetter().Replace(s, m =>
            (m.Groups[1].Value.Length == 3 ? Digits(m.Groups[1].Value) : NumberWords.Number(m.Groups[1].Value))
            + " " + NatoPhonetics.Word(m.Groups[2].Value[0]));

        // Abbreviations by the pronunciation table (2026-11-14): "ECAM" → "ee-cam", "SID" →
        // "sid", "APU" → "A P U" — the pilot's file over the shipped defaults.
        s = _pronunciations.Apply(s);

        foreach (var (rx, replacement) in Abbreviations)
        {
            s = rx.Replace(s, replacement);
        }

        s = LeftoverNumber().Replace(s, SpeakLeftover);

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
