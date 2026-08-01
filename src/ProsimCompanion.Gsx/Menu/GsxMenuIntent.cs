using System.Text.RegularExpressions;
using ProsimCompanion.Gsx.Mirror;

namespace ProsimCompanion.Gsx.Menu;

/// <summary>
/// Describes *what* to click in terms of the live menu — expected title prefix(es) plus a
/// regex line matcher — never a bare ordinal. See docs/integrations/gsx-remote-api.md §5.
/// </summary>
public sealed record GsxMenuIntent
{
    public required string Name { get; init; }

    /// <summary>The menu must carry one of these title prefixes (StartsWith, case-insensitive)
    /// before any pick is attempted.</summary>
    public required IReadOnlyList<string> TitlePrefixes { get; init; }

    /// <summary>Matches the entry line to pick. Null (with <see cref="EntryIndex"/> also null)
    /// makes this a navigation-only intent: success is reaching a shown menu with a matching
    /// title (no pick).</summary>
    public Regex? EntryPattern { get; init; }

    /// <summary>Positional pick for menus whose lines carry no stable keyword (e.g. GSX's
    /// "Select Position at …" list). Guarded by the title check, bounds and disabled checks,
    /// and the TOCTOU re-validation like any other pick. Ignored when
    /// <see cref="EntryPattern"/> is set.</summary>
    public int? EntryIndex { get; init; }

    /// <summary>When set, this menu is reached by executing the parent intent first — the
    /// parent's pick opens this submenu.</summary>
    public GsxMenuIntent? ParentMenu { get; init; }

    /// <summary>Custom post-pick verification against the mirror; null uses the default
    /// (menu dismissed, or title moved off this intent's prefixes).</summary>
    public Func<GsxStateMirror, bool>? Verify { get; init; }

    /// <summary>Overrides the configured verify budget (the reposition submenu is known to
    /// exceed the 5 s default).</summary>
    public TimeSpan? VerifyTimeout { get; init; }

    /// <summary>True when the live title satisfies this intent's prefixes.</summary>
    public bool TitleMatches(string? title)
    {
        if (title is null)
        {
            return false;
        }

        foreach (var prefix in TitlePrefixes)
        {
            if (title.StartsWith(prefix, StringComparison.InvariantCultureIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Every way an intent can conclude. All non-success outcomes obey the safe-fail rule:
/// the menu is left open for the user, logged — never a wrong click.</summary>
public enum GsxIntentOutcome
{
    Success,

    /// <summary>API not Ready, menu never appeared, pick refused, or verification timed out.</summary>
    GsxNoResponse,

    /// <summary>A menu is shown but its title is not the expected one.</summary>
    MenuTitleMismatch,

    /// <summary>No entry matched, or the matched entry is disabled/greyed.</summary>
    ItemNotAvailable,

    /// <summary>More than one entry matched — picking any would be a guess.</summary>
    AmbiguousMatch,
}

public sealed record GsxIntentResult(GsxIntentOutcome Outcome, string Detail)
{
    public bool Succeeded => Outcome == GsxIntentOutcome.Success;
}
