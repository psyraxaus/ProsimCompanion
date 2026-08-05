using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProsimCompanion.Core.Aircraft.Acars;

/// <summary>
/// The uplink envelope ProSim's ACARS dispatch expects on <c>efb.aoc.message.uplink</c>. The
/// wire keys are LOWERCASE and this is load-bearing: ProSim reads the acknowledgment flag from
/// <c>accept</c>; a PascalCase payload still displays in RCVD MSGS but never offers the ACCEPT
/// prompt (verified 2026-05, docs/integrations/prosim.md).
/// </summary>
public sealed record AcarsMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("header")]
    public string Header { get; init; } = "";

    /// <summary>Message slot id. Loadsheets use fixed slots — "01" prelim / "02" final — so a
    /// regenerated loadsheet replaces the prior cockpit entry instead of stacking, and the ATSU
    /// loadsheet-ACCEPT handling finds it.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("accept")]
    public bool Accept { get; init; }

    [JsonPropertyName("content")]
    public string Content { get; init; } = "";

    private static readonly JsonSerializerOptions Wire = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Serialized wire form (loadsheet bodies contain quotes/newlines — the GraphQL
    /// mutation layer escapes them again for transport).</summary>
    public string ToWireJson() => JsonSerializer.Serialize(this, Wire);
}
