using ProsimCompanion.Core.State;
using Xunit;

namespace ProsimCompanion.Core.Tests.State;

/// <summary>
/// The recomposed diagnostics store (campaign #86): the snapshot record is the single source,
/// the mirror-portion replace preserves feature-pushed rows, the rings stay bounded, and
/// value-equal pushes no longer notify (the pre-#86 store fired on every mutator call).
/// </summary>
public sealed class GsxDiagnosticsStoreTests
{
    [Fact]
    public void MirrorUpdate_PreservesTheFeaturePushedRows()
    {
        var store = new GsxDiagnosticsStore();
        store.UpdateGroundPower(true);
        store.UpdateGroundPrep(new GsxGroundPrepView("Reposition", "running"));
        store.RecordDecision(new GsxDecisionView(DateTimeOffset.UtcNow, "test", "because"));

        store.Update(GsxDiagnosticsSnapshot.Empty with { Readiness = "Ready" });

        var snapshot = store.Snapshot();
        Assert.Equal("Ready", snapshot.Readiness);
        Assert.Equal(true, snapshot.GroundPowerConnected);
        Assert.Equal("Reposition", snapshot.GroundPrep?.Stage);
        Assert.Single(snapshot.RecentDecisions);
    }

    [Fact]
    public void Rings_AreBounded_AndNewestFirst()
    {
        var store = new GsxDiagnosticsStore();
        for (var i = 0; i < GsxDiagnosticsStore.RecentDecisionLimit + 10; i++)
        {
            store.RecordDecision(new GsxDecisionView(DateTimeOffset.UtcNow, $"action {i}", "r"));
        }

        var decisions = store.Snapshot().RecentDecisions;
        Assert.Equal(GsxDiagnosticsStore.RecentDecisionLimit, decisions.Count);
        Assert.Equal($"action {GsxDiagnosticsStore.RecentDecisionLimit + 9}", decisions[0].Action);
    }

    [Fact]
    public void ValueEqualPush_DoesNotNotify_ButARealChangeDoes()
    {
        var store = new GsxDiagnosticsStore();
        store.UpdateBoardingCounters(new GsxBoardingCountersView(100, 10, null, null, null));
        var fired = 0;
        using var subscription = store.Observe(_ => fired++);

        // The 1 Hz boarding tick pushing identical counters: no subscriber churn (pre-#86
        // this fired into every Blazor circuit once a second).
        store.UpdateBoardingCounters(new GsxBoardingCountersView(100, 10, null, null, null));
        Assert.Equal(0, fired);

        store.UpdateBoardingCounters(new GsxBoardingCountersView(100, 11, null, null, null));
        Assert.Equal(1, fired);
    }
}
