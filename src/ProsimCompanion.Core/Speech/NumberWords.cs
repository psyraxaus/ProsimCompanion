using System.Globalization;

namespace ProsimCompanion.Core.Speech;

/// <summary>Whose radiotelephony rules the FO follows ON THE RADIO (owner decision 2026-11-14:
/// pilots from around the world expect their own; cockpit talk stays plain in every style).
/// The differences this app renders: a whole-hundred flight level ("flight level one hundred",
/// UK CAP 413), the frequency separator ("point", FAA AIM 4-2-8; "decimal" elsewhere) and the
/// flight number of the callsign in group form ("Speedbird eight fifty", FAA).</summary>
public enum RadioPhraseology
{
    /// <summary>ICAO Annex 10 Vol II / Doc 9432.</summary>
    Icao,

    /// <summary>FAA AIM chapter 4 section 2 / JO 7110.65.</summary>
    Faa,

    /// <summary>UK CAP 413.</summary>
    Uk,
}

/// <summary>
/// A number as WORDS, for the voice (owner report 2026-11-14: "eight thousand seven hundred
/// kilograms" came out "eighty seven, zero"). Until this class the app wrote digit words only
/// for the digit-by-digit quantities (QNH, heading, flight level …) and handed every other
/// figure to the TTS engine as Arabic digits, which each engine reads its own way. Nothing
/// that is spoken should leave the app as digits: quantities go through <see cref="Cardinal"/>
/// / <see cref="Number"/>, identifiers through <see cref="Phraseology.Digits"/>, and
/// <see cref="Phraseology.Normalize"/> is the net under both.
/// <para>
/// No "and" and no hyphens ("one hundred forty eight") — the form a crew reads a loadsheet in,
/// and the one <c>SpokenNumberText</c> reads back for the LLM number check.
/// </para>
/// </summary>
public static class NumberWords
{
    private static readonly string[] Small =
    [
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen",
    ];

    private static readonly string[] Tens =
        ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];

    /// <summary>8700 → "eight thousand seven hundred"; 148 → "one hundred forty eight";
    /// -180 → "minus one hundred eighty"; 0 → "zero".</summary>
    public static string Cardinal(long value)
    {
        if (value < 0)
        {
            return "minus " + Cardinal(value == long.MinValue ? long.MaxValue : -value);
        }

        if (value < 20)
        {
            return Small[value];
        }

        var parts = new List<string>(8);
        AppendScale(parts, ref value, 1_000_000_000, "billion");
        AppendScale(parts, ref value, 1_000_000, "million");
        AppendScale(parts, ref value, 1_000, "thousand");
        if (value > 0)
        {
            parts.Add(BelowThousand((int)value));
        }

        return string.Join(" ", parts);
    }

    private static void AppendScale(List<string> parts, ref long value, long scale, string word)
    {
        if (value >= scale)
        {
            parts.Add(Cardinal(value / scale) + " " + word);
            value %= scale;
        }
    }

    private static string BelowThousand(int value)
    {
        var parts = new List<string>(3);
        if (value >= 100)
        {
            parts.Add(Small[value / 100] + " hundred");
            value %= 100;
        }

        if (value >= 20)
        {
            parts.Add(Tens[value / 10]);
            value %= 10;
        }

        if (value > 0)
        {
            parts.Add(Small[value]);
        }

        return string.Join(" ", parts);
    }

    /// <summary>A written figure as a quantity: "6.8" → "six point eight", "2,500" → "two
    /// thousand five hundred", "-2" → "minus two", "62.30" → "sixty two point three zero".
    /// A whole number written with a leading zero ("090") or longer than six digits is an
    /// identifier, not a quantity, and reads digit by digit. Anything unreadable is returned
    /// as written.</summary>
    public static string Number(string written)
    {
        ArgumentNullException.ThrowIfNull(written);
        var text = written.Trim().Replace(",", "", StringComparison.Ordinal);
        var negative = text.StartsWith('-');
        if (negative)
        {
            text = text[1..];
        }

        var point = text.IndexOf('.', StringComparison.Ordinal);
        var whole = point >= 0 ? text[..point] : text;
        var fraction = point >= 0 ? text[(point + 1)..] : "";
        if (whole.Length == 0 || !whole.All(char.IsAsciiDigit) || !fraction.All(char.IsAsciiDigit))
        {
            return written;
        }

        string spoken;
        if (fraction.Length == 0 && ((whole.Length > 1 && whole[0] == '0') || whole.Length > 6))
        {
            spoken = Phraseology.Digits(whole);
        }
        else if (whole.Length > 15)
        {
            return written;
        }
        else
        {
            spoken = Cardinal(long.Parse(whole, CultureInfo.InvariantCulture));
            if (fraction.Length > 0)
            {
                spoken += " point " + string.Join(" ", fraction.Select(c => Small[c - '0']));
            }
        }

        return negative ? "minus " + spoken : spoken;
    }

    /// <summary>A value with a fixed number of decimals: (6.84, 1) → "six point eight".</summary>
    public static string Decimal(double value, int decimals)
        => Number(value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));

    /// <summary>A UTC clock time, four digits: (9, 0) → "zero niner zero zero".</summary>
    public static string Clock(int hour, int minute)
        => Phraseology.Digits(hour.ToString("00", CultureInfo.InvariantCulture) + minute.ToString("00", CultureInfo.InvariantCulture));

    /// <summary>An altitude the way it is transmitted — the same in ICAO Annex 10, the FAA AIM
    /// (4-2-9) and CAP 413: the digits of the thousands, THOUSAND, the hundreds digit, HUNDRED.
    /// 2500 → "two thousand five hundred", 10000 → "one zero thousand", 13500 → "one three
    /// thousand five hundred", 800 → "eight hundred". An altitude that is not a whole hundred
    /// reads digit by digit.</summary>
    public static string RadioAltitude(int feet)
    {
        if (feet <= 0)
        {
            return "zero";
        }

        if (feet % 100 != 0)
        {
            return Phraseology.Digits(feet.ToString(CultureInfo.InvariantCulture));
        }

        var thousands = feet / 1000;
        var hundreds = feet % 1000 / 100;
        var parts = new List<string>(2);
        if (thousands > 0)
        {
            parts.Add(Phraseology.Digits(thousands.ToString(CultureInfo.InvariantCulture)) + " thousand");
        }

        if (hundreds > 0)
        {
            parts.Add(NatoPhonetics.DigitWords[hundreds] + " hundred");
        }

        return string.Join(" ", parts);
    }

    /// <summary>A flight number in FAA group form (JO 7110.65 2-4-20: "American Fifty-Two",
    /// "Delta One Hundred", "Eastern Metro One Ten", "General Motors Thirty Fifteen", "United
    /// One Zero One", "TWA Ten Zero Four"): pairs read as numbers, a pair that starts with a
    /// zero digit by digit. Anything but one to four digits reads digit by digit.</summary>
    public static string GroupForm(string digits)
    {
        ArgumentNullException.ThrowIfNull(digits);
        if (digits.Length is 0 or > 4 || !digits.All(char.IsAsciiDigit) || digits[0] == '0')
        {
            return Phraseology.Digits(digits);
        }

        switch (digits.Length)
        {
            case 1:
            case 2:
                return Cardinal(int.Parse(digits, CultureInfo.InvariantCulture));
            case 3:
                if (digits.EndsWith("00", StringComparison.Ordinal))
                {
                    return Small[digits[0] - '0'] + " hundred";
                }

                return digits[1] == '0'
                    ? Phraseology.Digits(digits)
                    : Small[digits[0] - '0'] + " " + Cardinal(int.Parse(digits[1..], CultureInfo.InvariantCulture));
            default:
                if (digits.EndsWith("000", StringComparison.Ordinal))
                {
                    return Small[digits[0] - '0'] + " thousand";
                }

                return Pair(digits[..2]) + " " + Pair(digits[2..]);
        }
    }

    private static string Pair(string pair)
        => pair[0] == '0' ? Phraseology.Digits(pair) : Cardinal(int.Parse(pair, CultureInfo.InvariantCulture));
}
