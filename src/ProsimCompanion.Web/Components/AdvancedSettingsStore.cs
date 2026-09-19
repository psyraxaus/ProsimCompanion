using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Web.Components;

/// <summary>
/// The "Show advanced settings" switch (ADR-0010) as a snapshot store, so every settings
/// component re-renders the instant it flips instead of waiting for the settings-file
/// watcher to feed the options monitor back. The value is persisted through
/// <c>webUi.showAdvancedSettings</c> like any other option; this store is the live mirror.
/// </summary>
public sealed class AdvancedSettingsStore : SnapshotStore<bool>
{
    private readonly SettingsWriter _writer;

    public AdvancedSettingsStore(IOptionsMonitor<WebUiOptions> options, SettingsWriter writer)
        : base(Seed(options))
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
    }

    private static bool Seed(IOptionsMonitor<WebUiOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.CurrentValue.ShowAdvancedSettings;
    }

    /// <summary>True while advanced fields are revealed.</summary>
    public bool ShowAdvanced => Snapshot();

    /// <summary>Flips the switch: persists immediately (no dirty bar — it is a view
    /// preference, not aircraft behaviour) and notifies every open settings component.</summary>
    public void Set(bool showAdvanced)
    {
        _writer.Set<WebUiOptions, bool>(o => o.ShowAdvancedSettings, showAdvanced);
        Update(_ => showAdvanced);
    }
}
