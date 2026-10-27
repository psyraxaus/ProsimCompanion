using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Diagnostics.Ecam;
using Xunit;

namespace ProsimCompanion.Core.Tests.Diagnostics.Ecam;

/// <summary>The tick core without a timer: a throwing collector yields NoData and one log
/// line per failure streak; cautions keep their raise time and write RECENT edges; the memo
/// carries the counts; collectors on the wrong page are ignored.</summary>
public sealed class DiagnosticsSnapshotCoreTests
{
    private static readonly DateTimeOffset T0 = EcamTestSupport.T0;

    [Fact]
    public void ThrowingCollector_YieldsNoDataNodes_AndLogsOncePerStreak()
    {
        var logger = new CountingLogger();
        var core = new DiagnosticsSnapshotCore(logger);
        var log = new DiagEventLog();
        var collectors = new IDiagnosticsCollector[]
        {
            new DelegateCollector(DiagPageIds.Voice, (p, _) =>
            {
                p.Node("stt", DiagState.Normal, "412 ms", host: "MAC MINI");
                throw new InvalidOperationException("boom");
            }),
            new DelegateCollector(DiagPageIds.Voice, (p, _) => p.Node("tts", DiagState.Normal, "86 ms")),
        };

        DiagnosticsSnapshot? snap = null;
        for (var i = 0; i < 5; i++)
        {
            snap = core.ProcessTick(T0.AddSeconds(i), collectors, new FixedMemoSource(), log, snap);
        }

        Assert.NotNull(snap);
        var stt = snap.Voice.Nodes["stt"];
        Assert.Equal(DiagState.NoData, stt.State);
        Assert.Equal("XX", stt.Value);
        Assert.Equal("MAC MINI", stt.Host);
        Assert.Equal(DiagState.Normal, snap.Voice.Nodes["tts"].State);
        Assert.Equal(1, logger.Warnings);
        Assert.Contains(snap.Recent, e => e.Text == "VOICE COLLECTOR FAULT" && e.State == DiagState.Caution);
        Assert.Single(snap.Recent, e => e.Text == "VOICE COLLECTOR FAULT");
    }

    [Fact]
    public void RecoveringCollector_LogsAgainOnTheNextStreak()
    {
        var logger = new CountingLogger();
        var core = new DiagnosticsSnapshotCore(logger);
        var log = new DiagEventLog();
        var fail = true;
        var collectors = new IDiagnosticsCollector[]
        {
            new DelegateCollector(DiagPageIds.Sim, (p, _) =>
            {
                if (fail)
                {
                    throw new TimeoutException();
                }

                p.Node("prosim", DiagState.Normal, "CONN");
            }),
        };

        var snap = core.ProcessTick(T0, collectors, new FixedMemoSource(), log, null);
        fail = false;
        snap = core.ProcessTick(T0.AddSeconds(1), collectors, new FixedMemoSource(), log, snap);
        Assert.Equal(DiagState.Normal, snap.Sim.Nodes["prosim"].State);
        fail = true;
        core.ProcessTick(T0.AddSeconds(2), collectors, new FixedMemoSource(), log, snap);

        Assert.Equal(2, logger.Warnings);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("reporting again", StringComparison.Ordinal));
    }

    [Fact]
    public void Cautions_KeepTheirRaiseTime_AndCountIntoTheMemo()
    {
        var core = new DiagnosticsSnapshotCore(new CountingLogger());
        var log = new DiagEventLog();
        var collectors = new IDiagnosticsCollector[]
        {
            new DelegateCollector(DiagPageIds.Voice, (_, c) =>
            {
                c.Raise("VOICE", "LLM HOST NOT RESPONDING", DiagState.Caution);
                c.Raise("SIM", "SIMCONNECT LOST", DiagState.Warning);
                c.Inop("LLM BRIEFING");
                c.Degraded("SIMVAR FEED");
            }),
        };

        var first = core.ProcessTick(T0, collectors, new FixedMemoSource(), log, null);
        var second = core.ProcessTick(T0.AddSeconds(30), collectors, new FixedMemoSource(), log, first);

        Assert.Equal(2, second.Memo.CautionCount);
        Assert.Equal(1, second.Memo.WarningCount);
        Assert.Equal("0.8.0-test", second.Memo.Version);
        Assert.All(second.Cautions, c => Assert.Equal(T0, c.RaisedUtc));
        Assert.Equal("SIMCONNECT LOST", second.Cautions[0].Title); // warnings first
        Assert.Equal(["LLM BRIEFING"], second.InopSystems);
        Assert.Equal(["SIMVAR FEED"], second.Degraded);

        // RECENT: one raise line per caution on the first tick, none on the second.
        Assert.Equal(2, second.Recent.Count(e => e.Text.Contains("LLM HOST", StringComparison.Ordinal) || e.Text.Contains("SIMCONNECT", StringComparison.Ordinal)));
    }

    [Fact]
    public void Cautions_ClearingWritesAGreenRecentLine()
    {
        var core = new DiagnosticsSnapshotCore(new CountingLogger());
        var log = new DiagEventLog();
        var raise = true;
        var collectors = new IDiagnosticsCollector[]
        {
            new DelegateCollector(DiagPageIds.Gsx, (_, c) =>
            {
                if (raise)
                {
                    c.Raise("GSX", "CATERING QUEUED", DiagState.Caution);
                }
            }),
        };

        var snap = core.ProcessTick(T0, collectors, new FixedMemoSource(), log, null);
        raise = false;
        snap = core.ProcessTick(T0.AddSeconds(1), collectors, new FixedMemoSource(), log, snap);

        Assert.Empty(snap.Cautions);
        Assert.Equal(0, snap.Memo.CautionCount);
        Assert.Equal(DiagState.Normal, snap.Recent[0].State);
        Assert.Equal("GSX CATERING QUEUED", snap.Recent[0].Text);
        Assert.Equal(DiagState.Caution, snap.Recent[1].State);
    }

    [Fact]
    public void CollectorOnAnUnknownPage_IsIgnored_AndThePagesStillBuild()
    {
        var logger = new CountingLogger();
        var core = new DiagnosticsSnapshotCore(logger);
        var collectors = new IDiagnosticsCollector[]
        {
            new DelegateCollector("status", (p, _) => p.Node("x", DiagState.Normal, "y")),
        };

        var snap = core.ProcessTick(T0, collectors, new FixedMemoSource(), new DiagEventLog(), null);

        Assert.Empty(snap.Voice.Nodes);
        Assert.Equal(1, logger.Warnings);
        Assert.Equal(T0, snap.AtUtc);
    }

    [Fact]
    public void NoCollectors_YieldsEmptyPages_NeverThrows()
    {
        var core = new DiagnosticsSnapshotCore(new CountingLogger());
        var snap = core.ProcessTick(T0, [], new FixedMemoSource(), new DiagEventLog(), null);
        Assert.All(DiagPageIds.Drawing, id => Assert.True(snap.Page(id) is null || snap.Page(id)!.Nodes.Count == 0));
        Assert.Null(snap.Panels);   // nothing drew on PANELS → hidden, not "all XX"
        Assert.Null(snap.Page(DiagPageIds.Status));
    }
}
