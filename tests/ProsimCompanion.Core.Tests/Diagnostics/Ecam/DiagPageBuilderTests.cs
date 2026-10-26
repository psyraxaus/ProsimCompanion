using ProsimCompanion.Core.Diagnostics.Ecam;
using Xunit;

namespace ProsimCompanion.Core.Tests.Diagnostics.Ecam;

/// <summary>The page builder: merging two collectors, link state resolution, first caution
/// line wins, and the NoData downgrade after a throw.</summary>
public sealed class DiagPageBuilderTests
{
    [Fact]
    public void Merge_TwoCollectorsOnOnePage_UnionsNodesLinksGaugesAndReadouts()
    {
        var a = EcamTestSupport.Builder(DiagPageIds.Sim)
            .Node("prosim", DiagState.Normal, "CONN")
            .Readout("rate_datarefs", "20")
            .Gauge("g1", 5, 0, 10, 8, 9, "MS")
            .Link("prosim", "bus");
        var b = EcamTestSupport.Builder(DiagPageIds.Sim)
            .Node("simconnect", DiagState.Caution, "RECONN")
            .Readout("rate_simvars", "XX")
            .Link("simconnect", "bus")
            .CautionLine("SIMCONNECT LOST");

        var page = a.Merge(b).Build();

        Assert.Equal(2, page.Nodes.Count);
        Assert.Equal(2, page.Links.Count);
        Assert.Single(page.Gauges);
        Assert.Equal("20", page.Readout("rate_datarefs"));
        Assert.Equal("XX", page.Readout("rate_simvars"));
        Assert.Equal("SIMCONNECT LOST", page.CautionLine);
    }

    [Fact]
    public void Merge_LaterNodeWinsOnIdClash_ButFirstCautionLineStays()
    {
        var a = EcamTestSupport.Builder().Node("stt", DiagState.Normal, "412 ms").CautionLine("FIRST");
        var b = EcamTestSupport.Builder().Node("stt", DiagState.Caution, "XX").CautionLine("SECOND");

        var page = a.Merge(b).Build();

        Assert.Equal(DiagState.Caution, page.Nodes["stt"].State);
        Assert.Equal("FIRST", page.CautionLine);
    }

    [Theory]
    [InlineData(DiagState.Normal, DiagState.Normal, DiagState.Normal)]
    [InlineData(DiagState.Normal, DiagState.Caution, DiagState.Caution)]
    [InlineData(DiagState.Caution, DiagState.Warning, DiagState.Warning)]
    [InlineData(DiagState.Normal, DiagState.NoData, DiagState.NoData)]
    [InlineData(DiagState.NotApplicable, DiagState.Normal, DiagState.Normal)]
    [InlineData(DiagState.NoData, DiagState.Caution, DiagState.Caution)]
    public void Link_WithoutState_IsTheWorseEndpoint(DiagState from, DiagState to, DiagState expected)
    {
        var page = EcamTestSupport.Builder()
            .Link("a", "b")
            .Node("a", from, "x")
            .Node("b", to, "y")
            .Build();

        Assert.Equal(expected, Assert.Single(page.Links).State);
        Assert.Equal(expected, page.LinkState("a", "b"));
    }

    [Fact]
    public void Link_ToAMissingNode_ResolvesAgainstNoData()
    {
        var page = EcamTestSupport.Builder().Node("a", DiagState.Normal, "x").Link("a", "ghost").Build();
        Assert.Equal(DiagState.NoData, Assert.Single(page.Links).State);
    }

    [Fact]
    public void Link_WithExplicitState_KeepsIt()
    {
        var page = EcamTestSupport.Builder()
            .Node("a", DiagState.Warning, "x").Node("b", DiagState.Warning, "y")
            .Link("a", "b", DiagState.Normal).Build();
        Assert.Equal(DiagState.Normal, Assert.Single(page.Links).State);
    }

    [Fact]
    public void DowngradeToNoData_KeepsHostsAndIds_DropsValuesReadoutsAndCautionLine()
    {
        var builder = EcamTestSupport.Builder()
            .Node("stt", DiagState.Normal, "412 ms", "large-v3", "MAC MINI")
            .Gauge("e2e", 1800, 0, 5000, 2500, 4000, "MS")
            .Readout("vram", "18.2")
            .CautionLine("X");

        var page = builder.DowngradeToNoData().Build();

        var stt = page.Nodes["stt"];
        Assert.Equal(DiagState.NoData, stt.State);
        Assert.Equal(EcamTokens.NoData, stt.Value);
        Assert.Equal("MAC MINI", stt.Host);
        Assert.Null(stt.Detail);
        Assert.Null(page.Gauges["e2e"].Value);
        Assert.Empty(page.Readouts);
        Assert.Null(page.CautionLine);
    }

    [Fact]
    public void Page_NodeOrNoData_FallsBackToAmberXX()
    {
        var page = DiagPage.Empty(DiagPageIds.Voice);
        var node = page.NodeOrNoData("mic");
        Assert.Equal("mic", node.Id);
        Assert.Equal(DiagState.NoData, node.State);
        Assert.Equal("XX", node.Value);
        Assert.Null(page.GaugeOrNoData("e2e").Value);
    }

    [Fact]
    public void Gauge_ValueState_FollowsThresholds()
    {
        var g = new DiagGauge("e2e", 1000, 0, 5000, 2500, 4000, "MS");
        Assert.Equal(DiagState.Normal, g.ValueState);
        Assert.Equal(DiagState.Caution, (g with { Value = 2500 }).ValueState);
        Assert.Equal(DiagState.Warning, (g with { Value = 4000 }).ValueState);
        Assert.Equal(DiagState.NoData, (g with { Value = null }).ValueState);
    }

    [Fact]
    public void CautionSink_DeduplicatesBySystemAndTitle_AndListsInopOnce()
    {
        var sink = new DiagCautionSink(EcamTestSupport.T0)
            .Raise("VOICE", "LLM HOST NOT RESPONDING", DiagState.Caution, new DiagAction("RETRY", "3 / 10"))
            .Raise("VOICE", "LLM HOST NOT RESPONDING", DiagState.Warning)
            .Inop("LLM BRIEFING").Inop("LLM BRIEFING")
            .Degraded("SIMVAR FEED");

        var caution = Assert.Single(sink.Cautions);
        Assert.Equal(DiagState.Caution, caution.Severity);
        Assert.Single(caution.Actions);
        Assert.Equal(["LLM BRIEFING"], sink.InopSystems);
        Assert.Equal(["SIMVAR FEED"], sink.DegradedSystems);
    }
}
