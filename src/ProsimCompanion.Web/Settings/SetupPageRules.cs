using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Web.Settings;

/// <summary>
/// The Setup page's own rules (candidate C3): the one-way wake-on-LAN migration. Per-property
/// trims (host, SDK path, DFD path, the wake rows) are on the properties; this is what is left.
/// </summary>
public static class SetupPageRules
{
    /// <summary>One-way migration of <c>briefing.llmWakeOnLan</c> into the targets list
    /// (2026-10-08): an enabled legacy block with a MAC that no row carries becomes a row,
    /// and the legacy switch goes off. Both drafts dirty, so the bar prompts for the save
    /// that persists it; until then the startup module still honours the legacy block.
    /// The row's name is a settings.json value the log echoes, so it stays English.</summary>
    public static void MigrateLegacyWake(BriefingOptions briefing, WakeTargetsOptions wake)
    {
        ArgumentNullException.ThrowIfNull(briefing);
        ArgumentNullException.ThrowIfNull(wake);
        var legacy = briefing.LlmWakeOnLan;
        if (!legacy.Enabled || string.IsNullOrWhiteSpace(legacy.MacAddress))
        {
            return;
        }

        var known = wake.Targets.Any(t =>
            string.Equals(NormalizeMac(t.MacAddress), NormalizeMac(legacy.MacAddress), StringComparison.OrdinalIgnoreCase));
        if (!known)
        {
            wake.Targets.Add(new WakeTarget
            {
                Name = "Language-model PC",
                Enabled = true,
                MacAddress = legacy.MacAddress.Trim(),
                BroadcastAddress = legacy.BroadcastAddress,
                Port = legacy.Port,
            });
        }

        legacy.Enabled = false;
    }

    /// <summary>A MAC with its separators and spaces removed, for comparing two spellings.</summary>
    public static string NormalizeMac(string mac)
    {
        ArgumentNullException.ThrowIfNull(mac);
        return mac.Replace(":", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).Trim();
    }
}
