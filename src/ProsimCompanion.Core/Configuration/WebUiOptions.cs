namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Settings for the embedded web UI host. These are the only settings also surfaced in the WPF
/// shell, so a broken web configuration can never lock the user out (ADR-0001).
/// </summary>
public sealed class WebUiOptions : IOptionSection
{
    public static string SectionName => "webUi";

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

    /// <summary>Animate the split-flap (Solari) header displays; off snaps characters
    /// instantly (Prosim2GSX's SolariAnimationEnabled).</summary>
    public bool SolariAnimation { get; set; } = true;

    /// <summary>Weight unit source: "app" (the fixed <see cref="Unit"/> below) or "aircraft"
    /// (follow ProSim's configured weight unit) — Prosim2GSX's DisplayUnitSource.</summary>
    public string UnitSource { get; set; } = "app";

    /// <summary>Default weight display unit, "kg" or "lb" (Prosim2GSX's DisplayUnitDefault).
    /// Storage and all protocols stay kg — this affects display only.</summary>
    public string Unit { get; set; } = "kg";

    /// <summary>
    /// Bearer token required from non-loopback clients (delivered via the QR code / onboarding
    /// link, then held in a cookie). Generated automatically on first start; loopback requests
    /// never need it, so the local UI can never be locked out.
    /// </summary>
    public string? AccessToken { get; set; }

    /// <summary>
    /// Web UI theme: a built-in name (Default, Dark, Light, Delta, Finnair, Lufthansa, Qantas)
    /// or the name of a user theme JSON in <c>config/themes</c> (Prosim2GSX theme format).
    /// Unknown names fall back to Default.
    /// </summary>
    public string Theme { get; set; } = "Default";
}
