namespace ProsimCompanion.Web.Layout;

/// <summary>One entry of the settings rail: a page (or a section of a page) at a URL.</summary>
public sealed record SettingsNavItem(string Key, string Label, string Href);

/// <summary>
/// One group of the settings rail. A group with items expands to show them while it is
/// active; a group without items is a single page.
/// </summary>
public sealed record SettingsNavGroup(string Key, string Label, string Href, IReadOnlyList<SettingsNavItem> Items)
{
    /// <summary>True when <paramref name="path"/> (base-relative, no query) is this group or
    /// one of its items. Only a group WITH items owns its sub-paths — the single-page Setup
    /// group at "settings" must not swallow "settings/gsx/…".</summary>
    public bool Matches(string path)
        => path.Equals(Href, StringComparison.OrdinalIgnoreCase)
           || Items.Any(item => item.Href.Equals(path, StringComparison.OrdinalIgnoreCase))
           || (Items.Count > 0 && path.StartsWith(Href + "/", StringComparison.OrdinalIgnoreCase));

    /// <summary>The active item for <paramref name="path"/>: an exact href match, else the
    /// first item (the group's landing section).</summary>
    public SettingsNavItem? ActiveItem(string path)
        => Items.FirstOrDefault(item => item.Href.Equals(path, StringComparison.OrdinalIgnoreCase))
           ?? Items.FirstOrDefault();
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
        => [.. sections.Select(s => new SettingsNavItem(s.Key, s.Label, $"{basePath}/{s.Key}"))];

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

    public static readonly SettingsNavItem[] SpeechSections = Items("settings/speech",
        ("status", "Status"),
        ("general", "General"),
        ("listening", "Listening & PTT"),
        ("sterile", "Sterile Cockpit"),
        ("crew", "Crew Voices"),
        ("groundCrew", "Ground Crew"),
        ("cabin", "Cabin Crew"),
        ("briefing", "Briefings & LLM"),
        ("mcdu", "MCDU"),
        ("sayIntentions", "SayIntentions"),
        ("providers", "Voice Providers"));

    public static readonly SettingsNavItem[] AudioSections = Items("settings/audio",
        ("status", "Status"),
        ("general", "General"),
        ("coreaudio", "CoreAudio"),
        ("voicemeeter", "VoiceMeeter"),
        ("advanced", "Housekeeping"));

    public static readonly SettingsNavGroup[] Groups =
    [
        new("setup", "Setup", "settings", []),
        new("gsx", "Ground Services", "settings/gsx", GsxSections),
        new("speech", "Voice First Officer", "settings/speech", SpeechSections),
        new("audio", "Audio Control", "settings/audio", AudioSections),
        new("app", "Display & Flight Data", "settings/app", []),
        new("profiles", "Aircraft Profiles", "settings/profiles", []),
        new("advanced", "Advanced", "settings/advanced",
        [
            new("flightPhase", "Flight Phase Engine", "settings/flight-phase"),
            new("logs", "Logs", "settings/logs"),
        ]),
    ];

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

    /// <summary>The group owning <paramref name="path"/>, or null outside the hub.</summary>
    public static SettingsNavGroup? GroupFor(string path)
        => Groups.FirstOrDefault(group => group.Matches(path));

    /// <summary>Resolves a page's <c>{Section?}</c> route value against its section list —
    /// unknown or missing keys land on the first (Status) section.</summary>
    public static string ResolveSection(IReadOnlyList<SettingsNavItem> sections, string? requested)
        => sections.FirstOrDefault(s => s.Key.Equals(requested, StringComparison.OrdinalIgnoreCase))?.Key
           ?? sections[0].Key;
}
