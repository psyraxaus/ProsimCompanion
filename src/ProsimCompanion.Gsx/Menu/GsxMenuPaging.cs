using System.Text.RegularExpressions;
using ProsimCompanion.Gsx.Mirror;

namespace ProsimCompanion.Gsx.Menu;

/// <summary>
/// Turns the pages of a GSX menu through the intent executor (2026-10-11, issues #75 and
/// #174). GSX paginates every list longer than its panel — the in-flight "Select airport"
/// list, "Additional Services", "Select Position at …" — with a "Next Page ▶" row at the
/// bottom and "↑ Back" / "Previous" rows, and until this change nothing in the app ever
/// turned a page: the SimBrief reload never left page 1 of Additional Services and the airport
/// pick never saw EFHK, which was not among the first eight nearby fields. A turn is an
/// ordinary keyword intent (safe-fail, TOCTOU re-resolve) whose verification is "the rows
/// changed while the title stayed" — the page title does not change between pages. Callers
/// bound the number of turns themselves.
/// </summary>
public static class GsxMenuPaging
{
    /// <summary>GSX's "Next Page ▶" row (the arrow is a Unicode glyph; the match is on the words).</summary>
    public static readonly Regex NextPageRow = new(@"^\s*next\s+page\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>GSX's "↑ Back" / "Previous" / "Back" rows — never a candidate for anything.</summary>
    public static readonly Regex BackRow = new(@"^\s*(↑\s*)?(back|previous|prev)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>True when the page offers a next page.</summary>
    public static bool HasNextPage(IReadOnlyList<string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries.Any(NextPageRow.IsMatch);
    }

    /// <summary>
    /// Picks "Next Page" on the shown menu and waits for the rows to change. Null when the
    /// page has no next-page row (the walk is over); otherwise the executor's result — a
    /// failure leaves the menu where it is, for the user, as every intent does.
    /// </summary>
    public static async Task<GsxIntentResult?> TurnPageAsync(
        GsxMenuIntentExecutor executor,
        string intentName,
        IReadOnlyList<string> titlePrefixes,
        IReadOnlyList<string> currentEntries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(titlePrefixes);
        ArgumentNullException.ThrowIfNull(currentEntries);
        if (!HasNextPage(currentEntries))
        {
            return null;
        }

        var before = currentEntries.ToList();
        var turn = new GsxMenuIntent
        {
            Name = intentName,
            TitlePrefixes = titlePrefixes,
            EntryPattern = NextPageRow,
            Verify = mirror => mirror.MenuShown && mirror.Menu is { } menu && !menu.Entries.SequenceEqual(before, StringComparer.Ordinal),
        };
        return await executor.ExecuteAsync(turn, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>"'Additional Services' p2: [Request GPU | Request DeIce | …]" — one page for the
    /// decision log, so the next flight's reader sees what GSX offered.</summary>
    public static string DescribePage(GsxMenuInfo? menu, int pageNumber)
        => menu is null
            ? $"p{pageNumber}: (menu gone)"
            : $"'{menu.Title}' p{pageNumber}: [{string.Join(" | ", menu.Entries)}]";
}
