using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx;
using ProsimCompanion.Gsx.Automation;
using ProsimCompanion.Gsx.Mirror;
using ProsimCompanion.Gsx.Protocol;
using ProsimCompanion.Gsx.Services;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>
/// The trigger slot is the single serialized service.trigger path (campaign #77). These tests
/// drive the confirm/timeout watcher with a short confirm window so drop/retry paths run in
/// real time without long sleeps.
/// </summary>
public sealed class GsxTriggerSlotTests : IDisposable
{
    private static readonly TimeSpan ShortWindow = TimeSpan.FromMilliseconds(300);

    private readonly Mock<IGsxRemoteApi> _api = new();
    private readonly GsxStateMirror _mirror = new();
    private readonly GsxServiceLifecycleTracker _lifecycle = new(NullLogger<GsxServiceLifecycleTracker>.Instance);
    private readonly GsxDiagnosticsStore _diagnostics = new();
    private readonly string _sessionsDir = Path.Combine(Path.GetTempPath(), $"slot-tests-{Guid.NewGuid():N}");
    private readonly JsonlEventLog _eventLog;
    private readonly List<string> _sentServices = [];
    private GsxCommandResult _sendResult = new(true, "ok", null, null);

    public GsxTriggerSlotTests()
    {
        _eventLog = new JsonlEventLog(_sessionsDir, NullLogger<JsonlEventLog>.Instance);
        _api.SetupGet(a => a.Mirror).Returns(_mirror);
        _api
            .Setup(a => a.SendCommandAsync("service.trigger", It.IsAny<JsonObject>(), It.IsAny<CancellationToken>()))
            .Callback((string _, JsonObject? args, CancellationToken _) =>
            {
                lock (_sentServices)
                {
                    _sentServices.Add((string?)args?["service"] ?? "");
                }
            })
            .ReturnsAsync(() => _sendResult);
    }

    public void Dispose()
    {
        _eventLog.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try
        {
            Directory.Delete(_sessionsDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private GsxTriggerSlot CreateSlot()
    {
        var options = new Mock<IOptionsMonitor<GsxOptions>>();
        options.SetupGet(o => o.CurrentValue).Returns(new GsxOptions());
        return new GsxTriggerSlot(
            _api.Object,
            _lifecycle,
            options.Object,
            _diagnostics,
            _eventLog,
            NullLogger<GsxTriggerSlot>.Instance);
    }

    /// <summary>Seeds the mirror with one service in the given semantic wire state.</summary>
    private void SeedService(string id, string state)
        => _mirror.ApplyState("services", new JsonArray(new JsonObject
        {
            ["id"] = id,
            ["state"] = state,
            ["canTrigger"] = true,
        }));

    private async Task<GsxTriggerResolution> WaitForResolutionAsync(TaskCompletionSource<GsxTriggerResolution> tcs)
    {
        var winner = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(tcs.Task, winner);
        return await tcs.Task;
    }

    [Fact]
    public async Task Dispatch_SendsTheTrigger_AndOccupiesTheSlot()
    {
        using var slot = CreateSlot();

        var dispatch = await slot.TryDispatchAsync(
            new GsxTriggerRequest("Refueling", "test") { ConfirmWindow = TimeSpan.FromSeconds(5) });

        Assert.Equal(GsxTriggerDispatchStatus.Dispatched, dispatch.Status);
        Assert.Equal("Refueling", slot.InFlightServiceId);
        Assert.Equal(["Refueling"], _sentServices);
    }

    [Fact]
    public async Task SecondDispatch_WhileOccupied_IsBusy_NamingTheHolder()
    {
        using var slot = CreateSlot();
        await slot.TryDispatchAsync(
            new GsxTriggerRequest("Refueling", "test") { ConfirmWindow = TimeSpan.FromSeconds(5) });

        var second = await slot.TryDispatchAsync(
            new GsxTriggerRequest("Catering", "test") { SlotWait = TimeSpan.Zero });

        Assert.Equal(GsxTriggerDispatchStatus.Busy, second.Status);
        Assert.Equal("Refueling", second.BusyServiceId);
        Assert.Equal(["Refueling"], _sentServices);
    }

    [Fact]
    public async Task RejectedSend_FreesTheSlotImmediately()
    {
        using var slot = CreateSlot();
        _sendResult = new(false, "not_connected", null, null);

        var dispatch = await slot.TryDispatchAsync(new GsxTriggerRequest("Refueling", "test"));

        Assert.Equal(GsxTriggerDispatchStatus.Rejected, dispatch.Status);
        Assert.Equal("not_connected", dispatch.RejectCode);
        Assert.Null(slot.InFlightServiceId);
    }

    [Fact]
    public async Task MirrorEdge_ConfirmsTheTrigger_AndMarksTheCycleCalled()
    {
        using var slot = CreateSlot();
        var tcs = new TaskCompletionSource<GsxTriggerResolution>(TaskCreationOptions.RunContinuationsAsynchronously);
        await slot.TryDispatchAsync(new GsxTriggerRequest("Refueling", "test")
        {
            ConfirmWindow = TimeSpan.FromSeconds(5),
            OnResolved = tcs.SetResult,
        });
        Assert.False(_lifecycle.IsPending("Refueling"));

        SeedService("Refueling", "requested");

        Assert.Equal(GsxTriggerResolution.Confirmed, await WaitForResolutionAsync(tcs));
        Assert.Null(slot.InFlightServiceId);
        // MarkCalled happens on confirmation, never before the send (campaign #77).
        Assert.True(_lifecycle.IsPending("Refueling"));
    }

    [Fact]
    public async Task SilentDrop_WithoutRetry_ResolvesDropped_AndFreesTheSlot()
    {
        using var slot = CreateSlot();
        var tcs = new TaskCompletionSource<GsxTriggerResolution>(TaskCreationOptions.RunContinuationsAsynchronously);

        await slot.TryDispatchAsync(new GsxTriggerRequest("Refueling", "test")
        {
            ConfirmWindow = ShortWindow,
            OnResolved = tcs.SetResult,
        });

        Assert.Equal(GsxTriggerResolution.Dropped, await WaitForResolutionAsync(tcs));
        Assert.Null(slot.InFlightServiceId);
        Assert.Equal(["Refueling"], _sentServices);
        Assert.False(_lifecycle.IsPending("Refueling"));
    }

    [Fact]
    public async Task SilentDrop_WithRetryOnce_ResendsExactlyOnce_ThenResolvesDropped()
    {
        using var slot = CreateSlot();
        var tcs = new TaskCompletionSource<GsxTriggerResolution>(TaskCreationOptions.RunContinuationsAsynchronously);

        await slot.TryDispatchAsync(new GsxTriggerRequest("GPU", "test")
        {
            ConfirmWindow = ShortWindow,
            RetryOnce = true,
            OnResolved = tcs.SetResult,
        });

        Assert.Equal(GsxTriggerResolution.Dropped, await WaitForResolutionAsync(tcs));
        Assert.Equal(["GPU", "GPU"], _sentServices);
        Assert.Null(slot.InFlightServiceId);
    }

    [Fact]
    public async Task RetriedTrigger_ConfirmedOnSecondAttempt_ResolvesConfirmed()
    {
        using var slot = CreateSlot();
        var tcs = new TaskCompletionSource<GsxTriggerResolution>(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmAfterSecondSend = false;
        _api
            .Setup(a => a.SendCommandAsync("service.trigger", It.IsAny<JsonObject>(), It.IsAny<CancellationToken>()))
            .Callback((string _, JsonObject? args, CancellationToken _) =>
            {
                lock (_sentServices)
                {
                    _sentServices.Add((string?)args?["service"] ?? "");
                    if (_sentServices.Count == 2 && !confirmAfterSecondSend)
                    {
                        confirmAfterSecondSend = true;
                        SeedService("GPU", "requested");
                    }
                }
            })
            .ReturnsAsync(() => _sendResult);

        await slot.TryDispatchAsync(new GsxTriggerRequest("GPU", "test")
        {
            ConfirmWindow = ShortWindow,
            RetryOnce = true,
            OnResolved = tcs.SetResult,
        });

        Assert.Equal(GsxTriggerResolution.Confirmed, await WaitForResolutionAsync(tcs));
        Assert.Equal(["GPU", "GPU"], _sentServices);
    }

    [Fact]
    public async Task NoConfirmSend_HoldsTheSlotForSpacing_ThenFrees()
    {
        using var slot = CreateSlot();

        var dispatch = await slot.TryDispatchAsync(
            new GsxTriggerRequest("OperateStairs", "test") { NoConfirm = true });

        Assert.Equal(GsxTriggerDispatchStatus.Dispatched, dispatch.Status);
        // Occupied during the spacing interval — a back-to-back second toggle is refused,
        // which is the fix for the zero-spacing jetway+stairs removal pair.
        Assert.Equal("OperateStairs", slot.InFlightServiceId);
        var second = await slot.TryDispatchAsync(
            new GsxTriggerRequest("OperateJetways", "test") { NoConfirm = true, SlotWait = TimeSpan.Zero });
        Assert.Equal(GsxTriggerDispatchStatus.Busy, second.Status);

        // The spacing wait (2 s) then frees the slot on its own.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (slot.InFlightServiceId is not null && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100);
        }
        Assert.Null(slot.InFlightServiceId);
    }

    [Fact]
    public async Task SlotWait_OutlastsTheHolder_AndDispatches()
    {
        using var slot = CreateSlot();
        await slot.TryDispatchAsync(new GsxTriggerRequest("Refueling", "test")
        {
            ConfirmWindow = ShortWindow, // drops ~300 ms in, freeing the slot
        });

        var second = await slot.TryDispatchAsync(new GsxTriggerRequest("Catering", "test")
        {
            SlotWait = TimeSpan.FromSeconds(8),
            ConfirmWindow = TimeSpan.FromSeconds(5),
        });

        Assert.Equal(GsxTriggerDispatchStatus.Dispatched, second.Status);
        Assert.Equal(["Refueling", "Catering"], _sentServices);
    }

    [Fact]
    public async Task Reset_ClearsTheInFlightTrigger_AndItsWatcherNeverResolves()
    {
        using var slot = CreateSlot();
        var resolved = false;
        await slot.TryDispatchAsync(new GsxTriggerRequest("Refueling", "test")
        {
            ConfirmWindow = TimeSpan.FromSeconds(5),
            OnResolved = _ => resolved = true,
        });

        slot.Reset("arrival cycle boundary");

        Assert.Null(slot.InFlightServiceId);
        SeedService("Refueling", "requested");
        await Task.Delay(1200); // two watcher polls — it must have exited, not confirmed
        Assert.False(resolved);
        Assert.False(_lifecycle.IsPending("Refueling"));
    }

    [Fact]
    public async Task ChangedEvent_FiresOnDispatchOutcomes()
    {
        using var slot = CreateSlot();
        var changes = 0;
        slot.Changed += () => Interlocked.Increment(ref changes);
        _sendResult = new(false, "timeout", null, null);

        await slot.TryDispatchAsync(new GsxTriggerRequest("Refueling", "test"));

        Assert.True(changes >= 1);
    }
}
