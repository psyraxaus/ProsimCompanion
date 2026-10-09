using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Audio.Mixer;

/// <summary>Frames the agent sends (VoicemeeterBridge protocol v1, <c>claude/mixer-protocol.md</c>).
/// Unknown ops parse to null and are ignored; the protocol is additive.</summary>
public abstract record MixerInboundFrame;

public sealed record MixerWelcomeFrame(int Protocol, MixerVoicemeeterStatus Voicemeeter) : MixerInboundFrame;

public sealed record MixerStatusFrame(MixerVoicemeeterStatus Voicemeeter) : MixerInboundFrame;

/// <summary>Reply to <c>watch</c> (snapshot, no id) and <c>get</c> (values, echoed id) —
/// same shape.</summary>
public sealed record MixerValuesFrame(
    string? Id,
    IReadOnlyDictionary<string, MixerValue> Values,
    IReadOnlyDictionary<string, string> Errors) : MixerInboundFrame;

public sealed record MixerChangedFrame(string Parameter, MixerValue Value) : MixerInboundFrame;

public sealed record MixerResultFrame(string? Id, bool Ok, string? Error) : MixerInboundFrame;

public sealed record MixerErrorFrame(string Error) : MixerInboundFrame;

public sealed record MixerPongFrame : MixerInboundFrame;

/// <summary>
/// Builds outbound frames and parses inbound ones. Pure, so the wire format is testable
/// without a socket. Outbound text is compact JSON; the hello builder also yields the
/// redacted copy the wire trace is allowed to see (the token never reaches a log).
/// </summary>
public static class MixerFrame
{
    public const int Protocol = 1;

    private static readonly JsonSerializerOptions Compact = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public static string BuildHello(string token) =>
        JsonSerializer.Serialize(new { op = "hello", token, protocol = Protocol }, Compact);

    /// <summary>The hello as the wire trace may record it.</summary>
    public static string RedactedHello() =>
        JsonSerializer.Serialize(new { op = "hello", token = "***", protocol = Protocol }, Compact);

    public static string BuildWatch(IEnumerable<string> parameters) =>
        JsonSerializer.Serialize(new { op = "watch", @params = parameters.ToArray() }, Compact);

    public static string BuildUnwatch(IEnumerable<string> parameters) =>
        JsonSerializer.Serialize(new { op = "unwatch", @params = parameters.ToArray() }, Compact);

    public static string BuildSet(string id, string parameter, double value) =>
        JsonSerializer.Serialize(new { op = "set", id, param = parameter, value }, Compact);

    public static string BuildGet(string id, IEnumerable<string> parameters) =>
        JsonSerializer.Serialize(new { op = "get", id, @params = parameters.ToArray() }, Compact);

    public static string BuildPing() => "{\"op\":\"ping\"}";

    /// <summary>Parses one agent frame; null for invalid JSON, a missing op or an op this
    /// client does not consume (<c>levels</c>).</summary>
    public static MixerInboundFrame? Parse(string text)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }

        if (node is not JsonObject obj || obj["op"]?.GetValue<string>() is not { } op)
        {
            return null;
        }

        switch (op)
        {
            case "welcome":
                return new MixerWelcomeFrame(
                    obj["protocol"] is JsonValue p && p.TryGetValue<int>(out var protocol) ? protocol : 0,
                    ParseVoicemeeter(obj["voicemeeter"] as JsonObject));
            case "status":
                return new MixerStatusFrame(ParseVoicemeeter(obj["voicemeeter"] as JsonObject));
            case "snapshot":
            case "values":
                return new MixerValuesFrame(
                    obj["id"]?.GetValue<string>(),
                    ParseValues(obj["values"] as JsonObject),
                    ParseErrors(obj["errors"] as JsonObject));
            case "changed":
                return obj["param"]?.GetValue<string>() is { Length: > 0 } param && ParseValue(obj["value"]) is { } value
                    ? new MixerChangedFrame(param, value)
                    : null;
            case "result":
                return new MixerResultFrame(
                    obj["id"]?.GetValue<string>(),
                    obj["ok"] is JsonValue okNode && okNode.TryGetValue<bool>(out var ok) && ok,
                    obj["error"]?.GetValue<string>());
            case "error":
                return new MixerErrorFrame(obj["error"]?.GetValue<string>() ?? "error");
            case "pong":
                return new MixerPongFrame();
            default:
                return null;
        }
    }

    private static MixerVoicemeeterStatus ParseVoicemeeter(JsonObject? node)
    {
        if (node is null)
        {
            return MixerVoicemeeterStatus.Unknown;
        }

        var connected = node["connected"] is JsonValue c && c.TryGetValue<bool>(out var value) && value;
        return new MixerVoicemeeterStatus(
            connected,
            connected ? node["kind"]?.GetValue<string>() : null,
            connected ? node["version"]?.GetValue<string>() : null);
    }

    private static Dictionary<string, MixerValue> ParseValues(JsonObject? node)
    {
        var values = new Dictionary<string, MixerValue>(StringComparer.Ordinal);
        if (node is null)
        {
            return values;
        }

        foreach (var (name, raw) in node)
        {
            if (ParseValue(raw) is { } value)
            {
                values[name] = value;
            }
        }

        return values;
    }

    private static Dictionary<string, string> ParseErrors(JsonObject? node)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        if (node is null)
        {
            return errors;
        }

        foreach (var (name, raw) in node)
        {
            errors[name] = raw?.GetValue<string>() ?? "error";
        }

        return errors;
    }

    /// <summary>Number or string, as Voicemeeter reports it; anything else is dropped.</summary>
    private static MixerValue? ParseValue(JsonNode? raw)
    {
        if (raw is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<double>(out var number))
        {
            return MixerValue.FromNumber(number);
        }

        if (value.TryGetValue<string>(out var text))
        {
            return MixerValue.FromText(text);
        }

        if (value.TryGetValue<bool>(out var flag))
        {
            return MixerValue.FromNumber(flag ? 1 : 0);
        }

        return null;
    }

    /// <summary>Invariant text for log lines ("-6.5", never "-6,5").</summary>
    public static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
