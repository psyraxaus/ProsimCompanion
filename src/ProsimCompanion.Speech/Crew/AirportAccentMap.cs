namespace ProsimCompanion.Speech.Crew;

/// <summary>
/// Pure ICAO-prefix → Chirp 3 HD locale map (issue #53). Longest prefix wins — user
/// overrides first, then the built-in two-letter regions, then the one-letter continents
/// (so Ukraine's "UK" beats Russia's "U"). Only countries with a usable Chirp 3 HD locale
/// are listed; an unmapped airport returns null and the configured ground voice plays.
/// The mapping is deliberately coarse (accent flavour, not linguistics): countries without
/// their own locale borrow the nearest available one (Ireland → en-GB, Portugal → pt-BR).
/// </summary>
public static class AirportAccentMap
{
    private static readonly Dictionary<string, string> Prefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        // ---- Two-letter regions ----
        ["EG"] = "en-GB", // United Kingdom
        ["EI"] = "en-GB", // Ireland (no en-IE)
        ["LF"] = "fr-FR", // France
        ["ED"] = "de-DE", // Germany (civil)
        ["ET"] = "de-DE", // Germany (military)
        ["LO"] = "de-DE", // Austria
        ["LS"] = "de-DE", // Switzerland (coarse — override per-airport for Romandie)
        ["LI"] = "it-IT", // Italy
        ["LE"] = "es-ES", // Spain
        ["LP"] = "pt-BR", // Portugal (no pt-PT in Chirp 3 HD)
        ["EH"] = "nl-NL", // Netherlands
        ["EB"] = "nl-BE", // Belgium
        ["EK"] = "da-DK", // Denmark
        ["EN"] = "nb-NO", // Norway
        ["ES"] = "sv-SE", // Sweden
        ["EF"] = "fi-FI", // Finland
        ["EE"] = "et-EE", // Estonia
        ["EV"] = "lv-LV", // Latvia
        ["EY"] = "lt-LT", // Lithuania
        ["EP"] = "pl-PL", // Poland
        ["LK"] = "cs-CZ", // Czechia
        ["LZ"] = "sk-SK", // Slovakia
        ["LH"] = "hu-HU", // Hungary
        ["LR"] = "ro-RO", // Romania
        ["LB"] = "bg-BG", // Bulgaria
        ["LD"] = "hr-HR", // Croatia
        ["LJ"] = "sl-SI", // Slovenia
        ["LY"] = "sr-RS", // Serbia/Montenegro
        ["LG"] = "el-GR", // Greece
        ["LT"] = "tr-TR", // Türkiye
        ["LL"] = "he-IL", // Israel
        ["UK"] = "uk-UA", // Ukraine — must beat the U (Russia) continent entry
        ["OM"] = "ar-XA", // UAE
        ["OE"] = "ar-XA", // Saudi Arabia
        ["OB"] = "ar-XA", // Bahrain
        ["OK"] = "ar-XA", // Kuwait
        ["OT"] = "ar-XA", // Qatar
        ["OJ"] = "ar-XA", // Jordan
        ["OO"] = "ar-XA", // Oman
        ["VT"] = "th-TH", // Thailand
        ["VH"] = "yue-HK", // Hong Kong
        ["VV"] = "vi-VN", // Vietnam
        ["WI"] = "id-ID", // Indonesia (west)
        ["WA"] = "id-ID", // Indonesia (east)
        ["RJ"] = "ja-JP", // Japan
        ["RO"] = "ja-JP", // Japan (Okinawa)
        ["RK"] = "ko-KR", // South Korea
        ["VA"] = "en-IN", // India (west)
        ["VE"] = "en-IN", // India (east)
        ["VI"] = "en-IN", // India (north)
        ["VO"] = "en-IN", // India (south)
        ["NZ"] = "en-AU", // New Zealand (no en-NZ; nearest accent)
        ["SB"] = "pt-BR", // Brazil
        ["SD"] = "pt-BR",
        ["SI"] = "pt-BR",
        ["SJ"] = "pt-BR",
        ["SN"] = "pt-BR",
        ["SS"] = "pt-BR",
        ["SW"] = "pt-BR",
        ["MM"] = "es-US", // Mexico
        ["FA"] = "en-GB", // South Africa (no en-ZA; nearest accent)

        // ---- One-letter continents (checked after the two-letter table) ----
        ["K"] = "en-US", // Contiguous United States
        ["C"] = "en-US", // Canada
        ["Y"] = "en-AU", // Australia
        ["U"] = "ru-RU", // Russia/CIS
        ["Z"] = "cmn-CN", // Mainland China
        ["S"] = "es-US", // South America (non-Brazil)
    };

    /// <summary>Locale for an airport, or null when unmapped/invalid. <paramref name="overrides"/>
    /// (user config) is consulted first, longest matching prefix wins overall.</summary>
    public static string? LocaleFor(string? icao, IReadOnlyDictionary<string, string>? overrides = null)
    {
        if (string.IsNullOrWhiteSpace(icao))
        {
            return null;
        }

        var code = icao.Trim();
        for (var length = code.Length; length >= 1; length--)
        {
            var prefix = code[..length];
            if (overrides is not null && overrides.TryGetValue(prefix, out var custom)
                && !string.IsNullOrWhiteSpace(custom))
            {
                return custom;
            }

            if (Prefixes.TryGetValue(prefix, out var locale))
            {
                return locale;
            }
        }

        return null;
    }
}
