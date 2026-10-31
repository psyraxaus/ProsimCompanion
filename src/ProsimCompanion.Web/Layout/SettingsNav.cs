namespace ProsimCompanion.Web.Layout;

/// <summary>One entry of the settings rail: a page (or a section of a page) at a URL.
/// <paramref name="Label"/> is the English text; the rendered label comes from the language
/// pack under <see cref="LabelTextId"/> (ADR-0014), and the pack guard test keeps the two equal.</summary>
public sealed record SettingsNavItem(string Key, string Label, string Href)
{
    /// <summary>Language-pack text id of this item's label: <c>settings.nav.{group}.{item}</c>.</summary>
    public string LabelTextId { get; init; } = "";
}

/// <summary>
/// One group of the settings rail. A group with items expands to show them while it is
/// active; a group without items is a single page.
/// </summary>
public sealed record SettingsNavGroup(string Key, string Label, string Href, IReadOnlyList<SettingsNavItem> Items)
{
    /// <summary>Language-pack text id of this group's label: <c>settings.nav.{group}</c>.</summary>
    public string LabelTextId => "settings.nav." + Key;

    /// <summary>True when <paramref name="path"/> (base-relative, no query) is this group or
    /// one of its items. Only a group WITH items owns its sub-paths — the single-page Setup
    /// group at "settings" must not swallow "settings/gsx/…".</summary>
    public bool Matches(string path)
        => path.Equals(Href, StringComparison.OrdinalIgnoreCase)
           || Items.Any(item => item.Href.Equals(path, StringComparison.OrdinalIgnoreCase))
           || (Items.Count > 0 && path.StartsWith(Href + "/", StringComparison.OrdinalIgnoreCase));

    /// <summary>True when one of this group's items is exactly <paramref name="path"/> (or
    /// a sub-path of it, e.g. a provider tab). The hub-usability split (issue #199) put three
    /// rail groups over one page route, so the owner of a path is the group that LISTS it,
    /// never the one whose base path merely prefixes it.</summary>
    public bool ListsItem(string path)
        => Items.Any(item => item.Href.Equals(path, StringComparison.OrdinalIgnoreCase)
                             || path.StartsWith(item.Href + "/", StringComparison.OrdinalIgnoreCase));

    /// <summary>The active item for <paramref name="path"/>: an exact href match, else the
    /// first item (the group's landing section).</summary>
    public SettingsNavItem? ActiveItem(string path)
        => Items.FirstOrDefault(item => item.Href.Equals(path, StringComparison.OrdinalIgnoreCase)
                                        || path.StartsWith(item.Href + "/", StringComparison.OrdinalIgnoreCase))
           ?? (Items.Count > 0 ? Items[0] : null);
}

/// <summary>
/// The single settings hub (settings UX pass 2026-09-19, ADR-0010): every configuration
/// page hangs off one rail, grouped by pillar, and each section has its own URL so a page
/// refresh, the Back button and links from banners all land on the right section. The
/// section key lists double as the pages' valid <c>{Section?}</c> route values.
/// </summary>
public static class SettingsNav
{
    private static SettingsNavItem[] Items(string basePath, params (string Key, string Label)[] sections)
    {
        var group = basePath[(basePath.LastIndexOf('/') + 1)..];
        return [.. sections.Select(s => new SettingsNavItem(s.Key, s.Label, $"{basePath}/{s.Key}")
        {
            LabelTextId = $"settings.nav.{group}.{s.Key}",
        })];
    }

    public static readonly SettingsNavItem[] GsxSections = Items("settings/gsx",
        ("status", "Status"),
        ("doors", "Doors & Jetway"),
        ("equipment", "Ground Equipment"),
        ("departure", "Departure Services"),
        ("refuel", "Refuel & Boarding"),
        ("pushback", "Pushback"),
        ("arrival", "Arrival"),
        ("operators", "Operators & Hubs"),
        ("questions", "GSX Questions"),
        ("connection", "Connection & Timeouts"));

    // The Voice First Officer page (one route, settings/speech/{section}) is three rail groups
    // since the hub-usability pass (issue #199, owner request 2026-11-01): sixteen items under
    // one heading were too long to scan. The pack ids keep the "speech" group segment
    // (settings.nav.speech.{section}) because Items() derives it from the route, so no text
    // moved — only the headings are new.

    /// <summary>The First Officer themselves: on/off, seat, persona, listening, voices.</summary>
    public static readonly SettingsNavItem[] SpeechCoreSections = Items("settings/speech",
        ("status", "Status"),
        ("general", "General"),
        ("persona", "FO Persona"),
        ("listening", "Listening & PTT"),
        ("sterile", "Sterile Cockpit"),
        ("voices", "Voices"),
        ("providers", "Voice Providers"));

    /// <summary>What the FO does at the controls and on the radios.</summary>
    public static readonly SettingsNavItem[] FlightDeckSections = Items("settings/speech",
        ("callouts", "Callouts & Placards"),
        ("abnormals", "Abnormals & Drills"),
        ("briefing", "Briefings & LLM"),
        ("mcdu", "MCDU"),
        ("sayIntentions", "SayIntentions"));

    /// <summary>The other voices: ground crew, cabin crew, company, the paperwork.</summary>
    public static readonly SettingsNavItem[] CrewSections = Items("settings/speech",
        ("groundCrew", "Ground Crew"),
        ("cabin", "Cabin Crew"),
        ("company", "Company & Debrief"),
        ("techlog", "Tech Log & Logbook"));

    /// <summary>Every section the Voice First Officer page route accepts — the three rail
    /// groups' lists joined, in rail order (the page resolves <c>{Section?}</c> against it).</summary>
    public static readonly SettingsNavItem[] SpeechSections = [.. SpeechCoreSections, .. FlightDeckSections, .. CrewSections];

    public static readonly SettingsNavItem[] AudioSections = Items("settings/audio",
        ("status", "Status"),
        ("general", "General"),
        ("coreaudio", "CoreAudio"),
        ("voicemeeter", "VoiceMeeter"),
        // Remote mixer (VoicemeeterBridge on another PC, 2026-10-10).
        ("mixer", "Remote Mixer"),
        ("advanced", "Housekeeping"));

    // Screen & FOV (2026-10-28): the pilot's physical screens, the one-button cameras.cfg
    // rewrite, and the Multi Window / ChasePlane read-outs. The group key stays "display"
    // (route settings/display); the label says "Screen" so the rail does not read as a twin
    // of "Display & Flight Data" two lines down.
    public static readonly SettingsNavItem[] DisplaySections = Items("settings/display",
        ("displays", "Displays"),
        ("apply", "Apply FOV"),
        ("multiwindow", "Multi Window"),
        ("chaseplane", "ChasePlane"));

    public static readonly SettingsNavGroup[] Groups =
    [
        new("setup", "Setup", "settings", []),
        new("gsx", "Ground Services", "settings/gsx", GsxSections),
        new("speech", "Voice First Officer", "settings/speech", SpeechCoreSections),
        new("flightDeck", "Flight Deck", "settings/speech/callouts", FlightDeckSections),
        new("crew", "Crew & Company", "settings/speech/groundCrew", CrewSections),
        new("audio", "Audio Control", "settings/audio", AudioSections),
        // Cockpit switches (2026-10-24): the Stream Deck's Korry buttons, toggles and knobs.
        new("cockpit", "Cockpit Switches", "settings/cockpit", []),
        new("display", "Screen & FOV", "settings/display", DisplaySections),
        // Hardware (2026-10-27): the Skalarki Profiler link behind the PANELS diagnostics page.
        new("hardware", "Hardware", "settings/hardware/skalarki",
        [
            new("skalarki", "Skalarki Panels", "settings/hardware/skalarki") { LabelTextId = "settings.nav.hardware.skalarki" },
        ]),
        new("app", "Display & Flight Data", "settings/app", []),
        new("appearance", "Appearance", "settings/appearance", []),
        new("notifications", "Notifications", "settings/notifications", []),
        new("profiles", "Aircraft Profiles", "settings/profiles", []),
        // The group link must land on a real page: there is no /settings/advanced route, so
        // the header opens the first section (clicking it gave a 404 until 2026-09-26).
        new("advanced", "Advanced", "settings/flight-phase",
        [
            new("flightPhase", "Flight Phase Engine", "settings/flight-phase") { LabelTextId = "settings.nav.advanced.flightPhase" },
            new("logs", "Logs", "settings/logs") { LabelTextId = "settings.nav.advanced.logs" },
            // The ECAM diagnostics pages' two knobs (ADR-0016).
            new("diagnostics", "Diagnostics", "settings/diagnostics") { LabelTextId = "settings.nav.advanced.diagnostics" },
        ]),
    ];

    /// <summary>The groups that share the Voice First Officer page and its pillar switch.</summary>
    public static bool IsSpeechGroup(SettingsNavGroup? group)
        => group?.Key is "speech" or "flightDeck" or "crew";

    /// <summary>Old routes that still resolve (bookmarks, the manual, Stream Deck docs) mapped
    /// onto their hub location so the rail highlights correctly.</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gsx"] = "settings/gsx",
        ["speech"] = "settings/speech",
        ["audio"] = "settings/audio",
        ["profiles"] = "settings/profiles",
        ["logs"] = "settings/logs",
    };

    /// <summary>Base-relative path with query/fragment stripped and aliases resolved.</summary>
    public static string NormalizePath(string relativePath)
    {
        var path = relativePath;
        var cut = path.IndexOfAny(['?', '#']);
        if (cut >= 0)
        {
            path = path[..cut];
        }

        path = path.Trim('/');
        return Aliases.TryGetValue(path, out var mapped) ? mapped : path;
    }

    /// <summary>The group owning <paramref name="path"/>, or null outside the hub. A group
    /// that lists the path as an item wins over one whose base path merely prefixes it
    /// (three groups share the settings/speech route since issue #199).</summary>
    public static SettingsNavGroup? GroupFor(string path)
        => Groups.FirstOrDefault(group => group.ListsItem(path))
           ?? Groups.FirstOrDefault(group => group.Matches(path));

    /// <summary>The group and item at exactly <paramref name="href"/>, or null.</summary>
    public static (SettingsNavGroup Group, SettingsNavItem Item)? FindItem(string href)
    {
        foreach (var group in Groups)
        {
            foreach (var item in group.Items)
            {
                if (item.Href.Equals(href, StringComparison.OrdinalIgnoreCase))
                {
                    return (group, item);
                }
            }
        }

        return null;
    }

    /// <summary>Resolves a page's <c>{Section?}</c> route value against its section list —
    /// unknown or missing keys land on the first (Status) section.</summary>
    public static string ResolveSection(IReadOnlyList<SettingsNavItem> sections, string? requested)
        => sections.FirstOrDefault(s => s.Key.Equals(requested, StringComparison.OrdinalIgnoreCase))?.Key
           ?? sections[0].Key;
}
