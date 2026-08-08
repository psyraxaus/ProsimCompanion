using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ProsimCompanion.Speech.Mcdu;

/// <summary>
/// Parses the ProSim CDU2 display XML
/// (<c>&lt;root&gt;&lt;title/&gt;&lt;line/&gt;×12&lt;scratchpad/&gt;&lt;/root&gt;</c>) into a
/// structured <see cref="McduPage"/>. Empirically discovered encoding (Prosim2FO, 2025): the
/// display encodes colour/font as single LOWERCASE letters (s=small, g=green, w=white,
/// c=cyan, a=amber, m=magenta, l=large, y=yellow/temporary) and LSK arrows as
/// <c>&lt;</c>/<c>&gt;</c>; real MCDU text is upper-case, so stripping lowercase letters (and
/// the annunciator/scroll glyphs) yields the readable text. Never throws — bad or empty
/// input yields <see cref="McduPage.Empty"/>.
/// </summary>
public static class McduDisplayParser
{
    public static McduPage Parse(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return McduPage.Empty;
        }

        try
        {
            var root = XDocument.Parse(xml).Root;
            if (root is null)
            {
                return McduPage.Empty;
            }

            var rawTitle = root.Element("title")?.Value ?? "";
            var title = Clean(rawTitle);

            // Twelve <line> elements = six rows of small-label line + data/LSK line.
            var lines = root.Elements("line").Select(e => e.Value).ToList();
            var rows = new List<McduRow>();
            for (var i = 0; i + 1 < lines.Count; i += 2)
            {
                rows.Add(new McduRow(rows.Count + 1, Clean(lines[i]), Clean(lines[i + 1])));
            }

            var scratchpad = Clean(root.Element("scratchpad")?.Value);

            // Temporary flight plan: the box shows "TMPY" (and colours everything yellow)
            // while one is staged — the arrival changer's whole safety story hangs off this.
            var temporary = ContainsTmpy(title) || ContainsTmpy(scratchpad)
                || rows.Any(r => ContainsTmpy(r.Label) || ContainsTmpy(r.Data));

            // Scroll arrows live in the RAW title (empirical, Prosim2FO 2025):
            // £ = up available, ¢ = down available. Clean() strips them, so probe rawTitle.
            var up = rawTitle.Contains('£');
            var down = rawTitle.Contains('¢');

            return new McduPage(title, rows, scratchpad, temporary, up, down, xml.Trim());
        }
        catch
        {
            return McduPage.Empty;
        }
    }

    /// <summary>Strips colour/font codes (lowercase letters) and annunciator/scroll glyphs,
    /// then trims. Keeps upper-case text, digits and symbols (/, [], &lt;&gt;, °, -).</summary>
    public static string Clean(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return "";
        }

        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (c is >= 'a' and <= 'z')
            {
                continue; // colour/font code
            }

            if (c is '£' or '¢' or '¥' or '¤')
            {
                continue; // scroll/annunciator glyphs
            }

            sb.Append(c);
        }

        return sb.ToString().Trim();
    }

    /// <summary>Collapses runs of whitespace to a single space (for speech).</summary>
    public static string Collapse(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    /// <summary>Keeps only tokens that carry a letter or digit (drops dash/bracket
    /// placeholders), joined by ", " — turns a data line into an intelligible spoken
    /// fragment.</summary>
    public static string Speakable(string line)
    {
        var tokens = Collapse(line.Replace('<', ' ').Replace('>', ' '))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Any(char.IsLetterOrDigit));
        return string.Join(", ", tokens);
    }

    private static bool ContainsTmpy(string s) => s.Contains("TMPY", StringComparison.Ordinal);
}
