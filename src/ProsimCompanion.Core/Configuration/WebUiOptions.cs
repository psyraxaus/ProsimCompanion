namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Settings for the embedded web UI host. These are the only settings also surfaced in the WPF
/// shell, so a broken web configuration can never lock the user out (ADR-0001).
/// </summary>
public sealed class WebUiOptions
{
    public const string SectionName = "webUi";

    /// <summary>
    /// HTTP port for the embedded Kestrel host. 5320 avoids the neighbours: ProSim's EFB gateway
    /// (5000), the legacy Prosim2GSX web EFB (5001) and Prosim2FO dashboard (8730).
    /// </summary>
    public int Port { get; set; } = 5320;

    /// <summary>
    /// When false (default) the server binds loopback only; when true it binds all interfaces so
    /// other LAN devices (tablets, second PC) can reach the UI.
    /// </summary>
    public bool BindToAllInterfaces { get; set; }
}
