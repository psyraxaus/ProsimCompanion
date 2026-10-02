using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Core.Hosting;

/// <summary>What the host should listen on (ADR-0013): HTTP always, HTTPS when the options ask
/// for it AND the certificate file is usable. <see cref="HttpsProblem"/> is the human line for
/// the config-problem banner when HTTPS was asked for and is not coming up, or is coming up
/// with a warning (expired).</summary>
/// <param name="Host">"localhost" or "0.0.0.0".</param>
/// <param name="HttpPort">The HTTP port, always bound.</param>
/// <param name="HttpsPort">The HTTPS port, or null when HTTPS is off or failed.</param>
/// <param name="Certificate">The loaded certificate for Kestrel, null when HTTPS is off or failed.</param>
/// <param name="HttpsProblem">The reason HTTPS is off or degraded; null when off by choice or fully fine.</param>
/// <param name="HttpsWarningOnly">True when the certificate loaded but is expired / not yet
/// valid: the listener starts anyway and the banner warns.</param>
public sealed record WebListenerPlan(
    string Host,
    int HttpPort,
    int? HttpsPort,
    X509Certificate2? Certificate,
    string? HttpsProblem,
    bool HttpsWarningOnly)
{
    public bool HttpsEnabled => HttpsPort is not null;

    /// <summary>Builds the plan from the options. Pure apart from reading the PFX through
    /// <paramref name="loadCertificate"/> (tests substitute). Never throws: every certificate
    /// failure becomes <see cref="HttpsProblem"/> and HTTP alone.</summary>
    public static WebListenerPlan Build(
        WebUiOptions options,
        Func<string, string, X509Certificate2> loadCertificate,
        DateTimeOffset nowUtc,
        Func<string, bool>? fileExists = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loadCertificate);
        fileExists ??= File.Exists;

        var host = options.BindToAllInterfaces ? "0.0.0.0" : "localhost";
        var https = options.Https;
        if (!https.Enabled)
        {
            return new WebListenerPlan(host, options.Port, null, null, null, false);
        }

        if (https.Port is < 1 or > 65535 || https.Port == options.Port)
        {
            return new WebListenerPlan(host, options.Port, null, null,
                $"HTTPS port {https.Port.ToString(CultureInfo.InvariantCulture)} is not usable (must be 1–65535 and differ from the HTTP port {options.Port.ToString(CultureInfo.InvariantCulture)}) — HTTP only.", false);
        }

        var path = (https.PfxPath ?? "").Trim();
        if (path.Length == 0)
        {
            return new WebListenerPlan(host, options.Port, null, null,
                "HTTPS is enabled but no certificate file (PFX) is set — HTTP only. Settings → Setup → Web Interface.", false);
        }

        if (!fileExists(path))
        {
            return new WebListenerPlan(host, options.Port, null, null,
                $"HTTPS certificate file not found: {path} — HTTP only.", false);
        }

        X509Certificate2 certificate;
        try
        {
            certificate = loadCertificate(path, https.PfxPassword ?? "");
        }
        catch (Exception ex)
        {
            // Wrong password, not a PKCS#12, unreadable: the message from the crypto layer is
            // the diagnosis ("The specified network password is not correct").
            return new WebListenerPlan(host, options.Port, null, null,
                $"HTTPS certificate could not be read ({path}): {ex.Message} — HTTP only.", false);
        }

        if (!certificate.HasPrivateKey)
        {
            certificate.Dispose();
            return new WebListenerPlan(host, options.Port, null, null,
                $"HTTPS certificate has no private key ({path}) — export it as a PFX with the key. HTTP only.", false);
        }

        var notAfter = new DateTimeOffset(certificate.NotAfter.ToUniversalTime());
        var notBefore = new DateTimeOffset(certificate.NotBefore.ToUniversalTime());
        if (nowUtc > notAfter)
        {
            return new WebListenerPlan(host, options.Port, https.Port, certificate,
                $"HTTPS certificate expired on {notAfter:yyyy-MM-dd} — the secure address still answers, but browsers will warn. Renew the certificate.", true);
        }

        if (nowUtc < notBefore)
        {
            return new WebListenerPlan(host, options.Port, https.Port, certificate,
                $"HTTPS certificate is not valid before {notBefore:yyyy-MM-dd} — the secure address still answers, but browsers will warn.", true);
        }

        return new WebListenerPlan(host, options.Port, https.Port, certificate, null, false);
    }

    /// <summary>The production certificate loader: a PKCS#12 file with its password. The key
    /// goes into the current user's CNG key set (the default), NOT an ephemeral one: SChannel
    /// cannot use an EphemeralKeySet key for TLS server authentication and closes every
    /// handshake with no error (found on the 2026-10-03 smoke run — curl and HttpClient saw
    /// "unexpected EOF" while Kestrel said the listener was up). This is a key container, not
    /// the Windows certificate store: nothing is installed or trusted by it.</summary>
    public static X509Certificate2 LoadPfx(string path, string password)
        => X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.UserKeySet);
}
