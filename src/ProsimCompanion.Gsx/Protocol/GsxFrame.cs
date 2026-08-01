using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProsimCompanion.Gsx.Protocol;

/// <summary>
/// Lenient, field-by-field parsing of Remote API frames (protocol-1 additive rules: every field
/// optional, unknown types/topics ignored, never strict POCO binding) and exact command frame
/// building. See docs/integrations/gsx-remote-api.md §2.
/// </summary>
public abstract record GsxFrame
{
    /// <summary>Envelope version (<c>"v"</c>); null when absent. Anything other than 1/null
    /// means an unknown protocol — hold non-Ready.</summary>
    public int? EnvelopeVersion { get; init; }

    /// <summary>Parses one text frame. Returns null for unparseable JSON or unknown frame types
    /// (both are ignored by design).</summary>
    public static GsxFrame? Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }

        if (root is null)
        {
            return null;
        }

        var envelopeVersion = ReadInt(root["v"]);
        return ReadString(root["type"])?.ToLowerInvariant() switch
        {
            "hello" => ParseHello(root, envelopeVersion),
            "result" => ParseResult(root, envelopeVersion),
            "snapshot" => new GsxSnapshotFrame(StateEntries(root)) { EnvelopeVersion = envelopeVersion },
            "patch" => ParsePatch(root, envelopeVersion),
            "event" => ParseEvent(root, envelopeVersion),
            _ => null,
        };
    }

    /// <summary>Builds a command frame. <paramref name="args"/> is omitted entirely when null —
    /// never sent as <c>{}</c>.</summary>
    public static string BuildCommand(string id, string verb, JsonObject? args)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(verb);

        var frame = new JsonObject
        {
            ["type"] = "command",
            ["id"] = id,
            ["verb"] = verb,
        };
        if (args is not null)
        {
            frame["args"] = args;
        }

        return frame.ToJsonString();
    }

    /// <summary>The one non-command client frame: the state-channel subscription.</summary>
    public static string BuildSubscribe() => """{"type":"subscribe","channels":["state"]}""";

    private static GsxHelloFrame ParseHello(JsonObject root, int? envelopeVersion)
    {
        var capabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root["capabilities"] is JsonArray array)
        {
            foreach (var item in array)
            {
                if (ReadString(item) is { Length: > 0 } token)
                {
                    capabilities.Add(token);
                }
            }
        }

        return new GsxHelloFrame(ReadInt(root["protocol"]), ReadBool(root["gsxRunning"]) ?? false, capabilities)
        {
            EnvelopeVersion = envelopeVersion,
        };
    }

    private static GsxResultFrame ParseResult(JsonObject root, int? envelopeVersion)
    {
        var ok = ReadBool(root["ok"]) ?? false;
        var payload = root["payload"] as JsonObject;
        var error = root["error"] as JsonObject;
        var code = ok
            ? ReadString(payload?["code"]) ?? "ok"
            : ReadString(error?["code"]) ?? "error";

        return new GsxResultFrame(ReadString(root["id"]), ok, code, payload, error)
        {
            EnvelopeVersion = envelopeVersion,
        };
    }

    private static GsxPatchFrame? ParsePatch(JsonObject root, int? envelopeVersion)
    {
        var path = ReadString(root["path"]);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return new GsxPatchFrame(path.TrimStart('/'), root["value"])
        {
            EnvelopeVersion = envelopeVersion,
        };
    }

    private static GsxEngineEventFrame? ParseEvent(JsonObject root, int? envelopeVersion)
    {
        // Only the engine topic is consumed; unknown topics are ignored.
        if (!string.Equals(ReadString(root["topic"]), "engine", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new GsxEngineEventFrame(ReadBool(root["gsxRunning"]), ReadBool(root["restarting"]) ?? false)
        {
            EnvelopeVersion = envelopeVersion,
        };
    }

    /// <summary>The state model may arrive under a <c>state</c> object or inline beside the
    /// envelope — iterate top-level keys skipping envelope fields.</summary>
    private static List<KeyValuePair<string, JsonNode?>> StateEntries(JsonObject root)
    {
        var source = root["state"] as JsonObject ?? root;
        var entries = new List<KeyValuePair<string, JsonNode?>>();
        foreach (var (key, value) in source)
        {
            if (key is "v" or "type" or "ts" or "id" or "state")
            {
                continue;
            }
            entries.Add(new KeyValuePair<string, JsonNode?>(key, value));
        }
        return entries;
    }

    internal static string? ReadString(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    internal static int? ReadInt(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<int>(out var i))
        {
            return i;
        }

        return value.TryGetValue<double>(out var d) ? (int)d : null;
    }

    internal static bool? ReadBool(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<bool>(out var b) ? b : null;

    internal static double? ReadDouble(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<double>(out var d) ? d : null;
}

/// <summary>Server hello: protocol version, engine liveness, capability tokens.</summary>
public sealed record GsxHelloFrame(int? Protocol, bool GsxRunning, IReadOnlySet<string> Capabilities) : GsxFrame;

/// <summary>Command result. <see cref="Id"/> null means unsolicited (e.g. subscribe ack) —
/// ignore. <see cref="Code"/> defaults to "ok"/"error"; unknown codes round-trip verbatim.</summary>
public sealed record GsxResultFrame(string? Id, bool Ok, string Code, JsonObject? Payload, JsonObject? Error) : GsxFrame;

/// <summary>Full state model (list of top-level key/value pairs to apply).</summary>
public sealed record GsxSnapshotFrame(IReadOnlyList<KeyValuePair<string, JsonNode?>> StateEntries) : GsxFrame;

/// <summary>Coarse patch: one top-level key replaced wholesale; null value removes it.</summary>
public sealed record GsxPatchFrame(string Key, JsonNode? Value) : GsxFrame;

/// <summary>Engine event; <see cref="Restarting"/> arrives before the socket drops.</summary>
public sealed record GsxEngineEventFrame(bool? GsxRunning, bool Restarting) : GsxFrame;
