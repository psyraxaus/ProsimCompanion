namespace ProsimCompanion.Core.State;

/// <summary>What the web host actually listens on (ADR-0013), published once the server is up:
/// the WPF shell's QR and status line and the Setup page's HTTPS strip read it. Null HTTPS url
/// = off or failed; <see cref="HttpsProblem"/> says which.</summary>
/// <param name="HttpUrl">The HTTP address the QR / hint shows (LAN address when bound to all interfaces).</param>
/// <param name="HttpsUrl">The HTTPS address when that listener is up.</param>
/// <param name="HttpsEnabled">The option value — on, even when the listener failed.</param>
/// <param name="HttpsProblem">The banner line (missing file, bad password, expired…), null when fine or off.</param>
/// <param name="HttpsWarningOnly">True when HTTPS is up but the certificate is outside its validity.</param>
public sealed record WebListenerSnapshot(
    string HttpUrl,
    string? HttpsUrl,
    bool HttpsEnabled,
    string? HttpsProblem,
    bool HttpsWarningOnly)
{
    public static WebListenerSnapshot Empty { get; } = new("", null, false, null, false);

    public bool HttpsUp => HttpsUrl is not null;

    /// <summary>The address to put in the QR code: HTTPS when it is up (owner decision,
    /// ADR-0013 point 5), otherwise HTTP.</summary>
    public string PreferredUrl => HttpsUrl ?? HttpUrl;
}

public sealed class WebListenerStatus : SnapshotStore<WebListenerSnapshot>
{
    public WebListenerStatus()
        : base(WebListenerSnapshot.Empty)
    {
    }

    public void Set(WebListenerSnapshot snapshot) => Update(_ => snapshot);
}
