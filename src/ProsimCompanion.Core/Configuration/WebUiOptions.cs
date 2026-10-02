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

    /// <summary>Reveal the advanced (tuning) fields on the settings pages — timeouts,
    /// intervals, thresholds (ADR-0010). Off by default so a new user meets the everyday
    /// choices only; the controls still exist, they are folded.</summary>
    public bool ShowAdvancedSettings { get; set; }

    /// <summary>Show the NEXT SERVICE button in the EFB header on every page (issue #133,
    /// Nico's home-cockpit request 2026-09-27): the INT/RAD "smart button" for pilots who
    /// do not use the switch, without switching to Settings → Ground Services. Off by
    /// default so the header stays as it was for everyone else; the toggle lives on
    /// Settings → Appearance.</summary>
    public bool ShowNextServiceInHeader { get; set; }

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
    /// Web UI theme: a built-in name (KLM Royal Dutch, Lufthansa, Swiss International,
    /// British Airways, Air France, Singapore Airlines, Emirates, Qatar Airways, United
    /// Airlines, Qantas, Finnair, Light, Dark — the 13 from the 2026-09-20 Superdesign
    /// restyle) or the name of a user theme JSON in <c>config/themes</c> (Prosim2GSX theme
    /// format). Unknown names — and the pre-restyle name "Default" — fall back to KLM Royal
    /// Dutch.
    /// </summary>
    public string Theme { get; set; } = "KLM Royal Dutch";

    /// <summary>The optional HTTPS listener beside HTTP (ADR-0013). Lives here and on the web
    /// Setup page, not in the desktop window: a broken HTTPS setting cannot lock anyone out,
    /// because HTTP keeps serving on its own port.</summary>
    public HttpsOptions Https { get; set; } = new();
}

/// <summary>
/// The second, secure Kestrel endpoint (ADR-0013). Off by default. The certificate is the
/// user's own PKCS#12 file; the app never generates, requests or installs one. Takes effect on
/// restart, like the port.
/// </summary>
public sealed class HttpsOptions
{
    public bool Enabled { get; set; }

    /// <summary>HTTPS port — 5321 by default, beside HTTP's 5320.</summary>
    public int Port { get; set; } = 5321;

    /// <summary>Full path of the PKCS#12 (.pfx / .p12) file holding the certificate and its
    /// private key. Its subject alternative names must cover the address the tablet uses (the
    /// LAN IP by default).</summary>
    public string PfxPath { get; set; } = "";

    /// <summary>The PFX password; stored DPAPI-protected (<c>dpapi:…</c>) like every other secret.</summary>
    public string PfxPassword { get; set; } = "";
}
