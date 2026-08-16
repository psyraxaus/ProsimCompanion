using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Tests.TechLog;
using ProsimCompanion.Prosim.DataRefs;
using Xunit;

namespace ProsimCompanion.Core.Tests.Prosim;

/// <summary>
/// Covers the push dispatcher that decouples subscriber callbacks from the SDK receive thread
/// (issue #35: a blocked subscriber froze every dataref cache and the whole SDK session with it).
/// </summary>
public sealed class ProsimDataRefServiceTests : IAsyncDisposable
{
    private readonly ProsimDataRefService _service = new(
        OptionsSupport.Monitor(new ProsimOptions()),
        NullLogger<ProsimDataRefService>.Instance);

    public ValueTask DisposeAsync() => _service.DisposeAsync();

    [Fact]
    public async Task UpdateFromPush_DeliversValueToSubscriber()
    {
        using var subscription = _service.SubscribeDynamic("aircraft.speed.ias", DataRefTier.Frequent);
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        subscription.ValueChanged += (_, _) => notified.TrySetResult();

        _service.UpdateFromPush("aircraft.speed.ias", 142.0);

        await notified.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(142.0, subscription.GetValue(0.0));
        Assert.False(subscription.IsStale);
    }

    [Fact]
    public async Task UpdateFromPush_ReturnsEvenWhileASubscriberBlocks()
    {
        using var handlerEntered = new ManualResetEventSlim(false);
        using var releaseHandler = new ManualResetEventSlim(false);
        using var subscription = _service.SubscribeDynamic("system.gates.B_GROUND", DataRefTier.Frequent);
        subscription.ValueChanged += (_, _) =>
        {
            handlerEntered.Set();
            releaseHandler.Wait(TimeSpan.FromSeconds(10));
        };

        // Neither call may block, even though the first dispatch is stuck in the handler.
        _service.UpdateFromPush("system.gates.B_GROUND", 1);
        Assert.True(handlerEntered.Wait(TimeSpan.FromSeconds(5)));
        _service.UpdateFromPush("system.gates.B_GROUND", 0);

        // The stall probe names the dataref whose subscriber is blocking dispatch.
        Assert.True(_service.TryGetStalledDispatch(0, out var stalledRef, out var startedTicks));
        Assert.Equal("system.gates.B_GROUND", stalledRef);
        Assert.NotEqual(0, startedTicks);

        releaseHandler.Set();
        await WaitUntilAsync(() => subscription.GetValue(-1) == 0);
        await WaitUntilAsync(() => !_service.TryGetStalledDispatch(0, out _, out _));
    }

    [Fact]
    public async Task DetachBackend_MarksCachesStaleAfterPendingPushesApply()
    {
        using var subscription = _service.SubscribeDynamic("aircraft.altitude", DataRefTier.Critical);

        _service.UpdateFromPush("aircraft.altitude", 3500.0);
        _service.DetachBackend();

        await WaitUntilAsync(() => subscription.IsStale);

        // "Valid or hold previous decision": the queued value applied before the stale flag.
        Assert.Equal(3500.0, subscription.GetValue(0.0));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < deadline, "condition not met within 5 s");
            await Task.Delay(10);
        }
    }
}
