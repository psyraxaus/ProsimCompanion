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
}
