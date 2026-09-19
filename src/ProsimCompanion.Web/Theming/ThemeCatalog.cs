using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ProsimCompanion.Web.Theming;

/// <summary>
/// The available web UI themes: the built-ins embedded in this assembly plus any user theme
/// JSON files dropped into <c>config/themes</c> (Prosim2GSX theme format — predecessor themes
/// work unchanged). The user directory is re-scanned on every listing/lookup so new files are
/// picked up without a restart; an unparseable file is logged and skipped, never fatal.
/// </summary>
public sealed class ThemeCatalog
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// The theme used when none is configured or the configured name is unknown. Since the
    /// 2026-09-20 restyle (ADR-0011) this is the KLM navy palette from the Superdesign canvas;
    /// the pre-restyle name "Default" is kept as an alias so existing settings files and
    /// theme files that reference it keep resolving.
    /// </summary>
    public const string DefaultThemeName = "KLM Royal Dutch";

    private const string LegacyDefaultAlias = "Default";

    // Presentation order for the built-ins (List() appends user themes alphabetically after):
    // the default first, then the airlines as the canvas listed them, then the two basics.
    private static readonly string[] BuiltInOrder =
    [
        DefaultThemeName, "Lufthansa", "Swiss International", "British Airways", "Air France",
        "Singapore Airlines", "Emirates", "Qatar Airways", "United Airlines", "Qantas", "Finnair",
        "Light", "Dark",
    ];

    private readonly Dictionary<string, ThemeDefinition> _builtIns;
    private readonly string _userThemesDirectory;
    private readonly ILogger<ThemeCatalog> _logger;

    public ThemeCatalog(string userThemesDirectory, ILogger<ThemeCatalog> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userThemesDirectory);
        ArgumentNullException.ThrowIfNull(logger);
        _userThemesDirectory = userThemesDirectory;
        _logger = logger;
        _builtIns = LoadBuiltIns();
    }

    /// <summary>Theme names in presentation order: built-ins first, then user themes.</summary>
    public IReadOnlyList<string> ListNames()
    {
        var names = new List<string>(BuiltInOrder.Where(_builtIns.ContainsKey));
        names.AddRange(LoadUserThemes().Keys
            .Where(name => !_builtIns.ContainsKey(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
        return names;
    }

    /// <summary>
    /// Resolves a theme by name (case-insensitive; user themes shadow built-ins except the
    /// default). Unknown names fall back to <see cref="DefaultThemeName"/> so a stale setting
    /// can never blank the UI; the legacy name "Default" resolves there silently.
    /// </summary>
    public ThemeDefinition Get(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name)
            && !name.Equals(LegacyDefaultAlias, StringComparison.OrdinalIgnoreCase))
        {
            if (!name.Equals(DefaultThemeName, StringComparison.OrdinalIgnoreCase)
                && LoadUserThemes().TryGetValue(name, out var user))
            {
                return user;
            }

            if (_builtIns.TryGetValue(name, out var builtIn))
            {
                return builtIn;
            }

            _logger.LogWarning("Theme {Theme} not found; using {Default}", name, DefaultThemeName);
        }

        return _builtIns[DefaultThemeName];
    }

    private static Dictionary<string, ThemeDefinition> LoadBuiltIns()
    {
        var themes = new Dictionary<string, ThemeDefinition>(StringComparer.OrdinalIgnoreCase);
        var assembly = typeof(ThemeCatalog).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.Contains(".Themes.", StringComparison.Ordinal)
                || !resource.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resource)!;
            var theme = JsonSerializer.Deserialize<ThemeDefinition>(stream, ReadOptions);
            if (theme is not null && !string.IsNullOrWhiteSpace(theme.Name))
            {
                themes[theme.Name] = theme;
            }
        }

        return themes;
    }

    private Dictionary<string, ThemeDefinition> LoadUserThemes()
    {
        var themes = new Dictionary<string, ThemeDefinition>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(_userThemesDirectory))
        {
            return themes;
        }

        foreach (var file in Directory.EnumerateFiles(_userThemesDirectory, "*.json"))
        {
            try
            {
                var theme = JsonSerializer.Deserialize<ThemeDefinition>(
                    File.ReadAllText(file), ReadOptions);
                if (theme is not null)
                {
                    if (string.IsNullOrWhiteSpace(theme.Name))
                    {
                        theme.Name = Path.GetFileNameWithoutExtension(file);
                    }

                    themes[theme.Name] = theme;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                _logger.LogWarning(ex, "Skipping unreadable theme file {File}", file);
            }
        }

        return themes;
    }
}
