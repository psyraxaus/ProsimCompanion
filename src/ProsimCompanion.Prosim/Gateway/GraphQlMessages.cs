using System.Globalization;
using System.Text.Json;

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

    private static string BuildStringMutation(string name, string value)
    {
        var nameJson = JsonSerializer.Serialize(name);
        var valueJson = JsonSerializer.Serialize(value);
        return $"{{\"query\": \"mutation ($variableName: String!, $variableValue: String!) {{dataRef {{ writeString(name: $variableName, value: $variableValue) }} }}\", \"variables\": {{ \"variableName\": {nameJson}, \"variableValue\": {valueJson} }} }}";
    }
}
