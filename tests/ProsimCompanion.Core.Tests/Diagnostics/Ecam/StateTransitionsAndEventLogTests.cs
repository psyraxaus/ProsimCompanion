using ProsimCompanion.Core.Diagnostics.Ecam;
using Xunit;

namespace ProsimCompanion.Core.Tests.Diagnostics.Ecam;

public sealed class StateTransitionsAndEventLogTests
{
    [Fact]
    public void StateTransitions_ReportsOnlyChanges_AndFirstSightingOnlyWhenAbnormal()
    {
        var t = new StateTransitions<string>();

        var first = t.Observe([new("stt", DiagState.Normal), new("llm", DiagState.Caution)]);
        Assert.Equal(["llm"], first.Select(c => c.Id));

        var same = t.Observe([new("stt", DiagState.Normal), new("llm", DiagState.Caution)]);
        Assert.Empty(same);

        var changed = t.Observe([new("stt", DiagState.Caution), new("llm", DiagState.Normal)]);
        Assert.Equal(2, changed.Count);
        Assert.Contains(changed, c => c.Id == "stt" && c.From == DiagState.Normal && c.To == DiagState.Caution);
        Assert.Contains(changed, c => c.Id == "llm" && c.From == DiagState.Caution && c.To == DiagState.Normal);
    }

    [Fact]
    public void StateTransitions_SingleKey_Changed()
    {
        var t = new StateTransitions<string>();
        Assert.False(t.Changed("a", DiagState.Normal, out _));
        Assert.False(t.Changed("a", DiagState.Normal, out _));
        Assert.True(t.Changed("a", DiagState.Warning, out var previous));
        Assert.Equal(DiagState.Normal, previous);
        Assert.True(t.Changed("b", DiagState.Caution, out _));
    }

    [Fact]
    public void EventLog_KeepsFifty_NewestFirst()
    {
        var log = new DiagEventLog();
        for (var i = 0; i < 60; i++)
        {
            log.Push(new DiagEvent(EcamTestSupport.T0.AddSeconds(i), $"E{i}", DiagState.Normal));
        }

        var snap = log.Snapshot();
        Assert.Equal(DiagEventLog.Capacity, snap.Count);
        Assert.Equal("E59", snap[0].Text);
        Assert.Equal("E10", snap[^1].Text);
    }

    [Fact]
    public void EventSink_StampsEveryPushWithTheTickInstant()
    {
        var log = new DiagEventLog();
        var sink = new DiagEventSink(log, EcamTestSupport.T0);
        sink.Push("STT OK 412 MS");
        sink.Push("MIXER 3 LOST", DiagState.Caution);
        var snap = log.Snapshot();
        Assert.All(snap, e => Assert.Equal(EcamTestSupport.T0, e.AtUtc));
        Assert.Equal(DiagState.Caution, snap[0].State);
    }
}
