using System.Collections.Concurrent;
using ProsimCompanion.Core.Localization;

namespace ProsimCompanion.Web.Layout;

/// <summary>One thing the settings search can land on: a rail entry, or a card of a page.</summary>
/// <param name="Group">The rail group.</param>
/// <param name="Item">The rail item, or null for a single-page group.</param>
/// <param name="Title">What to show: the card title, else the rail label.</param>
/// <param name="Href">Where to go.</param>
/// <param name="Haystack">Every text of the card (or the label), lower-cased, for matching.</param>
/// <param name="IsNavLabel">True for the rail entry itself (ranked above a card hit).</param>
public sealed record SettingsSearchEntry(
    SettingsNavGroup Group,
    SettingsNavItem? Item,
    string Title,
    string Href,
    string Haystack,
    bool IsNavLabel);

/// <summary>
/// The settings rail search (hub-usability pass, issue #199): data-driven over the language
/// pack. Every page's pack file is one root (<c>speechSettings</c>, <c>gsxSettings</c>, …)
/// and every second-level block of it is one card (<c>speechSettings.callouts.*</c>), so the
/// index is the pack's own texts — labels, hints, the "more" paragraphs — attributed to the
/// rail item that renders the block. A block whose name is not a section key is mapped in
/// <see cref="BlockSections"/>; a source-scanning test keeps that table honest against the
/// Razor pages' <c>CurrentSection == "…"</c> branches. No markup is parsed at runtime.
/// </summary>
public static class SettingsSearch
{
    /// <summary>Pack root → the page's route base and (for single pages) its rail location.
    /// Roots with sections resolve the item per block; the others land on the page.</summary>
    public static readonly IReadOnlyList<(string Root, string RouteBase, string? GroupId, string? ItemId)> Roots =
    [
        ("setup", "settings", "setup", null),
        ("gsxSettings", "settings/gsx", null, null),
        ("speechSettings", "settings/speech", null, null),
        ("speechStatus", "settings/speech", null, "status"),
        ("audioSettings", "settings/audio", null, null),
        ("cockpit", "settings/cockpit", "cockpit", null),
        ("display", "settings/display", null, null),
        ("skalarkiSettings", "settings/hardware/skalarki", "hardware", "skalarki"),
        ("appSettings", "settings/app", "app", null),
        ("appearance", "settings/appearance", "appearance", null),
        ("notifications", "settings/notifications", "notifications", null),
        ("profiles", "settings/profiles", "profiles", null),
        ("flightPhase", "settings/flight-phase", "advanced", "flightPhase"),
        ("logs", "settings/logs", "advanced", "logs"),
        ("diagnostics", "settings/diagnostics", "advanced", "diagnostics"),
    ];

    /// <summary>Pack blocks whose name is not the section that renders them
    /// (<c>root.block</c> → section key). Blocks named after their section need no entry.</summary>
    public static readonly IReadOnlyDictionary<string, string> BlockSections = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // Voice First Officer (SpeechSettings.razor)
        ["speechSettings.techLog"] = "techlog",
        ["speechSettings.smallTalk"] = "briefing",
        ["speechSettings.foSide"] = "mcdu",
        ["speechSettings.scenario"] = "abnormals",
        ["speechSettings.devices"] = "listening",
        ["speechSettings.binding"] = "listening",
        ["speechSettings.capture"] = "listening",
        ["speechSettings.scan"] = "listening",
        ["speechSettings.voiceNotes"] = "voices",
        // Ground Services (GsxSettings.razor)
        ["gsxSettings.jetway"] = "doors",
        ["gsxSettings.automation"] = "departure",
        ["gsxSettings.coldDark"] = "departure",
        ["gsxSettings.serviceOrder"] = "departure",
        ["gsxSettings.simbrief"] = "departure",
        ["gsxSettings.boarding"] = "refuel",
        ["gsxSettings.passengers"] = "refuel",
        ["gsxSettings.beaconDelays"] = "pushback",
        ["gsxSettings.pushbackDirection"] = "pushback",
        ["gsxSettings.fuelMemory"] = "arrival",
        ["gsxSettings.standKnowledge"] = "arrival",
        ["gsxSettings.hubs"] = "operators",
        ["gsxSettings.deice"] = "questions",
        ["gsxSettings.activation"] = "connection",
        ["gsxSettings.constraint"] = "connection",
        ["gsxSettings.timeouts"] = "connection",
        ["gsxSettings.stage"] = "status",
        ["gsxSettings.arrivalGate"] = "status",
        ["gsxSettings.departureBoard"] = "status",
        ["gsxSettings.deiceStatus"] = "status",
        ["gsxSettings.captainsQuestions"] = "status",
        ["gsxSettings.diagnostics"] = "status",
        ["gsxSettings.services"] = "status",
        ["gsxSettings.menu"] = "status",
        ["gsxSettings.decisions"] = "status",
        ["gsxSettings.commands"] = "status",
        ["gsxSettings.menuCard"] = "status",
        // Audio Control (AudioSettings.razor)
        ["audioSettings.coreAudio"] = "coreaudio",
        ["audioSettings.voiceMeeter"] = "voicemeeter",
        ["audioSettings.housekeeping"] = "advanced",
        ["audioSettings.remoteMixer"] = "mixer",
        // Screen & FOV (DisplaySettings.razor)
        ["display.roleOptions"] = "displays",
        ["display.row"] = "displays",
        ["display.file"] = "apply",
        ["display.readout"] = "apply",
        ["display.realism"] = "apply",
        ["display.result"] = "apply",
        ["display.multi"] = "multiwindow",
        ["display.cp"] = "chaseplane",
    };

    /// <summary>Blocks that are shared wording or choice lists, not a card — never a hit.</summary>
    private static readonly HashSet<string> SkippedBlocks = new(StringComparer.Ordinal)
    {
        "speechSettings.common", "speechSettings.options", "gsxSettings.options", "setup.pageTitle",
    };

    /// <summary>Builds the index over one language's strings (ids are <c>root.block.rest</c>).</summary>
    public static IReadOnlyList<SettingsSearchEntry> Build(IReadOnlyDictionary<string, string> strings)
    {
        ArgumentNullException.ThrowIfNull(strings);
        var entries = new List<SettingsSearchEntry>();

        // The rail itself: every group and item by its (translated) label.
        foreach (var group in SettingsNav.Groups)
        {
            var groupLabel = Text(strings, group.LabelTextId, group.Label);
            entries.Add(new SettingsSearchEntry(group, null, groupLabel, group.Href, groupLabel.ToLowerInvariant(), true));
            foreach (var item in group.Items)
            {
                var label = Text(strings, item.LabelTextId, item.Label);
                entries.Add(new SettingsSearchEntry(group, item, label, item.Href, label.ToLowerInvariant(), true));
            }
        }

        // The cards: one entry per pack block, all of its texts as the haystack. A third-level
        // block with a title of its own (speechSettings.callouts.transition) is its own card
        // under the second-level block's rail place, so a nested card is found by its name.
        var cards = new Dictionary<string, (string Block, List<string> Texts)>(StringComparer.Ordinal);
        foreach (var (id, text) in strings)
        {
            var first = id.IndexOf('.');
            var second = first < 0 ? -1 : id.IndexOf('.', first + 1);
            if (second < 0)
            {
                continue;
            }

            var block = id[..second];
            if (SkippedBlocks.Contains(block))
            {
                continue;
            }

            var third = id.IndexOf('.', second + 1);
            var card = third > 0 && strings.ContainsKey(id[..third] + ".title") ? id[..third] : block;
            if (!cards.TryGetValue(card, out var entry))
            {
                entry = (block, []);
                cards[card] = entry;
            }

            entry.Texts.Add(text);
        }

        foreach (var (card, (block, texts)) in cards.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            if (Locate(block) is not { } location)
            {
                continue;
            }

            var (group, item, href) = location;
            var title = strings.TryGetValue(card + ".title", out var t) && t.Length > 0 ? t
                : strings.TryGetValue(card + ".label", out var l) && l.Length > 0 ? l
                : item is null ? Text(strings, group.LabelTextId, group.Label) : Text(strings, item.LabelTextId, item.Label);
            var haystack = string.Join('\n', texts.Prepend(title)).ToLowerInvariant();
            entries.Add(new SettingsSearchEntry(group, item, title, href, haystack, false));
        }

        return entries;
    }

    /// <summary>The rail location of a pack block, or null for a root that is not a settings page.</summary>
    public static (SettingsNavGroup Group, SettingsNavItem? Item, string Href)? Locate(string block)
    {
        ArgumentNullException.ThrowIfNull(block);
        var dot = block.IndexOf('.');
        var root = dot < 0 ? block : block[..dot];
        var name = dot < 0 ? "" : block[(dot + 1)..];
        var entry = Roots.FirstOrDefault(r => r.Root.Equals(root, StringComparison.Ordinal));
        if (entry.Root is null)
        {
            return null;
        }

        if (entry.GroupId is not null)
        {
            // A single page, or a page that is one item of a group (Skalarki, the Advanced trio).
            var group = SettingsNav.Groups.First(g => g.Key == entry.GroupId);
            var item = entry.ItemId is null ? null : group.Items.FirstOrDefault(i => i.Key == entry.ItemId);
            return (group, item, item?.Href ?? group.Href);
        }

        // A sectioned page: the mapped section, else a block named after its section (case-insensitive).
        var itemId = entry.ItemId
            ?? (BlockSections.TryGetValue(block, out var mapped) ? mapped : null)
            ?? SettingsNav.Groups
                .SelectMany(g => g.Items)
                .FirstOrDefault(i => i.Href.StartsWith(entry.RouteBase + "/", StringComparison.OrdinalIgnoreCase)
                                     && i.Key.Equals(name, StringComparison.OrdinalIgnoreCase))?.Key;
        if (itemId is not null && SettingsNav.FindItem(entry.RouteBase + "/" + itemId) is { } found)
        {
            return (found.Group, found.Item, found.Item.Href);
        }

        // A block that names no known section lands on the page itself.
        return SettingsNav.GroupFor(entry.RouteBase) is { } owner ? (owner, owner.ActiveItem(entry.RouteBase), entry.RouteBase) : null;
    }

    /// <summary>The entries matching every word of <paramref name="query"/>, rail labels and
    /// card titles first, at most <paramref name="max"/>.</summary>
    public static IReadOnlyList<SettingsSearchEntry> Find(IReadOnlyList<SettingsSearchEntry> index, string? query, int max = 12)
    {
        ArgumentNullException.ThrowIfNull(index);
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => w.ToLowerInvariant())
            .ToArray();
        if (words.Length == 0 || words.All(w => w.Length < 2))
        {
            return [];
        }

        var hits = new List<(SettingsSearchEntry Entry, int Rank)>();
        foreach (var entry in index)
        {
            if (!words.All(w => entry.Haystack.Contains(w, StringComparison.Ordinal)))
            {
                continue;
            }

            var title = entry.Title.ToLowerInvariant();
            var rank = entry.IsNavLabel ? 0 : words.All(w => title.Contains(w, StringComparison.Ordinal)) ? 1 : 2;
            hits.Add((entry, rank));
        }

        return [.. hits
            .OrderBy(h => h.Rank)
            .ThenBy(h => Array.IndexOf(SettingsNav.Groups, h.Entry.Group))
            .ThenBy(h => h.Entry.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(h => h.Entry)
            .DistinctBy(e => (e.Href, e.Title))
            .Take(max)];
    }

    private static string Text(IReadOnlyDictionary<string, string> strings, string id, string fallback)
        => strings.TryGetValue(id, out var text) && text.Length > 0 ? text : fallback;
}

/// <summary>
/// The per-language search index, built on first use from the English pack overlaid with the
/// language's own strings (a missing translation stays findable by its English words) and
/// kept until the catalogue rescans.
/// </summary>
public sealed class SettingsSearchService
{
    private readonly LanguagePackCatalog _catalog;
    private readonly ConcurrentDictionary<(string Code, LanguagePack? Pack), IReadOnlyList<SettingsSearchEntry>> _indexes = new();

    public SettingsSearchService(LanguagePackCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
    }

    /// <summary>The matches for <paramref name="query"/> in <paramref name="languageCode"/>.</summary>
    public IReadOnlyList<SettingsSearchEntry> Find(string languageCode, string? query, int max = 12)
    {
        ArgumentNullException.ThrowIfNull(languageCode);
        var pack = _catalog.Find(languageCode);
        var index = _indexes.GetOrAdd((languageCode, pack), _ => SettingsSearch.Build(Merged(pack)));
        return SettingsSearch.Find(index, query, max);
    }

    private IReadOnlyDictionary<string, string> Merged(LanguagePack? pack)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_catalog.Find(LanguagePackCatalog.DefaultCode) is { } english)
        {
            foreach (var (id, text) in english.Strings)
            {
                merged[id] = text;
            }
        }

        if (pack is not null)
        {
            foreach (var (id, text) in pack.Strings)
            {
                merged[id] = text;
            }
        }

        return merged;
    }
}
