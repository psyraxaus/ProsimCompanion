using System.Text.Json.Nodes;
using ProsimCompanion.Prosim.Gateway;
using Xunit;

namespace ProsimCompanion.Core.Tests.Prosim;

public sealed class GraphQlMessagesTests
{
    [Fact]
    public void BuildWriteMutation_Bool_UsesWriteBoolWithLowercaseLiteral()
    {
        var body = GraphQlMessages.BuildWriteMutation("efb.chocks", true);

        var root = JsonNode.Parse(body)!;
        Assert.Contains("writeBool(name: $variableName, value: true)", (string)root["query"]!, StringComparison.Ordinal);
        Assert.Equal("efb.chocks", (string)root["variables"]!["variableName"]!);
    }

    [Fact]
    public void BuildWriteMutation_Int_InlinesValue()
    {
        var body = GraphQlMessages.BuildWriteMutation("aircraft.passengers.zone1.amount", 42);

        var root = JsonNode.Parse(body)!;
        Assert.Contains("writeInt(name: $variableName, value: 42)", (string)root["query"]!, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildWriteMutation_Double_UsesFloatVariableWithInvariantFormat()
    {
        var body = GraphQlMessages.BuildWriteMutation("aircraft.refuel.fuelTarget.kg", 6250.5);

        var root = JsonNode.Parse(body)!;
        Assert.Contains("writeFloat(name: $variableName, value: $variableValue)", (string)root["query"]!, StringComparison.Ordinal);
        Assert.Equal(6250.5, (double)root["variables"]!["variableValue"]!, precision: 5);
    }

    [Fact]
    public void BuildWriteMutation_StringWithQuotesAndNewlines_StaysValidJson()
    {
        // The regression the predecessors hit: loadsheet/ACARS payloads full of quotes.
        var payload = """{"type":1,"content":"LINE1\nLINE2 \"QUOTED\""}""";

        var body = GraphQlMessages.BuildWriteMutation("efb.prelimLoadsheet", payload);

        var root = JsonNode.Parse(body)!;
        Assert.Contains("writeString", (string)root["query"]!, StringComparison.Ordinal);
        Assert.Equal(payload, (string)root["variables"]!["variableValue"]!);
    }

    [Fact]
    public void BuildQuery_ProducesAliasedQueryEnvelope()
    {
        var body = GraphQlMessages.BuildQuery("efb.flightTimestampJSON", "flightTimestampJSON");

        var root = JsonNode.Parse(body)!;
        Assert.Contains(
            "flightTimestampJSON: dataRef(name: \"efb.flightTimestampJSON\") {value}",
            (string)root["query"]!,
            StringComparison.Ordinal);
    }
}
