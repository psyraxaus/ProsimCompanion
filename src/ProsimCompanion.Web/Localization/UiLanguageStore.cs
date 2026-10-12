using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Web.Localization;

/// <summary>
/// The configured UI language (<c>webUi.language</c>) as a snapshot store, so every open
/// browser — the sim PC and the tablet — re-renders the instant the pilot picks a language
/// on Settings → Appearance, instead of waiting for the settings-file watcher to feed the
/// options monitor back (the same shape as <see cref="Components.AdvancedSettingsStore"/>).
/// An empty value means "follow the browser" (ADR-0014).
/// </summary>
public sealed class UiLanguageStore : SnapshotStore<string>
{
    private readonly SettingsWriter _writer;

    public UiLanguageStore(IOptionsMonitor<WebUiOptions> options, SettingsWriter writer)
        : base(Seed(options), StringComparer.OrdinalIgnoreCase)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
    }

    private static string Seed(IOptionsMonitor<WebUiOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.CurrentValue.Language ?? "";
    }

    /// <summary>The configured language code, or empty for "follow the browser".</summary>
    public string Configured => Snapshot();

    /// <summary>Persists the choice at once (a view preference, no dirty bar) and notifies
    /// every component that watches the store.</summary>
    public void Set(string? languageCode)
    {
        var code = (languageCode ?? "").Trim();
        _writer.Set<WebUiOptions, string>(o => o.Language, code);
        Update(_ => code);
    }
}
