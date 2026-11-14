using ProsimCompanion.Sim;
using Xunit;

namespace ProsimCompanion.Core.Tests.Sim;

/// <summary>Issue #246: every SimConnect call runs on the pump thread. The dispatcher is the
/// part of that which has no SimConnect in it.</summary>
public sealed class SimPumpDispatcherTests
{
    /// <summary>A real second thread, joined: the test thread stays the pump thread, which a
    /// thread-pool task could not promise (the pool may hand the work to the awaiting thread).</summary>
    private static void OnAnotherThread(Action work)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(failure);
    }

    [Fact]
    public void Post_WithoutASession_IsRefused_AndNothingRuns()
    {
        var pump = new SimPumpDispatcher();
        var ran = false;
        var abandoned = false;

        Assert.False(pump.Post(() => ran = true, _ => abandoned = true));

        Assert.False(ran);
        Assert.False(abandoned);
        Assert.Equal(0, pump.Count);
    }

    [Fact]
    public void Post_FromThePumpThread_RunsAtOnce_WithoutAWake()
    {
        var pump = new SimPumpDispatcher();
        var wakes = 0;
        pump.Attach(() => wakes++);
        var ran = false;

        Assert.True(pump.Post(() => ran = true));

        Assert.True(ran);
        Assert.Equal(0, wakes);
        Assert.Equal(0, pump.Count);
    }

    [Fact]
    public void Post_FromAnotherThread_WaitsForTheDrain_AndRunsOnThePumpThread_InOrder()
    {
        var pump = new SimPumpDispatcher();
        var wakes = 0;
        pump.Attach(() => Interlocked.Increment(ref wakes));
        var pumpThread = Environment.CurrentManagedThreadId;
        var order = new List<int>();
        var threads = new List<int>();

        OnAnotherThread(() =>
        {
            for (var i = 1; i <= 3; i++)
            {
                var n = i;
                Assert.True(pump.Post(() =>
                {
                    order.Add(n);
                    threads.Add(Environment.CurrentManagedThreadId);
                }));
            }
        });

        Assert.Empty(order);
        Assert.Equal(3, pump.Count);
        Assert.Equal(3, wakes);

        Assert.Equal(3, pump.Drain());

        Assert.Equal([1, 2, 3], order);
        Assert.All(threads, t => Assert.Equal(pumpThread, t));
        Assert.Equal(0, pump.Count);
    }

    [Fact]
    public void Detach_TellsEveryWaitingItem_AndRefusesNewWork()
    {
        var pump = new SimPumpDispatcher();
        pump.Attach(() => { });
        var ran = false;
        Exception? told = null;
        OnAnotherThread(() => pump.Post(() => ran = true, ex => told = ex));

        var reason = new InvalidOperationException("MSFS is not connected");
        pump.Detach(reason);

        Assert.False(ran);
        Assert.Same(reason, told);
        Assert.Equal(0, pump.Drain());
        Assert.False(pump.Post(() => ran = true));
        Assert.False(ran);
    }

    [Fact]
    public void AFailingItem_IsReported_AndThePumpCarriesOn()
    {
        var faults = new List<Exception>();
        var pump = new SimPumpDispatcher(faults.Add);
        pump.Attach(() => { });
        var second = false;
        OnAnotherThread(() =>
        {
            pump.Post(() => throw new InvalidOperationException("boom"));
            pump.Post(() => second = true);
        });

        Assert.Equal(2, pump.Drain());

        Assert.True(second);
        Assert.Equal("boom", Assert.Single(faults).Message);
    }
}
