using System.Text.Json;
using ProsimCompanion.Core.Aircraft.Gateway;
using Xunit;

namespace ProsimCompanion.Core.Tests.Aircraft;

/// <summary>Owner's flight 2026-11-14: the landing calculation answered 500 "Value cannot be
/// null. (Parameter 'AircraftSpeed')" and the page said "is ProSim running?".</summary>
public sealed class GatewayFailureTextTests
{
    [Fact]
    public void TheGatewaysOwnMessage_IsTheReason()
        => Assert.Equal(
            "HTTP 500 — Value cannot be null. (Parameter 'AircraftSpeed')",
            GatewayFailureText.Describe(500, "{\"message\":\"Value cannot be null. (Parameter 'AircraftSpeed')\"}"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithNoBody_TheStatusCodeIsTheReason(string? body)
        => Assert.Equal("HTTP 500", GatewayFailureText.Describe(500, body));

    [Fact]
    public void ABodyThatIsNotJson_IsTakenAsWritten_OnOneLine()
        => Assert.Equal("HTTP 502 — Bad  Gateway", GatewayFailureText.Describe(502, "Bad\r\nGateway\n"));

    [Fact]
    public void ALongBody_IsCut()
    {
        var text = GatewayFailureText.Describe(500, new string('x', 5000));
        Assert.Equal("HTTP 500 — ".Length + GatewayFailureText.MaxLength + 1, text.Length);
        Assert.EndsWith("…", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLandingRequest_AlwaysCarriesAnAircraftSpeed()
    {
        // The regression: an empty VAPP override went out as null (or was left out) and the
        // gateway refused the whole calculation. 0 = no override.
        var json = JsonSerializer.Serialize(new CalcLdrRequest());
        using var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Number, document.RootElement.GetProperty("AircraftSpeed").ValueKind);
        Assert.Equal(0, document.RootElement.GetProperty("AircraftSpeed").GetInt32());
    }
}
