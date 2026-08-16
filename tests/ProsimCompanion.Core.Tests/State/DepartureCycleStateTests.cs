using ProsimCompanion.Core.State;
using Xunit;

namespace ProsimCompanion.Core.Tests.State;

public sealed class DepartureCycleStateTests
{
    [Fact]
    public void MarkStarted_SetsStarted_AndClearsAStaleComplete()
    {
        var cycle = new DepartureCycleState();
        cycle.MarkStarted();
        cycle.MarkComplete();

        // A restart mid-cycle (Started already true) is a no-op…
        cycle.MarkStarted();
        Assert.True(cycle.Complete);

        // …but a fresh start after a cycle reset clears Complete.
        cycle.BeginTurnaroundCycle();
        cycle.MarkComplete();
        cycle.MarkStarted();
        Assert.True(cycle.Started);
        Assert.False(cycle.Complete);
    }

    [Fact]
    public void Changed_FiresOnlyOnActualTransitions()
    {
        var cycle = new DepartureCycleState();
        var changes = 0;
        cycle.Changed += () => changes++;

        cycle.MarkStarted();
        cycle.MarkStarted(); // no-op
        cycle.MarkPrepComplete();
        cycle.MarkPrepComplete(); // no-op
        cycle.ResetPrep();
        cycle.ResetPrep(); // no-op

        Assert.Equal(3, changes);
    }

    [Fact]
    public void BeginTurnaroundCycle_ResetsFlags_LatchesTurnaround_AndFiresCycleReset()
    {
        var cycle = new DepartureCycleState();
        cycle.MarkStarted();
        cycle.MarkComplete();
        cycle.MarkPrepComplete();
        var order = new List<string>();
        cycle.Changed += () => order.Add("changed");
        cycle.CycleReset += () => order.Add("reset");

        cycle.BeginTurnaroundCycle();

        Assert.False(cycle.Started);
        Assert.False(cycle.Complete);
        Assert.False(cycle.PrepComplete);
        Assert.True(cycle.IsTurnaround);
        // Flags settle (Changed) before feature latches reset (CycleReset) — a latch handler
        // reading the store must see the fresh cycle.
        Assert.Equal(["changed", "reset"], order);
    }

    [Fact]
    public void Turnaround_IsStickyAcrossCycles()
    {
        var cycle = new DepartureCycleState();
        cycle.MarkTurnaround();
        cycle.BeginTurnaroundCycle();

        Assert.True(cycle.IsTurnaround);
    }
}
