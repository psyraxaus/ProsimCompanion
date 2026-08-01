using System.Text.Json.Nodes;
using ProsimCompanion.Gsx.Mirror;
using ProsimCompanion.Gsx.Protocol;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

public sealed class GsxStateMirrorTests
{
    private readonly GsxStateMirror _mirror = new();

    [Fact]
    public void ApplyServices_MapsSemanticStates_KeyedCaseInsensitively()
    {
        _mirror.ApplyState("services", JsonNode.Parse(
            """
            [ { "id": "Refueling", "displayName": "Refuel", "state": "performing", "canTrigger": false },
              { "id": "Water", "state": "available", "canTrigger": true } ]
            """));

        Assert.Equal(GsxServiceState.Active, _mirror.Services["refueling"].State);
        Assert.Equal(GsxServiceState.Callable, _mirror.Services["WATER"].State);
        Assert.True(_mirror.Services["Water"].CanTrigger);
    }

    [Fact]
    public void ApplyServices_Null_ClearsAll()
    {
        _mirror.ApplyState("services", JsonNode.Parse("""[ { "id": "GPU", "state": "available" } ]"""));
        _mirror.ApplyState("services", null);

        Assert.Empty(_mirror.Services);
    }

    [Fact]
    public void ApplyMenu_ParsesEntriesAndDisabled_NullRemoves()
    {
        _mirror.ApplyState("menu", JsonNode.Parse(
            """{ "title": "Select handling operator", "entries": ["GSX choice", "Operator A"], "disabled": [false, true] }"""));

        Assert.Equal("Select handling operator", _mirror.Menu?.Title);
        Assert.Equal(2, _mirror.Menu?.Entries.Count);
        Assert.True(_mirror.Menu?.Disabled[1]);

        _mirror.ApplyState("menu", null);
        Assert.Null(_mirror.Menu);
    }

    [Fact]
    public void ApplyMenuShown_NullMeansFalse()
    {
        _mirror.ApplyState("menuShown", JsonNode.Parse("true"));
        Assert.True(_mirror.MenuShown);

        _mirror.ApplyState("menuShown", null);
        Assert.False(_mirror.MenuShown);
    }

    [Fact]
    public void ApplyHandlerData_ReadsAirportParkingsAndGateKey()
    {
        _mirror.ApplyState("handlerData", JsonNode.Parse(
            """
            { "airport": { "icao": "EHAM",
                "parkings": [ { "uiGateName": "D57", "bglName": "GATE_D_57", "number": 57, "lat": 52.3, "lon": 4.7 } ] },
              "gate": { "bglName": "GATE_D_57", "number": 57 } }
            """));

        Assert.Equal("EHAM", _mirror.AirportIcao);
        var parking = Assert.Single(_mirror.Parkings);
        Assert.Equal("D57", parking.UiGateName);
        Assert.Equal(52.3, parking.Latitude!.Value, precision: 5);
        Assert.Equal("GATE_D_57", _mirror.GateContextKey);
    }

    [Fact]
    public void ApplyHandlerData_AlternateCoordinateNames_AreRead()
    {
        _mirror.ApplyState("handlerData", JsonNode.Parse(
            """{ "airport": { "parkings": [ { "uiName": "B2", "latitude": 1.5, "longitude": 2.5 } ] } }"""));

        var parking = Assert.Single(_mirror.Parkings);
        Assert.Equal(1.5, parking.Latitude!.Value, precision: 5);
        Assert.Equal(2.5, parking.Longitude!.Value, precision: 5);
    }

    [Fact]
    public void ApplyStartup_SidChangeBetweenNonEmptyValues_RaisesSidChanged()
    {
        var changes = new List<(string? Old, string? New)>();
        _mirror.SidChanged += (oldSid, newSid) => changes.Add((oldSid, newSid));

        _mirror.ApplyState("startup", JsonNode.Parse("""{ "sid": "A" }"""));
        _mirror.ApplyState("startup", JsonNode.Parse("""{ "sid": "A" }"""));
        _mirror.ApplyState("startup", JsonNode.Parse("""{ "sid": "B" }"""));

        Assert.Equal([("A", "B")], changes);
    }

    [Fact]
    public void ApplyState_UnknownKey_IsIgnoredWithoutEvent()
    {
        var updates = 0;
        _mirror.Updated += _ => updates++;

        _mirror.ApplyState("message", JsonNode.Parse("""{ "text": "hi", "visible": true }"""));
        _mirror.ApplyState("futureKey", JsonNode.Parse("42"));

        Assert.Equal(0, updates);
    }
}
