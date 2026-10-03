using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Core.Notifications;

/// <summary>The ten milestones a target can be told about (issue #151).</summary>
public enum FlightEvent
{
    RefuelComplete,
    BoardingComplete,
    FinalLoadsheetSent,
    ReadyForPushback,
    CabinSecure,
    DeiceHoldoverExpiring,
    TopOfDescentApproaching,
    Landed,
    OnBlocks,
    DeboardingComplete,
    /// <summary>The "Send test" button — never raised by a signal, allowed on every target.</summary>
    Test,
}

/// <summary>One notification, kind-agnostic: the formatters turn it into each target's wire shape.</summary>
/// <param name="Event">Which milestone.</param>
/// <param name="Title">Short line ("Boarding complete").</param>
/// <param name="Text">One sentence with the figures ("150 passengers aboard. BAW552 EGLL–LIRF.").</param>
/// <param name="FlightNumber">From the OFP; "" when none is loaded.</param>
/// <param name="Route">"EGLL–LIRF" from the OFP; "" when none.</param>
/// <param name="AtUtc">When the milestone happened.</param>
/// <param name="Details">Extra figures for the webhook body (fuelKg, paxCount, minutesToTod…), never secrets.</param>
public sealed record NotificationMessage(
    FlightEvent Event,
    string Title,
    string Text,
    string FlightNumber,
    string Route,
    DateTimeOffset AtUtc,
    IReadOnlyDictionary<string, object?>? Details = null)
{
    /// <summary>The event's wire name — kebab-case, stable, documented.</summary>
    public string EventName => WireName(Event);

    public static string WireName(FlightEvent flightEvent) => flightEvent switch
    {
        FlightEvent.RefuelComplete => "refuel-complete",
        FlightEvent.BoardingComplete => "boarding-complete",
        FlightEvent.FinalLoadsheetSent => "final-loadsheet-sent",
        FlightEvent.ReadyForPushback => "ready-for-pushback",
        FlightEvent.CabinSecure => "cabin-secure",
        FlightEvent.DeiceHoldoverExpiring => "deice-holdover-expiring",
        FlightEvent.TopOfDescentApproaching => "top-of-descent-approaching",
        FlightEvent.Landed => "landed",
        FlightEvent.OnBlocks => "on-blocks",
        FlightEvent.DeboardingComplete => "deboarding-complete",
        _ => "test",
    };

    /// <summary>Whether a target's switches include this event (the test event always passes).</summary>
    public static bool Wants(NotificationEventSwitches switches, FlightEvent flightEvent)
    {
        ArgumentNullException.ThrowIfNull(switches);
        return flightEvent switch
        {
            FlightEvent.RefuelComplete => switches.RefuelComplete,
            FlightEvent.BoardingComplete => switches.BoardingComplete,
            FlightEvent.FinalLoadsheetSent => switches.FinalLoadsheetSent,
            FlightEvent.ReadyForPushback => switches.ReadyForPushback,
            FlightEvent.CabinSecure => switches.CabinSecure,
            FlightEvent.DeiceHoldoverExpiring => switches.DeiceHoldoverExpiring,
            FlightEvent.TopOfDescentApproaching => switches.TopOfDescentApproaching,
            FlightEvent.Landed => switches.Landed,
            FlightEvent.OnBlocks => switches.OnBlocks,
            FlightEvent.DeboardingComplete => switches.DeboardingComplete,
            _ => true,
        };
    }
}

/// <summary>One HTTP request ready to send: the kind-specific body and headers. No secrets
/// here beyond what the caller adds (the bearer header is applied by the dispatcher).</summary>
public sealed record NotificationRequest(string ContentType, string Body, IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// One formatter per target kind (issue #151). The generic webhook body is the versioned
/// contract in docs/integrations/notifications.md (<c>schema: "prosimcompanion.notification/1"</c>);
/// ntfy gets a plain-text body with Title / Tags / Priority headers; Discord gets
/// <c>{ "content": … }</c>. Pure.
/// </summary>
public static class NotificationFormatters
{
    /// <summary>The webhook body version. Bump only for a breaking change; additive fields keep it.</summary>
    public const string WebhookSchema = "prosimcompanion.notification/1";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static NotificationRequest Format(NotificationTargetKind kind, NotificationMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return kind switch
        {
            NotificationTargetKind.Ntfy => Ntfy(message),
            NotificationTargetKind.Discord => Discord(message),
            _ => Webhook(message),
        };
    }

    public static NotificationRequest Webhook(NotificationMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var body = JsonSerializer.Serialize(new
        {
            schema = WebhookSchema,
            @event = message.EventName,
            title = message.Title,
            text = message.Text,
            flightNumber = message.FlightNumber.Length > 0 ? message.FlightNumber : null,
            route = message.Route.Length > 0 ? message.Route : null,
            atUtc = message.AtUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            details = message.Details is { Count: > 0 } ? message.Details : null,
            source = "ProsimCompanion",
        }, Json);
        return new NotificationRequest("application/json", body, new Dictionary<string, string>(StringComparer.Ordinal));
    }

    public static NotificationRequest Ntfy(NotificationMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // ntfy reads these from headers; the body is the message text itself.
            ["Title"] = Ascii(Flight(message) + message.Title),
            ["Tags"] = Tag(message.Event),
            ["Priority"] = message.Event is FlightEvent.DeiceHoldoverExpiring ? "high" : "default",
        };
        return new NotificationRequest("text/plain", message.Text, headers);
    }

    public static NotificationRequest Discord(NotificationMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var body = JsonSerializer.Serialize(new
        {
            content = $"**{Flight(message)}{message.Title}** — {message.Text}",
        }, Json);
        return new NotificationRequest("application/json", body, new Dictionary<string, string>(StringComparer.Ordinal));
    }

    /// <summary>"BAW552 · " when a flight number is known, else "".</summary>
    private static string Flight(NotificationMessage message)
        => message.FlightNumber.Length > 0 ? message.FlightNumber + " · " : "";

    /// <summary>HTTP header values must be ASCII: the middle dot and dashes become plain.</summary>
    private static string Ascii(string text)
        => text.Replace('·', '-').Replace('—', '-').Replace('–', '-');

    private static string Tag(FlightEvent flightEvent) => flightEvent switch
    {
        FlightEvent.RefuelComplete => "fuelpump",
        FlightEvent.BoardingComplete => "busts_in_silhouette",
        FlightEvent.FinalLoadsheetSent => "page_facing_up",
        FlightEvent.ReadyForPushback => "airplane_departure",
        FlightEvent.CabinSecure => "lock",
        FlightEvent.DeiceHoldoverExpiring => "snowflake,warning",
        FlightEvent.TopOfDescentApproaching => "airplane_arriving",
        FlightEvent.Landed => "airplane_arriving",
        FlightEvent.OnBlocks => "checkered_flag",
        FlightEvent.DeboardingComplete => "door",
        _ => "bell",
    };
}
