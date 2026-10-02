using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ProsimCompanion.App.Hosting;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Hosting;
using ProsimCompanion.Core.State;
using Xunit;

namespace ProsimCompanion.Core.Tests.App;

/// <summary>Issue #150 / ADR-0013: the listener plan — HTTP always, HTTPS only with a usable
/// certificate, every failure a banner line and HTTP alone — plus the token middleware's
/// install-asset allow-list and the secret registration.</summary>
public sealed class WebListenerPlanTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A self-signed certificate with a private key, valid over the given window.</summary>
    private static X509Certificate2 SelfSigned(DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=simpc.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(notBefore, notAfter);
    }

    private static WebUiOptions Options(bool https = true, string path = @"C:\certs\simpc.pfx", int port = 5321)
        => new()
        {
            Port = 5320,
            BindToAllInterfaces = true,
            Https = new HttpsOptions { Enabled = https, PfxPath = path, PfxPassword = "secret", Port = port },
        };

    [Fact]
    public void HttpsOff_IsHttpOnly_WithNoProblem()
    {
        var plan = WebListenerPlan.Build(Options(https: false), (_, _) => throw new InvalidOperationException("never"), Now);

        Assert.Equal("0.0.0.0", plan.Host);
        Assert.Equal(5320, plan.HttpPort);
        Assert.Null(plan.HttpsPort);
        Assert.Null(plan.Certificate);
        Assert.Null(plan.HttpsProblem);
        Assert.False(plan.HttpsEnabled);
    }

    [Fact]
    public void Loopback_WhenLanIsOff()
    {
        var options = Options(https: false);
        options.BindToAllInterfaces = false;

        Assert.Equal("localhost", WebListenerPlan.Build(options, (_, _) => throw new InvalidOperationException(), Now).Host);
    }

    [Fact]
    public void ValidCertificate_AddsTheHttpsEndpoint()
    {
        using var cert = SelfSigned(Now.AddDays(-1), Now.AddDays(365));
        string? askedPath = null;
        string? askedPassword = null;

        var plan = WebListenerPlan.Build(Options(), (p, pw) => { askedPath = p; askedPassword = pw; return cert; }, Now, _ => true);

        Assert.Equal(5321, plan.HttpsPort);
        Assert.Same(cert, plan.Certificate);
        Assert.Null(plan.HttpsProblem);
        Assert.False(plan.HttpsWarningOnly);
        Assert.True(plan.HttpsEnabled);
        Assert.Equal(@"C:\certs\simpc.pfx", askedPath);
        Assert.Equal("secret", askedPassword);
    }

    [Fact]
    public void MissingFile_EmptyPath_BadPort_AreHttpOnly_WithTheReason()
    {
        var missing = WebListenerPlan.Build(Options(), (_, _) => throw new InvalidOperationException("never"), Now, _ => false);
        var empty = WebListenerPlan.Build(Options(path: " "), (_, _) => throw new InvalidOperationException("never"), Now, _ => true);
        var samePort = WebListenerPlan.Build(Options(port: 5320), (_, _) => throw new InvalidOperationException("never"), Now, _ => true);
        var zeroPort = WebListenerPlan.Build(Options(port: 0), (_, _) => throw new InvalidOperationException("never"), Now, _ => true);

        foreach (var plan in new[] { missing, empty, samePort, zeroPort })
        {
            Assert.Null(plan.HttpsPort);
            Assert.Null(plan.Certificate);
            Assert.Equal(5320, plan.HttpPort);
            Assert.Contains("HTTP only", plan.HttpsProblem, StringComparison.Ordinal);
        }

        Assert.Contains(@"not found: C:\certs\simpc.pfx", missing.HttpsProblem, StringComparison.Ordinal);
        Assert.Contains("no certificate file", empty.HttpsProblem, StringComparison.Ordinal);
        Assert.Contains("differ from the HTTP port", samePort.HttpsProblem, StringComparison.Ordinal);
    }

    [Fact]
    public void UnreadableCertificate_WrongPassword_IsHttpOnly_WithTheCryptoMessage()
    {
        var plan = WebListenerPlan.Build(
            Options(), (_, _) => throw new CryptographicException("The specified network password is not correct."), Now, _ => true);

        Assert.Null(plan.HttpsPort);
        Assert.Contains("could not be read", plan.HttpsProblem, StringComparison.Ordinal);
        Assert.Contains("network password is not correct", plan.HttpsProblem, StringComparison.Ordinal);
    }

    [Fact]
    public void CertificateWithoutAPrivateKey_IsHttpOnly()
    {
        using var full = SelfSigned(Now.AddDays(-1), Now.AddDays(30));
        var publicOnly = X509CertificateLoader.LoadCertificate(full.Export(X509ContentType.Cert));

        var plan = WebListenerPlan.Build(Options(), (_, _) => publicOnly, Now, _ => true);

        Assert.Null(plan.HttpsPort);
        Assert.Contains("no private key", plan.HttpsProblem, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpiredCertificate_StillListens_WithAWarning()
    {
        // Owner decision (ADR-0013 point 4): the file is valid, the browser will warn.
        using var cert = SelfSigned(Now.AddDays(-400), Now.AddDays(-30));

        var plan = WebListenerPlan.Build(Options(), (_, _) => cert, Now, _ => true);

        Assert.Equal(5321, plan.HttpsPort);
        Assert.Same(cert, plan.Certificate);
        Assert.True(plan.HttpsWarningOnly);
        Assert.Contains("expired on " + Now.AddDays(-30).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), plan.HttpsProblem, StringComparison.Ordinal);
        Assert.Contains("browsers will warn", plan.HttpsProblem, StringComparison.Ordinal);
    }

    [Fact]
    public void NotYetValidCertificate_StillListens_WithAWarning()
    {
        using var cert = SelfSigned(Now.AddDays(10), Now.AddDays(400));

        var plan = WebListenerPlan.Build(Options(), (_, _) => cert, Now, _ => true);

        Assert.Equal(5321, plan.HttpsPort);
        Assert.True(plan.HttpsWarningOnly);
        Assert.Contains("not valid before", plan.HttpsProblem, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadPfx_RoundTrips_AnExportedCertificate()
    {
        using var cert = SelfSigned(Now.AddDays(-1), Now.AddDays(30));
        var path = Path.Combine(Path.GetTempPath(), $"pc-test-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pkcs12, "pw"));
        try
        {
            using var loaded = WebListenerPlan.LoadPfx(path, "pw");
            Assert.True(loaded.HasPrivateKey);
            Assert.Equal(cert.Thumbprint, loaded.Thumbprint);
            Assert.ThrowsAny<CryptographicException>(() => WebListenerPlan.LoadPfx(path, "wrong"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- the status store and the QR preference ------------------------------------------

    [Fact]
    public void Snapshot_PrefersHttps_ForTheQr_OnlyWhenUp()
    {
        var up = new WebListenerSnapshot("http://192.168.1.20:5320", "https://192.168.1.20:5321", true, null, false);
        var failed = new WebListenerSnapshot("http://192.168.1.20:5320", null, true, "certificate file not found", false);

        Assert.Equal("https://192.168.1.20:5321", up.PreferredUrl);
        Assert.True(up.HttpsUp);
        Assert.Equal("http://192.168.1.20:5320", failed.PreferredUrl);
        Assert.False(failed.HttpsUp);
        Assert.Equal("", WebListenerSnapshot.Empty.PreferredUrl);
    }

    // ---- the token middleware's install-asset allow-list ---------------------------------

    [Theory]
    [InlineData("/manifest.webmanifest", true)]
    [InlineData("/favicon.svg", true)]
    [InlineData("/apple-touch-icon.png", true)]
    [InlineData("/apple-touch-icon-180.png", true)]
    [InlineData("/icons/icon-192.png", true)]
    [InlineData("/icons/icon-512.png", true)]
    [InlineData("/", false)]
    [InlineData("/settings", false)]
    [InlineData("/api/status", false)]
    [InlineData("/api/telemetry/sessions", false)]
    [InlineData("/js/app.js", false)]
    [InlineData("/css/app.css", false)]
    [InlineData("/icons/../api/status", false)]
    [InlineData("/icons/sub/icon.png", false)]
    [InlineData("/icons/icon.svg", false)]
    [InlineData("/manifest.webmanifest.bak", false)]
    public void PublicInstallAssets_AreExactlyTheManifestAndIcons(string path, bool expected)
        => Assert.Equal(expected, LanTokenMiddleware.IsPublicInstallAsset(new Microsoft.AspNetCore.Http.PathString(path)));

    [Fact]
    public void PfxPassword_IsARegisteredSecret_AndHttpsIsOffByDefault()
    {
        Assert.Contains("webUi:https:pfxPassword", SecretProtector.SecretPaths);
        var defaults = new WebUiOptions();
        Assert.False(defaults.Https.Enabled);
        Assert.Equal(5321, defaults.Https.Port);
        Assert.Equal("", defaults.Https.PfxPath);
    }
}
