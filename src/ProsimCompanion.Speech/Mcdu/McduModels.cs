using ProsimCompanion.Speech.Callouts;

namespace ProsimCompanion.Speech.Mcdu;

/// <summary>One MCDU row = the small label line (top) and the data/LSK line (bottom), cleaned
/// of the display's colour/font codes. <see cref="Row"/> is 1..6, matching LSK1..LSK6.</summary>
public sealed record McduRow(int Row, string Label, string Data)
{
    public bool IsEmpty => Label.Length == 0 && Data.Length == 0;
}

/// <summary>
/// A parsed CDU2 page (from <c>aircraft.mcdu2.display</c>): title, six label/data rows, the
/// scratchpad, plus flags for a staged temporary flight plan and scrollability. Read-only
/// view — see <see cref="McduDisplayParser"/>. <see cref="Raw"/> keeps the raw XML so the
/// actuator's settle loop can compare consecutive reads exactly.
/// </summary>
public sealed record McduPage(
    string Title,
    IReadOnlyList<McduRow> Rows,
    string Scratchpad,
    bool IsTemporary,
    bool CanScrollUp,
    bool CanScrollDown,
    string Raw)
{
    public static McduPage Empty { get; } = new("", [], "", false, false, false, "");

    public bool HasData => Title.Length > 0 || Rows.Any(r => !r.IsEmpty) || Scratchpad.Length > 0;
}

/// <summary>Deterministic spoken forms shared by the MCDU voice features. Numbers and
/// identifiers are never persona-styled — a read-back must be verbatim-checkable.</summary>
public static class McduSpeech
{
    /// <summary>"04L" → "runway zero four left"; null/blank → "the arrival runway".</summary>
    public static string SpeakRunway(string? runway)
    {
        if (string.IsNullOrWhiteSpace(runway))
        {
            return "the arrival runway";
        }

        var s = runway.Trim().ToUpperInvariant();
        var digits = new string(s.TakeWhile(char.IsAsciiDigit).ToArray());
        if (digits.Length == 0)
        {
            return "the arrival runway";
        }

        var side = s.SkipWhile(char.IsAsciiDigit).FirstOrDefault() switch
        {
            'L' => " left",
            'R' => " right",
            'C' => " center",
            _ => "",
        };
        return $"runway {Aviation.ToDigits(digits)}{side}";
    }

    /// <summary>"110.30" → "one one zero decimal three zero" (ICAO digits, 9 = "niner").</summary>
    public static string SpeakFrequency(string frequency) => Aviation.ToDigits(frequency);

    /// <summary>Letters/digits spelled out ("KODAP" → "K O D A P") for idents and STAR names.</summary>
    public static string SpeakLetters(string s)
        => string.Join(" ", s.ToUpperInvariant().Where(char.IsLetterOrDigit).Select(c => c.ToString()));

    /// <summary>"16R"/"4L"/"RW16R" → "16R"/"04L"; null when no leading digits. More than two
    /// leading digits truncates to two; a side letter other than L/R/C is dropped.</summary>
    public static string? NormalizeRunway(string? runway)
    {
        if (string.IsNullOrWhiteSpace(runway))
        {
            return null;
        }

        var s = runway.Trim().ToUpperInvariant();
        if (s.StartsWith("RW", StringComparison.Ordinal))
        {
            s = s[2..];
        }

        var digits = new string(s.TakeWhile(char.IsAsciiDigit).ToArray());
        if (digits.Length == 0)
        {
            return null;
        }

        digits = digits.Length switch
        {
            1 => "0" + digits,
            > 2 => digits[..2],
            _ => digits,
        };
        var side = s.SkipWhile(char.IsAsciiDigit).FirstOrDefault();
        return digits + (side is 'L' or 'R' or 'C' ? side.ToString() : "");
    }
}
