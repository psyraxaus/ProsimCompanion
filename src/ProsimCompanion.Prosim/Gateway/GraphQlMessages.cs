using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProsimCompanion.Prosim.Gateway;

/// <summary>
/// Builds the exact GraphQL request bodies the ProSim gateway expects. The shapes are
/// wire-contract facts carried over from the predecessors — in particular, string values are
/// emitted as proper JSON string literals and travel via GraphQL variables, because payloads
/// like loadsheet envelopes and ACARS messages contain quotes, backslashes and newlines that
/// break naive interpolation.
/// </summary>
public static class GraphQlMessages
{
    /// <summary>Builds a dataref write mutation. The mutation type follows the runtime type of
    /// <paramref name="value"/>: bool → writeBool, int → writeInt, float/double → writeFloat,
    /// anything else → writeString.</summary>
    public static string BuildWriteMutation(string name, object value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);

        return value switch
        {
            bool boolValue =>
                $"{{\"query\": \"mutation ($variableName: String!) {{dataRef {{ writeBool(name: $variableName, value: {(boolValue ? "true" : "false")}) }} }}\", \"variables\": {{ \"variableName\": \"{name}\" }} }}",

            int intValue =>
                $"{{\"query\": \"mutation ($variableName: String!) {{dataRef {{ writeInt(name: $variableName, value: {intValue.ToString(CultureInfo.InvariantCulture)}) }} }}\", \"variables\": {{ \"variableName\": \"{name}\" }} }}",

            float or double =>
                string.Format(
                    CultureInfo.InvariantCulture,
                    "{{\"query\": \"mutation ($variableName: String!, $variableValue: Float!) {{dataRef {{ writeFloat(name: $variableName, value: $variableValue) }} }}\", \"variables\": {{ \"variableName\": \"{0}\", \"variableValue\": {1:F8} }} }}",
                    name,
                    Convert.ToDouble(value, CultureInfo.InvariantCulture)),

            _ => BuildStringMutation(name, value.ToString() ?? string.Empty),
        };
    }

    /// <summary>Builds a dataref value query. <paramref name="alias"/> names the result field in
    /// the response envelope (<c>data.dataRef.{alias}.value</c>).</summary>
    public static string BuildQuery(string name, string alias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);

        return $"{{\"query\":\"query {{ dataRef {{{alias}: dataRef(name: \\\"{name}\\\") {{value}} }} }}\",\"variables\":{{}} }}";
    }

    /// <summary>
    /// Reads the gateway's answer to a write mutation. ProSim returns HTTP 200 for every
    /// well-formed mutation and puts the verdict in the body: <c>{"data":{"dataRef":{"writeBool":true}}}</c>
    /// on success, <c>false</c> when the dataref is unknown or not writable (seen 2026-10-06
    /// on ProSim 1.75.1: <c>efb.gsx.autoCatering</c> answered <c>false</c>, a real option
    /// <c>true</c>). Until then the client took the status code alone and reported dead writes
    /// as done. A body without the verdict (older gateway, GraphQL errors) still counts as
    /// success, so nothing that worked before turns red; a GraphQL <c>errors</c> array does not.
    /// </summary>
    /// <returns>True when ProSim accepted the write (or gave no verdict).</returns>
    public static bool WriteAccepted(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return true;
        }

        try
        {
            var dataRef = JsonNode.Parse(responseBody)?["data"]?["dataRef"]?.AsObject();
            if (dataRef is null)
            {
                return true;
            }

            foreach (var (_, result) in dataRef)
            {
                if (result is JsonValue value && value.TryGetValue<bool>(out var accepted))
                {
                    return accepted;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return true;
        }
    }

    private static string BuildStringMutation(string name, string value)
    {
        var nameJson = JsonSerializer.Serialize(name);
        var valueJson = JsonSerializer.Serialize(value);
        return $"{{\"query\": \"mutation ($variableName: String!, $variableValue: String!) {{dataRef {{ writeString(name: $variableName, value: $variableValue) }} }}\", \"variables\": {{ \"variableName\": {nameJson}, \"variableValue\": {valueJson} }} }}";
    }
}
