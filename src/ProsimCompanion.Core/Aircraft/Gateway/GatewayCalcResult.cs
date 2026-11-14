using System.Text.Json;

namespace ProsimCompanion.Core.Aircraft.Gateway;

/// <summary>
/// Outcome of a gateway performance calculation (<c>/efb/calculate/*</c>). Three states:
/// <list type="bullet">
/// <item><see cref="Value"/> non-null — the gateway answered with a result;</item>
/// <item><see cref="Reached"/> true and <see cref="Value"/> null — the gateway answered and
///   refused; <see cref="FailureReason"/> carries its own words;</item>
/// <item><see cref="Reached"/> false — no HTTP answer at all (ProSim not running).</item>
/// </list>
/// Owner's flight 2026-11-14: <c>/calculate/ldr</c> answered 500 "Value cannot be null.
/// (Parameter 'AircraftSpeed')" and the page said "is ProSim running?" — a bare null hid the
/// difference between "refused" and "unreachable" (the same lesson as issue #62 for METAR).
/// </summary>
public sealed record GatewayCalcResult<T>(T? Value, string? FailureReason, bool Reached)
    where T : class;

/// <summary>Turns a failed gateway answer into one short line for the page and the log.</summary>
public static class GatewayFailureText
{
    /// <summary>Longest reason shown: the gateway's messages are one sentence; anything longer
    /// is a stack trace nobody reads on a performance page.</summary>
    public const int MaxLength = 200;

    /// <summary>"HTTP 500 — Value cannot be null. (Parameter 'AircraftSpeed')". The gateway
    /// answers errors as <c>{"message":"…"}</c>; any other body is taken as written. Line
    /// breaks are removed (the text goes to the log) and the text is cut at
    /// <see cref="MaxLength"/>.</summary>
    public static string Describe(int statusCode, string? body)
    {
        var detail = OneLine(Message(body));
        return detail.Length == 0 ? $"HTTP {statusCode}" : $"HTTP {statusCode} — {detail}";
    }

    private static string Message(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (string.Equals(property.Name, "message", StringComparison.OrdinalIgnoreCase)
                        && property.Value.ValueKind == JsonValueKind.String)
                    {
                        return property.Value.GetString() ?? string.Empty;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON — the raw text is the message.
        }

        return body;
    }

    private static string OneLine(string text)
    {
        var flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= MaxLength ? flat : flat[..MaxLength] + "…";
    }
}
