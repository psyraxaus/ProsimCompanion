namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Outbound event notifications (issue #151), <c>notifications</c>: a short message to a phone
/// or a webhook at the flight's milestones. Off by default; nothing leaves this PC until the
/// master switch is on AND a target exists. Targets are a top-level list (the binder
/// list-append fix covers it); each target's URL and token are DPAPI-protected at rest.
/// </summary>
public sealed class NotificationOptions : IOptionSection
{
    public static string SectionName => "notifications";

    /// <summary>Master switch. Off: no target is ever called, whatever the list says.</summary>
    public bool Enabled { get; set; }

    /// <summary>Where the messages go. Empty by default.</summary>
    public List<NotificationTarget> Targets { get; set; } = [];

    /// <summary>Seconds a target is left alone after a failed send (timeout, 4xx, 5xx, DNS).
    /// Events raised during the cooldown are dropped for that target, not queued.</summary>
    public int FailureCooldownSeconds { get; set; } = 60;

    /// <summary>Per-send HTTP timeout. A slow phone service must never hold the queue.</summary>
    public int SendTimeoutSeconds { get; set; } = 5;

    /// <summary>Minutes of holdover time left at which "deice holdover expiring" fires.</summary>
    public int HoldoverExpiringMinutes { get; set; } = 5;
}

/// <summary>The wire shape a target speaks.</summary>
public enum NotificationTargetKind
{
    /// <summary>A JSON POST with the versioned ProsimCompanion body (docs/integrations/notifications.md).</summary>
    Webhook,

    /// <summary>An ntfy topic URL: plain-text body, Title / Tags / Priority headers.</summary>
    Ntfy,

    /// <summary>A Discord webhook URL: <c>{ "content": … }</c>.</summary>
    Discord,
}

/// <summary>One place to notify. <see cref="Url"/> and <see cref="Token"/> are secrets:
/// never logged (the <see cref="Name"/> and the HTTP status are), redacted in the bundle.</summary>
public sealed class NotificationTarget
{
    /// <summary>Your label for it ("My phone"); what the log and the Notifications page show.</summary>
    public string Name { get; set; } = "";

    public NotificationTargetKind Kind { get; set; } = NotificationTargetKind.Ntfy;

    /// <summary>The ntfy topic URL, the Discord webhook URL, or any https webhook URL.</summary>
    public string Url { get; set; } = "";

    /// <summary>Optional bearer token (ntfy access token, your webhook's secret). Sent as
    /// <c>Authorization: Bearer …</c>.</summary>
    public string Token { get; set; } = "";

    /// <summary>Which milestones this target gets. All on by default.</summary>
    public NotificationEventSwitches Events { get; set; } = new();
}

/// <summary>The ten milestones, one switch each (plain booleans — a nested list default is
/// barred by the option-section guard, and ten named switches read better on the page).</summary>
public sealed class NotificationEventSwitches
{
    public bool RefuelComplete { get; set; } = true;

    public bool BoardingComplete { get; set; } = true;

    public bool FinalLoadsheetSent { get; set; } = true;

    public bool ReadyForPushback { get; set; } = true;

    public bool CabinSecure { get; set; } = true;

    public bool DeiceHoldoverExpiring { get; set; } = true;

    public bool TopOfDescentApproaching { get; set; } = true;

    public bool Landed { get; set; } = true;

    public bool OnBlocks { get; set; } = true;

    public bool DeboardingComplete { get; set; } = true;
}
