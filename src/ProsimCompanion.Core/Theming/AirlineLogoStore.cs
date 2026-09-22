namespace ProsimCompanion.Core.Theming;

/// <summary>
/// The pilot's airline logos keyed by the OFP airline's ICAO code ("KLM", "BAW"), for the
/// pop-out Flight Monitor board (owner request 2026-09-23). A second
/// <see cref="ThemeLogoStore"/> over its own folder
/// (<c>%LOCALAPPDATA%\ProsimCompanion\config\themes\logos\airlines\</c>) — the theme logos
/// are keyed by theme name and share one flat folder, so an airline named like a theme
/// must not collide with it. Same trademark rule: never shipped, the pilot uploads their own.
/// </summary>
public sealed class AirlineLogoStore
{
    public AirlineLogoStore(string directory)
    {
        Files = new ThemeLogoStore(directory);
    }

    /// <summary>The file store (slug = lower-cased ICAO code).</summary>
    public ThemeLogoStore Files { get; }

    /// <summary>Normalises an airline code the way it is stored ("klm"); null when the code
    /// is not a plausible 2–4 character ICAO/IATA airline designator.</summary>
    public static string? Slug(string? airlineCode)
    {
        if (string.IsNullOrWhiteSpace(airlineCode))
        {
            return null;
        }

        var trimmed = airlineCode.Trim();
        if (trimmed.Length is < 2 or > 4 || !trimmed.All(char.IsLetterOrDigit))
        {
            return null;
        }

        return ThemeLogoStore.Slug(trimmed);
    }

    /// <summary>Full path of the logo for <paramref name="airlineCode"/>, or null.</summary>
    public string? FindFile(string? airlineCode)
        => Slug(airlineCode) is { } slug ? Files.FindFileBySlug(slug) : null;
}
