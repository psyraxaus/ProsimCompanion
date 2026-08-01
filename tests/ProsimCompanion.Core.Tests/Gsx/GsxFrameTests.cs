using System.Text.Json.Nodes;
using ProsimCompanion.Gsx.Protocol;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

public sealed class GsxFrameTests
{
    [Fact]
    public void Parse_Hello_ReadsProtocolRunningAndCapabilities()
    {
        var frame = GsxFrame.Parse(
            """{ "type": "hello", "protocol": 1, "gsxRunning": true, "capabilities": ["gate", "handlerData", "handlerSet"] }""");

        var hello = Assert.IsType<GsxHelloFrame>(frame);
        Assert.Equal(1, hello.Protocol);
        Assert.True(hello.GsxRunning);
        Assert.Contains("GATE", hello.Capabilities); // case-insensitive set
    }

    [Fact]
    public void Parse_ResultOk_CodeDefaultsToOk()
    {
        var frame = GsxFrame.Parse(
            """{ "v": 1, "type": "result", "id": "g-1", "ok": true, "payload": { "status": "done" } }""");

        var result = Assert.IsType<GsxResultFrame>(frame);
        Assert.True(result.Ok);
        Assert.Equal("ok", result.Code);
        Assert.Equal("g-1", result.Id);
    }

    [Fact]
    public void Parse_ResultError_ReadsErrorCodeAndCandidates()
    {
        var frame = GsxFrame.Parse(
            """{ "v": 1, "type": "result", "id": "g-2", "ok": false, "error": { "code": "ambiguous", "candidates": [ { "uiName": "B12" } ] } }""");

        var result = Assert.IsType<GsxResultFrame>(frame);
        Assert.False(result.Ok);
        Assert.Equal("ambiguous", result.Code);
        Assert.NotNull(result.Error?["candidates"]);
    }

    [Fact]
    public void Parse_Snapshot_WithStateWrapper_IteratesModelKeys()
    {
        var frame = GsxFrame.Parse(
            """{ "v": 1, "type": "snapshot", "ts": 1, "state": { "menuShown": true, "services": [] } }""");

        var snapshot = Assert.IsType<GsxSnapshotFrame>(frame);
        Assert.Equal(["menuShown", "services"], snapshot.StateEntries.Select(e => e.Key).Order());
    }

    [Fact]
    public void Parse_Snapshot_InlineModel_SkipsEnvelopeKeys()
    {
        var frame = GsxFrame.Parse(
            """{ "v": 1, "type": "snapshot", "ts": 1, "menuShown": false }""");

        var snapshot = Assert.IsType<GsxSnapshotFrame>(frame);
        var entry = Assert.Single(snapshot.StateEntries);
        Assert.Equal("menuShown", entry.Key);
    }

    [Fact]
    public void Parse_Patch_TrimsLeadingSlash_NullValueMeansRemoval()
    {
        var frame = GsxFrame.Parse("""{ "v": 1, "type": "patch", "path": "/menu", "value": null }""");

        var patch = Assert.IsType<GsxPatchFrame>(frame);
        Assert.Equal("menu", patch.Key);
        Assert.Null(patch.Value);
    }

    [Fact]
    public void Parse_EngineEvent_ReadsRunningAndRestarting()
    {
        var frame = GsxFrame.Parse(
            """{ "v": 1, "type": "event", "topic": "engine", "gsxRunning": false, "restarting": true }""");

        var engineEvent = Assert.IsType<GsxEngineEventFrame>(frame);
        Assert.False(engineEvent.GsxRunning);
        Assert.True(engineEvent.Restarting);
    }

    [Theory]
    [InlineData("""{ "type": "event", "topic": "weather" }""")]   // unknown topic
    [InlineData("""{ "type": "mystery" }""")]                       // unknown frame type
    [InlineData("not json at all")]
    public void Parse_UnknownOrInvalid_ReturnsNull(string json)
        => Assert.Null(GsxFrame.Parse(json));

    [Fact]
    public void BuildCommand_WithArgs_ProducesEnvelope()
    {
        var body = GsxFrame.BuildCommand("s-1", "service.trigger", new JsonObject { ["service"] = "Refueling" });

        var root = JsonNode.Parse(body)!;
        Assert.Equal("command", (string?)root["type"]);
        Assert.Equal("s-1", (string?)root["id"]);
        Assert.Equal("service.trigger", (string?)root["verb"]);
        Assert.Equal("Refueling", (string?)root["args"]!["service"]);
    }

    [Fact]
    public void BuildCommand_NoArgs_OmitsArgsEntirely()
    {
        var body = GsxFrame.BuildCommand("m-1", "menu.open", null);

        Assert.DoesNotContain("args", body, StringComparison.Ordinal);
    }
}
