using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Automation;
using ProsimCompanion.Gsx.Sync;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>
/// The one tracking-LVAR writer (issue #230): every bulk reset goes through the MSFS-down
/// queue, so a new-plan clear issued while SimConnect is down is applied on the connected edge
/// instead of being lost (the direct writes it replaced cleared nothing, and the next restart
/// resynced to the OLD leg's progress).
/// </summary>
public sealed class CompanionTrackingLvarsTests : IDisposable
{
    private readonly RecordingSimVars _sim = new();
    private readonly ConnectionStatusStore _status = new();
    private CompanionTrackingLvars? _writer;

    public void Dispose() => _writer?.Dispose();

    private CompanionTrackingLvars CreateWriter(bool connected)
    {
        _sim.Connected = connected;
        _status.Set(Subsystems.SimConnect, connected ? ConnectionState.Connected : ConnectionState.Disconnected);
        _writer = new CompanionTrackingLvars(_sim, _status, NullLogger<CompanionTrackingLvars>.Instance);
        return _writer;
    }

    private void Reconnect()
    {
        _sim.Connected = true;
        _status.Set(Subsystems.SimConnect, ConnectionState.Connected);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25);
        }
        Assert.True(condition(), what);
    }

    private static string Done(string serviceId) => CompanionLvarNames.ServiceDone(serviceId).Name;

    private static readonly string[] DepartureSideNames =
    [
        .. NewPlanCycleReset.DepartureServices.Select(Done),
        CompanionLvarNames.LoadsheetPrelimEdition.Name,
        CompanionLvarNames.LoadsheetFinalSent.Name,
    ];

    [Fact]
    public async Task ClearDepartureSide_WhileConnected_WritesAtOnce()
    {
        var writer = CreateWriter(connected: true);

        await writer.ClearDepartureSideAsync();

        Assert.True(writer.IsSimConnected);
        Assert.Equal(0, writer.PendingCount);
        Assert.Equal(DepartureSideNames.Order(), _sim.Writes.Select(w => w.Name).Order());
        Assert.All(_sim.Writes, w => Assert.Equal(0, w.Value));
    }

    /// <summary>The #230 case: a different plan lands while MSFS is down — the clear waits in
    /// the queue and is written when SimConnect comes back, every LVAR of it.</summary>
    [Fact]
    public async Task ClearDepartureSide_WhileDisconnected_IsAppliedOnTheConnectedEdge()
    {
        var writer = CreateWriter(connected: false);

        await writer.ClearDepartureSideAsync();

        Assert.False(writer.IsSimConnected);
        Assert.Empty(_sim.Writes);
        Assert.Equal(DepartureSideNames.Length, writer.PendingCount);

        Reconnect();
        await WaitUntilAsync(() => writer.PendingCount == 0 && _sim.Writes.Length >= DepartureSideNames.Length, "the queue flushes on the connected edge");

        Assert.True(writer.IsSimConnected);
        Assert.Equal(DepartureSideNames.Order(), _sim.Writes.Select(w => w.Name).Order());
        Assert.All(_sim.Writes, w => Assert.Equal(0, w.Value));
    }

    /// <summary>The departure-side clear leaves the arrival's memory alone: deboarding, GPU,
    /// the turnaround and prep-done flags are not touched (an app restart after a new-plan
    /// reset must still see a turnaround and must not re-drive the reposition).</summary>
    [Fact]
    public async Task ClearDepartureSide_LeavesTheArrivalSideAlone()
    {
        var writer = CreateWriter(connected: true);

        await writer.ClearDepartureSideAsync();

        var names = _sim.Writes.Select(w => w.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(Done(GsxServiceIds.Deboarding), names);
        Assert.DoesNotContain(Done(GsxServiceIds.Gpu), names);
        Assert.DoesNotContain(CompanionLvarNames.Turnaround.Name, names);
        Assert.DoesNotContain(CompanionLvarNames.PrepDone.Name, names);
    }

    [Fact]
    public async Task ResetForArrival_WritesTurnaroundOne_AndEverythingElseZero()
    {
        var writer = CreateWriter(connected: true);

        await writer.ResetForArrivalAsync();

        var writes = _sim.Writes.ToDictionary(w => w.Name, w => w.Value, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(1, writes[CompanionLvarNames.Turnaround.Name]);
        Assert.Equal(0, writes[CompanionLvarNames.PrepDone.Name]);
        Assert.Equal(0, writes[CompanionLvarNames.LoadsheetPrelimEdition.Name]);
        Assert.Equal(0, writes[CompanionLvarNames.LoadsheetFinalSent.Name]);
        foreach (var serviceId in CompanionTrackingLvars.TrackedServices)
        {
            Assert.Equal(0, writes[Done(serviceId)]);
        }
        Assert.Equal(CompanionTrackingLvars.TrackedServices.Count + 4, writes.Count);
    }

    [Fact]
    public async Task ClearAll_ZeroesTheTurnaroundFlagToo()
    {
        var writer = CreateWriter(connected: true);

        await writer.ClearAllAsync();

        var writes = _sim.Writes.ToDictionary(w => w.Name, w => w.Value, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(0, writes[CompanionLvarNames.Turnaround.Name]);
        Assert.Equal(CompanionTrackingLvars.TrackedServices.Count + 4, writes.Count);
        Assert.All(writes.Values, value => Assert.Equal(0, value));
    }

    /// <summary>Latest value per LVAR wins in the queue: a service-done latch followed by the
    /// new-leg clear while MSFS is down flushes as ONE write of 0 — never the stale 1.</summary>
    [Fact]
    public async Task QueuedWrites_KeepTheLatestValuePerLvar()
    {
        var writer = CreateWriter(connected: false);

        await writer.MarkServiceDoneAsync(GsxServiceIds.Refueling);
        await writer.ClearDepartureSideAsync();
        Assert.Equal(DepartureSideNames.Length, writer.PendingCount);

        Reconnect();
        await WaitUntilAsync(() => writer.PendingCount == 0 && _sim.Writes.Length >= DepartureSideNames.Length, "the queue flushes on the connected edge");

        var refuel = _sim.Writes.Where(w => w.Name.Equals(Done(GsxServiceIds.Refueling), StringComparison.OrdinalIgnoreCase)).ToArray();
        var single = Assert.Single(refuel);
        Assert.Equal(0, single.Value);
    }

    [Fact]
    public async Task MarkServiceDone_WhileConnected_WritesOne()
    {
        var writer = CreateWriter(connected: true);

        await writer.MarkServiceDoneAsync(GsxServiceIds.Boarding);
        await writer.MarkPrepDoneAsync();

        Assert.Equal(
            [(Done(GsxServiceIds.Boarding), 1.0), (CompanionLvarNames.PrepDone.Name, 1.0)],
            _sim.Writes);
    }

    /// <summary>MSFS as the writer sees it: a write throws InvalidOperationException while
    /// disconnected (what ProsimCompanion.Sim does) and is recorded otherwise.</summary>
    private sealed class RecordingSimVars : ISimVars
    {
        private readonly object _gate = new();
        private readonly List<(string Name, double Value)> _writes = [];

        public volatile bool Connected;

        public (string Name, double Value)[] Writes
        {
            get
            {
                lock (_gate)
                {
                    return [.. _writes];
                }
            }
        }

        public IDataRefSubscription SubscribeDynamic(string simVarName, string unit, DataRefTier tier)
            => throw new NotSupportedException("the writer never subscribes");

        public Task WriteAsync(string simVarName, double value, CancellationToken cancellationToken = default)
        {
            if (!Connected)
            {
                throw new InvalidOperationException("MSFS is not connected");
            }

            lock (_gate)
            {
                _writes.Add((simVarName, value));
            }
            return Task.CompletedTask;
        }
    }
}
