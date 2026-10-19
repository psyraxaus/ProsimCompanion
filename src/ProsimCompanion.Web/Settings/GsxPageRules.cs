using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Web.Settings;

/// <summary>
/// The Ground Services page's own save rules (candidate C3): the list editors prune and tidy
/// their rows — a per-property trim is on the property's <see cref="OptionFieldAttribute"/>,
/// never here. Plain C# so xunit reaches it.
/// </summary>
public static class GsxPageRules
{
    /// <summary>Blank service rows never persist; operator keywords save trimmed; company hubs
    /// save trimmed and upper-cased (ICAO prefixes). <c>fuelFobSaved</c> is never edited here —
    /// the diff save leaves the app-maintained key alone.</summary>
    public static void BeforeSave(GsxOptions value)
    {
        ArgumentNullException.ThrowIfNull(value);
        value.DepartureServices.RemoveAll(s => string.IsNullOrWhiteSpace(s.Service));
        value.OperatorPreferences = value.OperatorPreferences
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .ToList();
        value.CompanyHubs = value.CompanyHubs
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h.Trim().ToUpperInvariant())
            .ToList();
    }

    /// <summary>pcaMode falls back to the legacy autoPca bool while unset — the select shows
    /// the effective value and writes the tri-state key from then on.</summary>
    public static string EffectivePcaMode(GsxOptions value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return !string.IsNullOrWhiteSpace(value.PcaMode)
            ? value.PcaMode
            : value.AutoPca ? "always" : "never";
    }

    /// <summary>"folder; folder" ↔ the scenery package folder list; quotes from Explorer's
    /// "Copy as path" are stripped per entry.</summary>
    public static string SceneryFoldersText(GsxOptions value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return string.Join("; ", value.SceneryPackageFolders);
    }

    /// <summary>Writes the ';'-separated folder text back into the list.</summary>
    public static void SetSceneryFolders(GsxOptions value, string? text)
    {
        ArgumentNullException.ThrowIfNull(value);
        value.SceneryPackageFolders = (text ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(f => f.Trim('"'))
            .Where(f => f.Length > 0)
            .ToList();
    }
}
