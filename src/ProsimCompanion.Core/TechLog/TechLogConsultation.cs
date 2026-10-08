using System.Text.RegularExpressions;

namespace ProsimCompanion.Core.TechLog;

/// <summary>
/// The procedural hooks that let a briefing or a checklist consult the tech log (2026-10-09;
/// ROADMAP "Tech-log procedural hooks"). Pure functions over the open defects so the clauses
/// and the matching are testable without the store, the arbiter or a checklist engine.
/// Everything here produces WORDS for the FO: a defect never changes what a checklist line
/// verifies, and a briefing clause never gets styled — titles are locked facts, spoken verbatim.
/// </summary>
public static partial class TechLogConsultation
{
    /// <summary>How many titles a briefing clause names before folding the rest into "and N more".</summary>
    public const int MaxBriefedTitles = 3;

    /// <summary>Words in a title, system tag or implication that mark an item as bearing on
    /// the landing, whatever its MEL category. Whole-word, case-insensitive.</summary>
    private static readonly string[] LandingKeywords =
    [
        "brake", "brakes", "braking", "gear", "spoiler", "spoilers", "reverser", "reversers",
        "autobrake", "auto-brake", "anti-skid", "antiskid", "anti skid",
    ];

    /// <summary>The open items a departure briefing names: every open defect, most-due first
    /// (the service already orders them that way).</summary>
    public static IReadOnlyList<TechLogDefect> DepartureItems(IReadOnlyList<TechLogDefect> open)
    {
        ArgumentNullException.ThrowIfNull(open);
        return [.. open.Where(d => d.IsOpen)];
    }

    /// <summary>The open items an arrival briefing names: category A or B (the short-interval
    /// deferrals a crew re-reads before landing), or anything about the landing systems.</summary>
    public static IReadOnlyList<TechLogDefect> ArrivalItems(IReadOnlyList<TechLogDefect> open)
    {
        ArgumentNullException.ThrowIfNull(open);
        return [.. open.Where(d => d.IsOpen && (d.Category is MelCategory.A or MelCategory.B || AffectsLanding(d)))];
    }

    /// <summary>True when the item's title, system tag or implication names a landing system.</summary>
    public static bool AffectsLanding(TechLogDefect defect)
    {
        ArgumentNullException.ThrowIfNull(defect);
        var haystack = string.Join(' ', defect.Title, defect.System, defect.OperationalImplications, defect.Placard);
        return LandingKeywords.Any(keyword => ContainsWord(haystack, keyword));
    }

    /// <summary>The spoken briefing clause for a set of titles, or null when there are none:
    /// "Open tech log items: APU inoperative, and Cabin reading light unserviceable." / three
    /// titles "and 2 more." — a list the pilot can absorb by ear, not the whole log.</summary>
    public static string? BriefingClause(IReadOnlyList<TechLogDefect> items, bool departure)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return null;
        }

        var lead = departure ? "Open tech log items" : "Open tech log items affecting landing";
        var named = items.Take(MaxBriefedTitles).Select(d => d.Title.Trim()).ToList();
        var more = items.Count - named.Count;
        var list = named.Count switch
        {
            1 => named[0],
            2 => $"{named[0]}, and {named[1]}",
            _ => $"{string.Join(", ", named.Take(named.Count - 1))}, and {named[^1]}",
        };
        var tail = more > 0 ? $", and {more} more" : "";
        return $"{lead}: {list}{tail}.";
    }

    /// <summary>Open items a checklist line's <c>system</c> tag concerns: a defect whose own
    /// tag equals it, or — for untagged defects (voice-raised, wear pool) — whose title holds
    /// the tag as a whole word ("apu" in "APU inoperative"). A line without a tag never
    /// matches, so a checklist file that predates the tag behaves exactly as before.</summary>
    public static IReadOnlyList<TechLogDefect> MatchesForChecklistLine(string? lineSystem, IReadOnlyList<TechLogDefect> open)
    {
        ArgumentNullException.ThrowIfNull(open);
        var tag = NormalizeTag(lineSystem);
        if (tag.Length == 0)
        {
            return [];
        }

        return [.. open.Where(d => d.IsOpen && (
            string.Equals(NormalizeTag(d.System), tag, StringComparison.Ordinal)
            || (string.IsNullOrWhiteSpace(d.System) && ContainsWord(d.Title, tag))))];
    }

    /// <summary>The text appended to a checklist challenge for one matched item.</summary>
    public static string ChecklistNote(TechLogDefect defect)
    {
        ArgumentNullException.ThrowIfNull(defect);
        return $" — note, open tech log item: {defect.Title.Trim()}.";
    }

    /// <summary>Lower-case, trimmed, hyphens/underscores/spaces folded to one hyphen so
    /// "Anti Ice", "anti-ice" and "anti_ice" are the same tag.</summary>
    public static string NormalizeTag(string? tag)
        => tag is null ? "" : TagSeparators().Replace(tag.Trim().ToLowerInvariant(), "-");

    /// <summary>Whole-word containment with a tag that may itself hold a hyphen ("anti-ice"
    /// matches "Anti ice valve" because separators are folded on both sides).</summary>
    private static bool ContainsWord(string? haystack, string word)
    {
        if (string.IsNullOrWhiteSpace(haystack))
        {
            return false;
        }

        var folded = TagSeparators().Replace(haystack.ToLowerInvariant(), "-");
        var needle = TagSeparators().Replace(word.ToLowerInvariant(), "-");
        return Regex.IsMatch(folded, $@"(?<![a-z0-9]){Regex.Escape(needle)}(?![a-z0-9])");
    }

    [GeneratedRegex(@"[\s_\-]+")]
    private static partial Regex TagSeparators();
}
