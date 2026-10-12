using System.Globalization;
using Microsoft.Extensions.Localization;
using ProsimCompanion.Core.Localization;

namespace ProsimCompanion.Web.Localization;

/// <summary>
/// The web UI's string source (ADR-0014): a per-circuit <see cref="IStringLocalizer"/> over
/// the JSON language packs. Components inject <c>IStringLocalizer T</c> and write
/// <c>@T["layout.nav.flightStatus"]</c>; the language is resolved on every lookup from the
/// configured code (<see cref="UiLanguageStore"/>) or, when none is set, this browser's own
/// preferences (<see cref="BrowserLanguageContext"/>). Deliberately NOT tied to
/// <see cref="CultureInfo.CurrentUICulture"/>: the app formats every number, weight and time
/// the ICAO way (decimal point, kg, UTC) whatever the language, and a per-circuit culture
/// would silently change <c>double.Parse</c> on the settings pages. Placeholders are
/// <c>{0}</c>-style and formatted invariant.
/// </summary>
public sealed class UiText : IStringLocalizer
{
    private readonly LanguagePackCatalog _catalog;
    private readonly UiLanguageStore _configured;
    private readonly BrowserLanguageContext _browser;

    public UiText(LanguagePackCatalog catalog, UiLanguageStore configured, BrowserLanguageContext browser)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(configured);
        ArgumentNullException.ThrowIfNull(browser);
        _catalog = catalog;
        _configured = configured;
        _browser = browser;
    }

    /// <summary>The language code this circuit renders in right now.</summary>
    public string Language => _catalog.Resolve(_configured.Configured, _browser.Languages);

    /// <inheritdoc />
    public LocalizedString this[string name]
    {
        get
        {
            ArgumentNullException.ThrowIfNull(name);
            var text = _catalog.Lookup(Language, name, out var found);
            return new LocalizedString(name, text, resourceNotFound: !found);
        }
    }

    /// <inheritdoc />
    public LocalizedString this[string name, params object[] arguments]
    {
        get
        {
            ArgumentNullException.ThrowIfNull(name);
            var text = _catalog.Lookup(Language, name, out var found);
            string formatted;
            try
            {
                formatted = string.Format(CultureInfo.InvariantCulture, text, arguments);
            }
            catch (FormatException)
            {
                // A translator broke a placeholder — show the raw text rather than throw on
                // every render of the page; the guard test catches it before release.
                formatted = text;
            }

            return new LocalizedString(name, formatted, resourceNotFound: !found);
        }
    }

    /// <inheritdoc />
    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures)
    {
        var pack = _catalog.Find(Language) ?? _catalog.Find(LanguagePackCatalog.DefaultCode);
        if (pack is null)
        {
            yield break;
        }

        foreach (var (textId, value) in pack.Strings)
        {
            yield return new LocalizedString(textId, value);
        }
    }
}
