using System.Text.Json.Nodes;
using ProsimCompanion.Gsx.Protocol;

namespace ProsimCompanion.Gsx.Mirror;

/// <summary>
/// Typed mirror of the server-pushed state model. Mutated only from the WebSocket receive path;
/// readers on any thread get immutable snapshots swapped atomically. Every field is optional on
/// the wire — all extraction is null-safe. See docs/integrations/gsx-remote-api.md §4.
/// </summary>
public sealed class GsxStateMirror
{
    private readonly object _gate = new();
    private Dictionary<string, GsxServiceInfo> _services = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised after any state key is applied, with the key name (receive thread).</summary>
    public event Action<string>? Updated;

    /// <summary>Raised for state keys this mirror does not consume (excluding the known
    /// diagnostics-only "message" key) — first-flight telemetry for protocol additions.</summary>
    public event Action<string>? UnknownKeySeen;

    /// <summary>Raised when startup.sid changes between two non-empty values — the Couatl engine
    /// restarted; all cached context (airport, gate, menus, prepared gate) is invalid.</summary>
    public event Action<string?, string?>? SidChanged;

    public IReadOnlyDictionary<string, GsxServiceInfo> Services
    {
        get
        {
            lock (_gate)
            {
                return _services;
            }
        }
    }

    public GsxMenuInfo? Menu { get; private set; }

    /// <summary>Authoritative menu open/closed flag — trust this, never a client-side view.</summary>
    public bool MenuShown { get; private set; }

    public string? AirportIcao { get; private set; }

    public IReadOnlyList<GsxParking> Parkings { get; private set; } = [];

    /// <summary>Stable key for the loaded gate context, or null when none
    /// (uiName ?? bglName ?? number ?? "loaded").</summary>
    public string? GateContextKey { get; private set; }

    public string? StartupSid { get; private set; }

    /// <summary>Applies one top-level state key (from a snapshot entry or a coarse patch).
    /// Null value removes the key.</summary>
    public void ApplyState(string key, JsonNode? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        switch (key.ToLowerInvariant())
        {
            case "services":
                ApplyServices(value as JsonArray);
                break;
            case "menu":
                Menu = ParseMenu(value as JsonObject);
                break;
            case "menushown":
                MenuShown = GsxFrame.ReadBool(value) ?? false;
                break;
            case "handlerdata":
                ApplyHandlerData(value as JsonObject);
                break;
            case "startup":
                ApplyStartup(value as JsonObject);
                break;
            case "message":
                // Known but diagnostics-only — deliberately not consumed.
                return;
            default:
                // Unknown keys are ignored (protocol-1 additive rules) but surfaced for the
                // first-flight log so protocol additions are noticed.
                UnknownKeySeen?.Invoke(key);
                return;
        }

        Updated?.Invoke(key);
    }

    private void ApplyServices(JsonArray? array)
    {
        var services = new Dictionary<string, GsxServiceInfo>(StringComparer.OrdinalIgnoreCase);
        if (array is not null)
        {
            foreach (var item in array)
            {
                if (item is not JsonObject entry)
                {
                    continue;
                }

                var id = GsxFrame.ReadString(entry["id"]);
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var semanticState = GsxFrame.ReadString(entry["state"]);
                services[id] = new GsxServiceInfo(
                    id,
                    GsxFrame.ReadString(entry["displayName"]) ?? id,
                    semanticState,
                    GsxServiceStateMapper.Map(semanticState),
                    GsxFrame.ReadBool(entry["canTrigger"]) ?? false,
                    GsxFrame.ReadBool(entry["waiting"]) ?? false,
                    GsxFrame.ReadString(entry["operator"]),
                    GsxFrame.ReadString(entry["progressText"]));
            }
        }

        lock (_gate)
        {
            _services = services;
        }
    }

    private static GsxMenuInfo? ParseMenu(JsonObject? menu)
    {
        if (menu is null)
        {
            return null;
        }

        var entries = new List<string>();
        if (menu["entries"] is JsonArray entryArray)
        {
            foreach (var entry in entryArray)
            {
                entries.Add(GsxFrame.ReadString(entry) ?? "");
            }
        }

        var disabled = new bool[entries.Count];
        if (menu["disabled"] is JsonArray disabledArray)
        {
            for (var i = 0; i < disabled.Length && i < disabledArray.Count; i++)
            {
                disabled[i] = GsxFrame.ReadBool(disabledArray[i]) ?? false;
            }
        }

        return new GsxMenuInfo(GsxFrame.ReadString(menu["title"]) ?? "", entries, disabled);
    }

    private void ApplyHandlerData(JsonObject? handlerData)
    {
        if (handlerData is null)
        {
            AirportIcao = null;
            Parkings = [];
            GateContextKey = null;
            return;
        }

        var airport = handlerData["airport"] as JsonObject;
        AirportIcao = GsxFrame.ReadString(airport?["icao"]);

        var parkings = new List<GsxParking>();
        if (airport?["parkings"] is JsonArray parkingArray)
        {
            foreach (var item in parkingArray)
            {
                if (item is not JsonObject parking)
                {
                    continue;
                }

                parkings.Add(new GsxParking(
                    GsxFrame.ReadString(parking["uiName"]),
                    GsxFrame.ReadString(parking["uiGateName"]),
                    GsxFrame.ReadString(parking["bglName"]),
                    GsxFrame.ReadInt(parking["number"]),
                    // Coordinates may arrive as lat/lon or latitude/longitude.
                    GsxFrame.ReadDouble(parking["lat"]) ?? GsxFrame.ReadDouble(parking["latitude"]),
                    GsxFrame.ReadDouble(parking["lon"]) ?? GsxFrame.ReadDouble(parking["longitude"])));
            }
        }
        Parkings = parkings;

        var gate = handlerData["gate"] as JsonObject;
        GateContextKey = gate is null
            ? null
            : GsxFrame.ReadString(gate["uiName"])
                ?? GsxFrame.ReadString(gate["bglName"])
                ?? GsxFrame.ReadInt(gate["number"])?.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ?? "loaded";
    }

    private void ApplyStartup(JsonObject? startup)
    {
        var newSid = GsxFrame.ReadString(startup?["sid"]);
        var oldSid = StartupSid;
        StartupSid = newSid;

        if (!string.IsNullOrEmpty(oldSid)
            && !string.IsNullOrEmpty(newSid)
            && !string.Equals(oldSid, newSid, StringComparison.Ordinal))
        {
            SidChanged?.Invoke(oldSid, newSid);
        }
    }
}

/// <summary>One mirrored service. <see cref="SemanticState"/> is the wire string; control flow
/// uses <see cref="State"/> (mapped). Everything else is display/diagnostics.</summary>
public sealed record GsxServiceInfo(
    string Id,
    string DisplayName,
    string? SemanticState,
    GsxServiceState State,
    bool CanTrigger,
    bool Waiting,
    string? Operator,
    string? ProgressText);

/// <summary>The mirrored menu; entry index is the <c>menu.pick</c> index; <see cref="Disabled"/>
/// is parallel to <see cref="Entries"/>.</summary>
public sealed record GsxMenuInfo(string Title, IReadOnlyList<string> Entries, IReadOnlyList<bool> Disabled);

public sealed record GsxParking(
    string? UiName,
    string? UiGateName,
    string? BglName,
    int? Number,
    double? Latitude,
    double? Longitude);
