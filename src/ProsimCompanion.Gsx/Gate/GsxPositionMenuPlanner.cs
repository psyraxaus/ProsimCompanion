using System.Text.RegularExpressions;

namespace ProsimCompanion.Gsx.Gate;

/// <summary>What to pick on GSX's own "Select Position at …" page for a requested gate.</summary>
/// <param name="Entry">The row text, exactly as shown (the executor matches it anchored).</param>
/// <param name="IsPosition">True when the row IS the position (a flat list); false when it is
/// a facility group whose positions page must then be searched for the token.</param>
public sealed record GsxPositionMenuPick(string Entry, bool IsPosition);

/// <summary>
/// Pure rules for answering GSX's "Select Position at &lt;airport&gt;" menu with the pilot's
/// arrival gate (issue #156, EFHK W40 2026-10-04: gate.select was refused four times and GSX
/// then asked the question itself, which the app only watched). The page lists facility
/// groups — <c>Apron 1W (Gates W34-W48)</c>, <c>Apron 4 (Cargo, 401-411)</c>, <c>Remote Stands
/// 8XX/9XX</c> — or, at small fields, the positions themselves. The pick is text-resolved only:
/// a row that names the token, else the ONE row whose range covers it; two covering rows, a
/// pager row, or nothing → null and the menu stays with the pilot (§7 safe-fail rule).
/// </summary>
public static partial class GsxPositionMenuPlanner
{
    /// <summary>Rows that are never a facility: paging and navigation.</summary>
    private static readonly string[] NavigationRows = ["next", "previous", "back", "cancel", "change facility", "type a gate"];

    public static GsxPositionMenuPick? Pick(IReadOnlyList<string> entries, string requestedGate)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var token = (requestedGate ?? "").Trim().ToUpperInvariant();
        if (token.Length == 0)
        {
            return null;
        }

        var rows = entries
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Where(e => !NavigationRows.Any(n => e.Trim().StartsWith(n, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // 1. A row that names the position itself ("Gate W40", "Stand 401 [Medium]").
        var whole = new Regex($@"(?<![A-Z0-9]){Regex.Escape(token)}(?![A-Z0-9])", RegexOptions.IgnoreCase);
        var direct = rows.Where(r => whole.IsMatch(r)).ToList();
        if (direct.Count == 1)
        {
            return new GsxPositionMenuPick(direct[0], IsPosition: !LooksLikeGroup(direct[0]));
        }

        if (direct.Count > 1)
        {
            return null;
        }

        // 2. The one facility group whose range covers the token.
        var covering = rows.Where(r => Covers(r, token)).ToList();
        return covering.Count == 1 ? new GsxPositionMenuPick(covering[0], IsPosition: false) : null;
    }

    /// <summary>"Apron 1W (Gates W34-W48)" reads as a group; "Gate W40" does not.</summary>
    internal static bool LooksLikeGroup(string row)
        => RangeRegex().IsMatch(row) || HundredsRegex().IsMatch(row)
            || row.Contains("positions", StringComparison.OrdinalIgnoreCase)
            || row.Contains("Gates", StringComparison.OrdinalIgnoreCase)
            || row.Contains("Stands", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when a range in the row — <c>W34-W48</c>, <c>401-411</c>, <c>301-365</c>,
    /// <c>8XX/9XX</c> — covers the token: same letter prefix (or none on both), number inside.</summary>
    internal static bool Covers(string row, string token)
    {
        var t = TokenRegex().Match(token);
        if (!t.Success)
        {
            return false;
        }

        var prefix = t.Groups["p"].Value;
        var number = int.Parse(t.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture);

        foreach (Match m in RangeRegex().Matches(row))
        {
            var fromPrefix = m.Groups["p1"].Value.ToUpperInvariant();
            var toPrefix = m.Groups["p2"].Value.ToUpperInvariant();
            var rangePrefix = toPrefix.Length > 0 ? toPrefix : fromPrefix;
            if (!string.Equals(rangePrefix, prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var from = int.Parse(m.Groups["n1"].Value, System.Globalization.CultureInfo.InvariantCulture);
            var to = int.Parse(m.Groups["n2"].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (number >= Math.Min(from, to) && number <= Math.Max(from, to))
            {
                return true;
            }
        }

        if (prefix.Length == 0)
        {
            foreach (Match m in HundredsRegex().Matches(row))
            {
                var hundreds = int.Parse(m.Groups["h"].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (number / 100 == hundreds)
                {
                    return true;
                }
            }
        }

        return false;
    }

    // "W40", "401", "545R" → prefix W / none, number 40 / 401 / 545 (a trailing letter is ignored for ranges).
    [GeneratedRegex(@"^(?<p>[A-Z]{0,2})(?<n>\d{1,4})[A-Z]?$")]
    private static partial Regex TokenRegex();

    // "W34-W48", "401-411", "301 – 365"
    [GeneratedRegex(@"(?<![A-Z0-9])(?<p1>[A-Z]{0,2})(?<n1>\d{1,4})\s*[-–]\s*(?<p2>[A-Z]{0,2})(?<n2>\d{1,4})(?![0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex RangeRegex();

    // "8XX", "9XX"
    [GeneratedRegex(@"(?<![A-Z0-9])(?<h>\d)XX(?![A-Z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex HundredsRegex();
}
