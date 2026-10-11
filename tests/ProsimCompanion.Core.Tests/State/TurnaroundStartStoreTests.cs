using ProsimCompanion.Core.State;
using Xunit;

namespace ProsimCompanion.Core.Tests.State;

/// <summary>The turnaround "commence ground services?" question (issue #169): asked once per
/// cycle, settled by yes/no or by any other start, reset with the flight cycle.</summary>
public sealed class TurnaroundStartStoreTests
{
    private readonly GroundOpsSignals _signals = new();

    [Fact]
    public void Ask_RaisesOnce_ThenIsANoOp()
    {
        using var store = new TurnaroundStartStore(_signals);
        var raised = 0;
        store.QuestionRaised += (_, _) => raised++;

        Assert.True(store.Ask("Next plan loaded — commence ground services?"));
        Assert.False(store.Ask("again"));

        Assert.Equal(1, raised);
        Assert.True(store.Snapshot().QuestionOpen);
    }

    [Fact]
    public void Accept_SettlesTheQuestion_AndNamesTheSource()
    {
        using var store = new TurnaroundStartStore(_signals);
        (bool Accepted, string Source)? answer = null;
        store.Answered += (_, a) => answer = a;
        store.Ask("q");

        store.Accept("voice");
        store.Accept("web"); // idempotent — one event

        Assert.Equal((true, "voice"), answer);
        Assert.False(store.Snapshot().QuestionOpen);
        Assert.True(store.Snapshot().Accepted);
    }

    [Fact]
    public void Decline_SettlesTheQuestion_AndAskIsNotRepeated()
    {
        using var store = new TurnaroundStartStore(_signals);
        store.Ask("q");

        store.Decline("voice", "captain said 'not yet'");

        Assert.False(store.Snapshot().QuestionOpen);
        Assert.Equal("captain said 'not yet'", store.Snapshot().Declined);
        Assert.False(store.Ask("q")); // declined for the cycle
    }

    [Fact]
    public void AcceptWithoutAQuestion_StillRecords_SoAnotherStartCannotBeAskedAgain()
    {
        // The Start button / "commence ground services" before the FO ever asked.
        using var store = new TurnaroundStartStore(_signals);

        store.Accept("web");

        Assert.True(store.Snapshot().Accepted);
        Assert.False(store.Ask("q"));
    }

    [Fact]
    public void FlightCycleReset_ClearsEverything()
    {
        using var store = new TurnaroundStartStore(_signals);
        store.Ask("q");
        store.Decline("voice", "no");

        _signals.RaiseFlightCycleReset();

        Assert.Equal(TurnaroundStartSnapshot.Empty, store.Snapshot());
        Assert.True(store.Ask("q"));
    }
}
