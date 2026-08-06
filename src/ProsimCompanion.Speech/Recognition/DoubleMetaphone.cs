using System.Text;

namespace ProsimCompanion.Speech.Recognition;

/// <summary>
/// Double Metaphone phonetic encoder (Lawrence Philips). Produces a primary and an
/// alternate phonetic key (≤4 chars) so words that sound alike encode alike — used
/// by <see cref="CommandMatcher"/> so accent-driven near-misses still resolve to the
/// right command. Pure/static; deterministic.
/// </summary>
public static class DoubleMetaphone
{
    private const int MaxLength = 4;

    public static (string Primary, string Alternate) Encode(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return ("", "");

        var primary = new StringBuilder(8);
        var secondary = new StringBuilder(8);
        string w = input.Trim().ToUpperInvariant() + "     "; // pad to avoid bounds checks
        int length = input.Trim().Length;
        int last = length - 1;
        int current = 0;
        bool slavoGermanic =
            input.ToUpperInvariant().Contains('W') || input.ToUpperInvariant().Contains('K') ||
            input.Contains("CZ", StringComparison.OrdinalIgnoreCase) ||
            input.Contains("WITZ", StringComparison.OrdinalIgnoreCase);

        // Skip silent initial letters.
        if (At(w, 0, 2, "GN", "KN", "PN", "WR", "PS")) current = 1;

        // Initial 'X' = 'S' (e.g. "Xavier").
        if (w[0] == 'X') { Add(primary, secondary, "S"); current = 1; }

        while (current < length && (primary.Length < MaxLength || secondary.Length < MaxLength))
        {
            char c = w[current];
            switch (c)
            {
                case 'A': case 'E': case 'I': case 'O': case 'U': case 'Y':
                    if (current == 0) Add(primary, secondary, "A");
                    current++;
                    break;

                case 'B':
                    Add(primary, secondary, "P");
                    current += w[current + 1] == 'B' ? 2 : 1;
                    break;

                case 'Ç':
                    Add(primary, secondary, "S");
                    current++;
                    break;

                case 'C':
                    current = EncodeC(w, current, last, primary, secondary);
                    break;

                case 'D':
                    if (At(w, current, 2, "DG"))
                    {
                        if (At(w, current + 2, 1, "I", "E", "Y")) { Add(primary, secondary, "J"); current += 3; }
                        else { Add(primary, secondary, "TK"); current += 2; }
                    }
                    else if (At(w, current, 2, "DT", "DD")) { Add(primary, secondary, "T"); current += 2; }
                    else { Add(primary, secondary, "T"); current += 1; }
                    break;

                case 'F':
                    Add(primary, secondary, "F");
                    current += w[current + 1] == 'F' ? 2 : 1;
                    break;

                case 'G':
                    current = EncodeG(w, current, last, slavoGermanic, primary, secondary);
                    break;

                case 'H':
                    // Keep H only between vowels or at start before a vowel.
                    if ((current == 0 || IsVowel(w[current - 1])) && IsVowel(w[current + 1]))
                    { Add(primary, secondary, "H"); current += 2; }
                    else current += 1;
                    break;

                case 'J':
                    if (At(w, current, 4, "JOSE") || At(w, 0, 4, "SAN "))
                    {
                        if ((current == 0 && w[current + 4] == ' ') || At(w, 0, 4, "SAN ")) Add(primary, secondary, "H");
                        else { Add(primary, "J"); Add(secondary, "H"); }
                        current += 1;
                    }
                    else
                    {
                        if (current == 0) { Add(primary, "J"); Add(secondary, "A"); }
                        else if (IsVowel(w[current - 1]) && !slavoGermanic && (w[current + 1] == 'A' || w[current + 1] == 'O'))
                        { Add(primary, "J"); Add(secondary, "H"); }
                        else if (current == last) { Add(primary, "J"); Add(secondary, ""); }
                        else if (!At(w, current + 1, 1, "L", "T", "K", "S", "N", "M", "B", "Z") && !At(w, current - 1, 1, "S", "K", "L"))
                        { Add(primary, secondary, "J"); }

                        current += w[current + 1] == 'J' ? 2 : 1;
                    }
                    break;

                case 'K':
                    Add(primary, secondary, "K");
                    current += w[current + 1] == 'K' ? 2 : 1;
                    break;

                case 'L':
                    Add(primary, secondary, "L");
                    current += w[current + 1] == 'L' ? 2 : 1;
                    break;

                case 'M':
                    Add(primary, secondary, "M");
                    current += w[current + 1] == 'M' ? 2 : 1;
                    break;

                case 'N':
                    Add(primary, secondary, "N");
                    current += w[current + 1] == 'N' ? 2 : 1;
                    break;

                case 'Ñ':
                    Add(primary, secondary, "N");
                    current++;
                    break;

                case 'P':
                    if (w[current + 1] == 'H') { Add(primary, secondary, "F"); current += 2; }
                    else { Add(primary, secondary, "P"); current += At(w, current + 1, 1, "P", "B") ? 2 : 1; }
                    break;

                case 'Q':
                    Add(primary, secondary, "K");
                    current += w[current + 1] == 'Q' ? 2 : 1;
                    break;

                case 'R':
                    Add(primary, secondary, "R");
                    current += w[current + 1] == 'R' ? 2 : 1;
                    break;

                case 'S':
                    current = EncodeS(w, current, last, primary, secondary);
                    break;

                case 'T':
                    current = EncodeT(w, current, last, primary, secondary);
                    break;

                case 'V':
                    Add(primary, secondary, "F");
                    current += w[current + 1] == 'V' ? 2 : 1;
                    break;

                case 'W':
                    current = EncodeW(w, current, last, primary, secondary);
                    break;

                case 'X':
                    if (!(current == last && (At(w, current - 3, 3, "IAU", "EAU") || At(w, current - 2, 2, "AU", "OU"))))
                        Add(primary, secondary, "KS");
                    current += At(w, current + 1, 1, "C", "X") ? 2 : 1;
                    break;

                case 'Z':
                    if (w[current + 1] == 'H') { Add(primary, secondary, "J"); current += 2; }
                    else { Add(primary, secondary, "S"); current += w[current + 1] == 'Z' ? 2 : 1; }
                    break;

                default:
                    current++;
                    break;
            }
        }

        return (Trim(primary), Trim(secondary));
    }

    private static int EncodeC(string w, int current, int last, StringBuilder p, StringBuilder s)
    {
        if (current > 1 && !IsVowel(w[current - 2]) && At(w, current - 1, 3, "ACH") && w[current + 2] != 'I'
            && (w[current + 2] != 'E' || At(w, current - 2, 6, "BACHER", "MACHER")))
        { Add(p, s, "K"); return current + 2; }

        if (current == 0 && At(w, current, 6, "CAESAR")) { Add(p, s, "S"); return current + 2; }
        if (At(w, current, 4, "CHIA")) { Add(p, s, "K"); return current + 2; }

        if (At(w, current, 2, "CH"))
        {
            if (current > 0 && At(w, current, 4, "CHAE")) { Add(p, "K"); Add(s, "X"); return current + 2; }

            if (current == 0 && (At(w, current + 1, 5, "HARAC", "HARIS") || At(w, current + 1, 3, "HOR", "HYM", "HIA", "HEM"))
                && !At(w, 0, 5, "CHORE"))
            { Add(p, s, "K"); return current + 2; }

            if (At(w, 0, 4, "VAN ", "VON ") || At(w, 0, 3, "SCH")
                || At(w, current - 2, 6, "ORCHES", "ARCHIT", "ORCHID")
                || At(w, current + 2, 1, "T", "S")
                || ((At(w, current - 1, 1, "A", "O", "U", "E") || current == 0)
                    && At(w, current + 2, 1, "L", "R", "N", "M", "B", "H", "F", "V", "W", " ")))
            { Add(p, s, "K"); }
            else
            {
                if (current > 0)
                {
                    if (At(w, 0, 2, "MC")) Add(p, s, "K");
                    else { Add(p, "X"); Add(s, "K"); }
                }
                else Add(p, s, "X");
            }
            return current + 2;
        }

        if (At(w, current, 2, "CZ") && !At(w, current - 2, 4, "WICZ")) { Add(p, "S"); Add(s, "X"); return current + 2; }
        if (At(w, current + 1, 3, "CIA")) { Add(p, s, "X"); return current + 3; }

        if (At(w, current, 2, "CC") && !(current == 1 && w[0] == 'M'))
        {
            if (At(w, current + 2, 1, "I", "E", "H") && !At(w, current + 2, 2, "HU"))
            {
                if ((current == 1 && w[current - 1] == 'A') || At(w, current - 1, 5, "UCCEE", "UCCES")) Add(p, s, "KS");
                else Add(p, s, "X");
                return current + 3;
            }
            Add(p, s, "K"); return current + 2;
        }

        if (At(w, current, 2, "CK", "CG", "CQ")) { Add(p, s, "K"); return current + 2; }
        if (At(w, current, 2, "CI", "CE", "CY"))
        {
            if (At(w, current, 3, "CIO", "CIE", "CIA")) { Add(p, "S"); Add(s, "X"); }
            else Add(p, s, "S");
            return current + 2;
        }

        Add(p, s, "K");
        if (At(w, current + 1, 2, " C", " Q", " G")) return current + 3;
        return At(w, current + 1, 1, "C", "K", "Q") && !At(w, current + 1, 2, "CE", "CI") ? current + 2 : current + 1;
    }

    private static int EncodeG(string w, int current, int last, bool slavoGermanic, StringBuilder p, StringBuilder s)
    {
        if (w[current + 1] == 'H')
        {
            if (current > 0 && !IsVowel(w[current - 1])) { Add(p, s, "K"); return current + 2; }
            if (current == 0)
            {
                Add(p, s, w[current + 2] == 'I' ? "J" : "K");
                return current + 2;
            }
            if ((current > 1 && At(w, current - 2, 1, "B", "H", "D"))
                || (current > 2 && At(w, current - 3, 1, "B", "H", "D"))
                || (current > 3 && At(w, current - 4, 1, "B", "H")))
                return current + 2;

            if (current > 2 && w[current - 1] == 'U' && At(w, current - 3, 1, "C", "G", "L", "R", "T"))
                Add(p, s, "F");
            else if (current > 0 && w[current - 1] != 'I')
                Add(p, s, "K");
            return current + 2;
        }

        if (w[current + 1] == 'N')
        {
            if (current == 1 && IsVowel(w[0]) && !slavoGermanic) { Add(p, "KN"); Add(s, "N"); }
            else if (!At(w, current + 2, 2, "EY") && w[current + 1] != 'Y' && !slavoGermanic) { Add(p, "N"); Add(s, "KN"); }
            else Add(p, s, "KN");
            return current + 2;
        }

        if (At(w, current + 1, 2, "LI") && !slavoGermanic) { Add(p, "KL"); Add(s, "L"); return current + 2; }

        if (current == 0 && (w[current + 1] == 'Y' || At(w, current + 1, 2, "ES", "EP", "EB", "EL", "EY", "IB", "IL", "IN", "IE", "EI", "ER")))
        { Add(p, "K"); Add(s, "J"); return current + 2; }

        if ((At(w, current + 1, 2, "ER") || w[current + 1] == 'Y')
            && !At(w, 0, 6, "DANGER", "RANGER", "MANGER")
            && !At(w, current - 1, 1, "E", "I") && !At(w, current - 1, 3, "RGY", "OGY"))
        { Add(p, "K"); Add(s, "J"); return current + 2; }

        if (At(w, current + 1, 1, "E", "I", "Y") || At(w, current - 1, 4, "AGGI", "OGGI"))
        {
            if (At(w, 0, 4, "VAN ", "VON ") || At(w, 0, 3, "SCH") || At(w, current + 1, 2, "ET")) Add(p, s, "K");
            else if (At(w, current + 1, 4, "IER ")) Add(p, s, "J");
            else { Add(p, "J"); Add(s, "K"); }
            return current + 2;
        }

        Add(p, s, "K");
        return w[current + 1] == 'G' ? current + 2 : current + 1;
    }

    private static int EncodeS(string w, int current, int last, StringBuilder p, StringBuilder s)
    {
        if (At(w, current - 1, 3, "ISL", "YSL")) return current + 1;
        if (current == 0 && At(w, current, 5, "SUGAR")) { Add(p, "X"); Add(s, "S"); return current + 1; }

        if (At(w, current, 2, "SH"))
        {
            if (At(w, current + 1, 4, "HEIM", "HOEK", "HOLM", "HOLZ")) Add(p, s, "S");
            else Add(p, s, "X");
            return current + 2;
        }

        if (At(w, current, 3, "SIO", "SIA") || At(w, current, 4, "SIAN"))
        {
            if (!slavo(w)) { Add(p, "S"); Add(s, "X"); }
            else Add(p, s, "S");
            return current + 3;
        }

        if ((current == 0 && At(w, current + 1, 1, "M", "N", "L", "W")) || At(w, current + 1, 1, "Z"))
        {
            Add(p, "S"); Add(s, "X");
            return At(w, current + 1, 1, "Z") ? current + 2 : current + 1;
        }

        if (At(w, current, 2, "SC"))
        {
            if (w[current + 2] == 'H')
            {
                if (At(w, current + 3, 2, "OO", "ER", "EN", "UY", "ED", "EM")) { Add(p, "X"); Add(s, "SK"); }
                else if (At(w, current + 3, 1, "I", "E", "Y")) Add(p, s, "S");
                else { Add(p, "SK"); Add(s, "SK"); }
            }
            else if (At(w, current + 2, 1, "I", "E", "Y")) Add(p, s, "S");
            else Add(p, s, "SK");
            return current + 3;
        }

        if (current == last && At(w, current - 2, 2, "AI", "OI")) { Add(p, ""); Add(s, "S"); }
        else Add(p, s, "S");
        return At(w, current + 1, 1, "S", "Z") ? current + 2 : current + 1;
    }

    private static int EncodeT(string w, int current, int last, StringBuilder p, StringBuilder s)
    {
        if (At(w, current, 4, "TION")) { Add(p, s, "X"); return current + 3; }
        if (At(w, current, 3, "TIA", "TCH")) { Add(p, s, "X"); return current + 3; }

        if (At(w, current, 2, "TH") || At(w, current, 3, "TTH"))
        {
            if (At(w, current + 2, 2, "OM", "AM") || At(w, 0, 4, "VAN ", "VON ") || At(w, 0, 3, "SCH"))
                Add(p, s, "T");
            else { Add(p, "0"); Add(s, "T"); }
            return current + 2;
        }

        Add(p, s, "T");
        return At(w, current + 1, 1, "T", "D") ? current + 2 : current + 1;
    }

    private static int EncodeW(string w, int current, int last, StringBuilder p, StringBuilder s)
    {
        if (At(w, current, 2, "WR")) { Add(p, s, "R"); return current + 2; }

        if (current == 0 && (IsVowel(w[current + 1]) || At(w, current, 2, "WH")))
        {
            if (IsVowel(w[current + 1])) { Add(p, "A"); Add(s, "F"); }
            else Add(p, s, "A");
        }

        if ((current == last && IsVowel(w[current - 1]))
            || At(w, current - 1, 5, "EWSKI", "EWSKY", "OWSKI", "OWSKY")
            || At(w, 0, 3, "SCH"))
        { Add(p, ""); Add(s, "F"); return current + 1; }

        if (At(w, current, 4, "WICZ", "WITZ")) { Add(p, "TS"); Add(s, "FX"); return current + 4; }

        return current + 1;
    }

    private static bool slavo(string w) =>
        w.Contains('W') || w.Contains('K') || w.Contains("CZ") || w.Contains("WITZ");

    private static bool IsVowel(char c) => c is 'A' or 'E' or 'I' or 'O' or 'U' or 'Y';

    /// <summary>True if the substring of <paramref name="w"/> at <paramref name="start"/> (length n) matches any candidate.</summary>
    private static bool At(string w, int start, int length, params string[] candidates)
    {
        if (start < 0 || start + length > w.Length) return false;
        var seg = w.Substring(start, length);
        foreach (var c in candidates)
            if (seg == c) return true;
        return false;
    }

    private static void Add(StringBuilder primary, StringBuilder secondary, string value)
    {
        primary.Append(value);
        secondary.Append(value);
    }

    private static void Add(StringBuilder sb, string value) => sb.Append(value);

    private static string Trim(StringBuilder sb) =>
        sb.Length > MaxLength ? sb.ToString(0, MaxLength) : sb.ToString();
}
