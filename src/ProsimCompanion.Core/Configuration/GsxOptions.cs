namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Settings for GSX Pro integration. The Couatl Remote API port is intentionally NOT configurable
/// here — it is always read from CouatlAddons.ini (see docs/integrations/gsx.md).
/// </summary>
public sealed class GsxOptions
{
    public const string SectionName = "gsx";

    /// <summary>Master switch for the GSX ground automation pillar.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Delay between Remote API reconnect attempts.</summary>
    public int ReconnectIntervalMs { get; set; } = 5000;

    /// <summary>How long to await a command result before synthesizing a timeout.</summary>
    public int CommandTimeoutMs { get; set; } = 10_000;

    /// <summary>How long to wait for a menu to appear (menuShown + matching title).</summary>
    public int MenuOpenTimeoutMs { get; set; } = 5000;

    /// <summary>Default budget for verifying a menu pick's observable effect (per-intent
    /// overridable — the reposition submenu is known to exceed 5 s).</summary>
    public int IntentVerifyTimeoutMs { get; set; } = 5000;
}
