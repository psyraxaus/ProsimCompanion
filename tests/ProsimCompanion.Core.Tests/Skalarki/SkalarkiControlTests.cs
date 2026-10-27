using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Hardware.Skalarki;
using ProsimCompanion.Core.State;
using ProsimCompanion.Skalarki;
using Xunit;

namespace ProsimCompanion.Core.Tests.Skalarki;

/// <summary>The service's ISkalarkiControl side: lamp test timing on a manual clock, HW reset,
/// and the hardware check fed from client events.</summary>
public sealed class SkalarkiControlTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 11, 0, 0, TimeSpan.Zero);

    private static async Task<(SkalarkiConnectionService Service, SkalarkiServiceTests.FakeClient Client, SkalarkiStatusStore Store, ManualClock Clock)> ConnectedAsync(int lampTestMs = 2000)
    {
        var client = new SkalarkiServiceTests.FakeClient();
        var store = new SkalarkiStatusStore();
        var clock = new ManualClock(T0);
        var options = new FixedOptionsMonitor<SkalarkiOptions>(new SkalarkiOptions { Enabled = true, SdkPath = @"C:\sdk", LampTestMs = lampTestMs });
        var service = new SkalarkiConnectionService(options, new Factory(client), store, new ConnectionStatusStore(), NullLogger<SkalarkiConnectionService>.Instance, clock);
        await service.StartAsync(CancellationToken.None);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (store.Snapshot().LinkState != ConnectionState.Connected && sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(10);
        }

        Assert.Equal(ConnectionState.Connected, store.Snapshot().LinkState);
        client.RaiseDevice(SkalarkiDeviceId.Glare, true);
        client.RaiseDevice(SkalarkiDeviceId.Mcdu1, true);
        client.RaiseDevice(SkalarkiDeviceId.Rmp2, false);
        await Task.Delay(20);
        return (service, client, store, clock);
    }

    [Fact]
    public async Task LampTest_LightsEveryConnectedBoard_ThenTurnsThemOffAfterTheConfiguredHold()
    {
        var (service, client, _, clock) = await ConnectedAsync(lampTestMs: 2000);

        var test = service.LampTestAsync(null, CancellationToken.None);
        await Task.Delay(50);
        Assert.False(test.IsCompleted);
        Assert.Equal([(SkalarkiDeviceId.Glare, true), (SkalarkiDeviceId.Mcdu1, true)], client.Outputs);   // RMP2 is not connected

        clock.Advance(TimeSpan.FromSeconds(1.9));
        await Task.Delay(50);
        Assert.False(test.IsCompleted);

        clock.Advance(TimeSpan.FromSeconds(0.2));
        Assert.Equal("2 LIT", await test.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal([(SkalarkiDeviceId.Glare, true), (SkalarkiDeviceId.Mcdu1, true), (SkalarkiDeviceId.Glare, false), (SkalarkiDeviceId.Mcdu1, false)], client.Outputs);

        client.Outputs.Clear();
        var one = service.LampTestAsync(SkalarkiDeviceId.Glare, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal("1 LIT", await one.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal([(SkalarkiDeviceId.Glare, true), (SkalarkiDeviceId.Glare, false)], client.Outputs);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task LampTest_WithoutALink_SaysSo_AndHardwareReset_IsSent()
    {
        var store = new SkalarkiStatusStore();
        var off = new SkalarkiConnectionService(
            new FixedOptionsMonitor<SkalarkiOptions>(new SkalarkiOptions()), new Factory(new SkalarkiServiceTests.FakeClient()), store,
            new ConnectionStatusStore(), NullLogger<SkalarkiConnectionService>.Instance);
        Assert.Equal("NO LINK", await off.LampTestAsync(null, CancellationToken.None));
        Assert.Equal("NO LINK", await off.HardwareResetAsync(SkalarkiDeviceId.Overhead, false, CancellationToken.None));

        var (service, _, _, _) = await ConnectedAsync();
        Assert.Equal("SENT", await service.HardwareResetAsync(SkalarkiDeviceId.Overhead, true, CancellationToken.None));
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HardwareCheck_RunsFromClientEvents_RestartsAndStops()
    {
        var (service, client, store, _) = await ConnectedAsync();

        service.StartCheck(new Dictionary<SkalarkiDeviceId, string> { [SkalarkiDeviceId.Mcdu1] = "PRESS CLR" });
        var check = store.Snapshot().Check!;
        Assert.Equal(2, check.Total);                                                  // GLARE + MCDU1; RMP2 not enabled (never connected)
        Assert.Equal("PRESS CLR", check.Items.Single(i => i.Device == SkalarkiDeviceId.Mcdu1).Prompt);

        client.RaiseEvent(new SkalarkiHardwareEvent(SkalarkiDeviceId.Mcdu1, "MCDUCS", SkalarkiSourceKind.Switch, "MCDU1_CLR", "ON", T0.AddSeconds(3)));
        await Task.Delay(20);
        Assert.Equal(1, store.Snapshot().Check!.Done);

        client.RaiseDevice(SkalarkiDeviceId.Glare, false);
        await Task.Delay(20);
        Assert.Equal(SkalarkiCheckState.Lost, store.Snapshot().Check!.Items.Single(i => i.Device == SkalarkiDeviceId.Glare).State);
        client.RaiseDevice(SkalarkiDeviceId.Glare, true);
        await Task.Delay(20);
        Assert.Equal(SkalarkiCheckState.Pending, store.Snapshot().Check!.Items.Single(i => i.Device == SkalarkiDeviceId.Glare).State);

        service.RestartCheck();
        Assert.Equal(0, store.Snapshot().Check!.Done);
        service.StopCheck();
        Assert.Null(store.Snapshot().Check);
        service.RestartCheck();                                                        // nothing running: no-op
        Assert.Null(store.Snapshot().Check);

        await service.StopAsync(CancellationToken.None);
    }

    private sealed class Factory(ISkalarkiClient client) : ISkalarkiClientFactory
    {
        public string? Validate(string sdkPath) => null;

        public ISkalarkiClient Create(string sdkPath) => client;
    }
}

/// <summary>A clock the test moves by hand; timers (Task.Delay / PeriodicTimer on this
/// provider) fire when <see cref="Advance"/> passes their due time.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by)
    {
        _now += by;
        ManualTimer[] due;
        lock (_timers)
        {
            due = [.. _timers.Where(t => t.Due is { } d && d <= _now)];
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state, dueTime, period);
        lock (_timers)
        {
            _timers.Add(timer);
        }

        if (dueTime == TimeSpan.Zero)
        {
            timer.Fire();
        }

        return timer;
    }

    private sealed class ManualTimer(ManualClock owner, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) : ITimer
    {
        private TimeSpan _period = period;

        public DateTimeOffset? Due { get; private set; } = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;

        public void Fire()
        {
            if (_period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero)
            {
                Due = null;
                lock (owner._timers)
                {
                    owner._timers.Remove(this);
                }
            }
            else
            {
                Due = owner._now + _period;
            }

            callback(state);
        }

        public bool Change(TimeSpan newDueTime, TimeSpan newPeriod)
        {
            _period = newPeriod;
            Due = newDueTime == Timeout.InfiniteTimeSpan ? null : owner._now + newDueTime;
            return true;
        }

        public void Dispose()
        {
            lock (owner._timers)
            {
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
