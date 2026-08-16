using ProsimCompanion.Core.State;
using Xunit;

namespace ProsimCompanion.Core.Tests.State;

/// <summary>Session gate edges and windows (campaign #79) — computed once in the store,
/// never re-derived by consumers.</summary>
public sealed class SimSessionStoreTests
{
    private static void Publish(SimSessionStore store, SimSessionPhase phase)
        => store.Publish(SimSessionSnapshot.Empty with { Phase = phase });

    [Fact]
    public void SessionEdges_FireOnLivenessTransitions_NotPhaseShuffles()
    {
        var store = new SimSessionStore();
        var started = 0;
        var ended = 0;
        store.SessionStarted += () => started++;
        store.SessionEnded += () => ended++;

        Publish(store, SimSessionPhase.NotInSession);
        Assert.Equal((0, 0), (started, ended));

        Publish(store, SimSessionPhase.Walkaround);
        Assert.Equal((1, 0), (started, ended));

        // Walkaround → InSession is a phase change WITHIN the live session: no edge.
        Publish(store, SimSessionPhase.InSession);
        Assert.Equal((1, 0), (started, ended));

        // Leaving into Unknown counts as a session end (the verbatim predicate the three
        // consumers used to hand-roll).
        Publish(store, SimSessionPhase.Unknown);
        Assert.Equal((1, 1), (started, ended));

        Publish(store, SimSessionPhase.InSession);
        Assert.Equal((2, 1), (started, ended));
    }

    [Fact]
    public void SessionWindow_ElapsesOnlyInsideALiveSession()
    {
        var store = new SimSessionStore();
        using var window = store.OpenWindow(TimeSpan.Zero);

        Assert.False(window.Elapsed); // no session yet — never elapsed

        Publish(store, SimSessionPhase.InSession);
        Assert.True(window.Elapsed); // zero settle: elapsed the moment the session starts

        Publish(store, SimSessionPhase.NotInSession);
        Assert.False(window.Elapsed); // session ended — un-anchored again
    }

    [Fact]
    public void SessionWindow_HonoursTheSettlePeriod()
    {
        var store = new SimSessionStore();
        Publish(store, SimSessionPhase.InSession);
        using var window = store.OpenWindow(TimeSpan.FromHours(1));

        Assert.False(window.Elapsed); // anchored, but the settle period has not passed
    }

    [Fact]
    public void SessionWindow_AnchorsImmediately_WhenOpenedMidSession()
    {
        var store = new SimSessionStore();
        Publish(store, SimSessionPhase.Walkaround);

        using var window = store.OpenWindow(TimeSpan.Zero);

        Assert.True(window.Elapsed);
    }

    [Fact]
    public void SessionWindow_Restart_ReanchorsWithoutASessionEdge()
    {
        var store = new SimSessionStore();
        Publish(store, SimSessionPhase.InSession);
        using var window = store.OpenWindow(TimeSpan.FromHours(1));

        window.Restart();

        Assert.False(window.Elapsed); // still anchored, fresh clock
    }
}
