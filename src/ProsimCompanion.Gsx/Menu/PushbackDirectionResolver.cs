namespace ProsimCompanion.Gsx.Menu;

/// <summary>The resolved menu line for a pushback direction preference.</summary>
public sealed record PushbackDirectionPick(int Index, string Entry, string Strategy);

/// <summary>
/// Pure entry matching for GSX's "Select pushback direction" menu, ported from Prosim2GSX's
/// proven behaviour (issue #41 — the first port regressed it three ways at once):
/// the straight token is "Straight pushback" so it can never tie with "Straight Pull pushback";
/// matches are ranked (exact > starts-with > contains) instead of failing on multiples; and
/// tail preferences fall back to fixed menu positions, because stands phrase their directional
/// entries with airport-specific compass text ("Nose Right - Facing North on Taxiway C") that
/// contains no stable keyword. Direction entries are always the first two lines; tail left is
/// the stand's nose-right line, hence tailLeft → index 0.
/// </summary>
public static class PushbackDirectionResolver
{
    // Lines that are never direction entries; if one occupies a fallback slot, the menu is not
    // shaped like a direction menu and the fixed-index fallback must refuse (predecessor list).
    private static readonly string[] MetaPrefixes =
        ["Straight", "QuickEdit", "Customize", "GSX", "Restart", "SimBrief"];

    public static PushbackDirectionPick? Resolve(IReadOnlyList<string> entries, string preference)
    {
        ArgumentNullException.ThrowIfNull(entries);

        return preference switch
        {
            "tailLeft" => ResolveTail(entries, "Tail Left", fallbackIndex: 0),
            "tailRight" => ResolveTail(entries, "Tail Right", fallbackIndex: 1),
            _ => RankedMatch(entries, "Straight pushback"),
        };
    }

    private static PushbackDirectionPick? ResolveTail(
        IReadOnlyList<string> entries, string token, int fallbackIndex)
    {
        if (RankedMatch(entries, token) is { } byText)
        {
            return byText;
        }

        if (entries.Count < 2
            || IsMetaLine(entries[0]) || IsMetaLine(entries[1])
            || fallbackIndex >= entries.Count)
        {
            return null;
        }

        return new PushbackDirectionPick(fallbackIndex, entries[fallbackIndex], "fixed-index");
    }

    private static PushbackDirectionPick? RankedMatch(IReadOnlyList<string> entries, string token)
    {
        PushbackDirectionPick? startsWith = null;
        PushbackDirectionPick? contains = null;

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            if (string.Equals(entry.Trim(), token, StringComparison.OrdinalIgnoreCase))
            {
                return new PushbackDirectionPick(i, entry, "exact");
            }

            if (startsWith is null && entry.TrimStart().StartsWith(token, StringComparison.OrdinalIgnoreCase))
            {
                startsWith = new PushbackDirectionPick(i, entry, "starts-with");
            }
            else if (contains is null && entry.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                contains = new PushbackDirectionPick(i, entry, "contains");
            }
        }

        return startsWith ?? contains;
    }

    private static bool IsMetaLine(string line)
        => string.IsNullOrWhiteSpace(line)
            || MetaPrefixes.Any(prefix => line.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
