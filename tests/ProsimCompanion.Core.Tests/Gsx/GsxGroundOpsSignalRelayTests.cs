using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Mirror;
using ProsimCompanion.Gsx.Protocol;
using ProsimCompanion.Gsx.Services;
using ProsimCompanion.Gsx.Sync;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>Issue #90: the two paths that re-fired a milestone inside one ground-ops cycle —
/// a service vanishing from the mirror (its tracker latches reset) and the substring de-ice
/// match over two spellings — are absorbed by the relay's own once-per-cycle memory, which
/// only the real flight-cycle reset clears.</summary>
public sealed class GsxGroundOpsSignalRelayTests : IDisposable
{
    private readonly GsxServiceLifecycleTracker _tracker = new(NullLogger<GsxServiceLifecycleTracker>.Instance);
    private readonly GroundOpsSignals _signals = new();
    private readonly GsxGroundOpsSignalRelay _relay;
    private readonly List<string> _raised = [];

    public GsxGroundOpsSignalRelayTests()
    {
        _relay = new GsxGroundOpsSignalRelay(_tracker, _signals, new StubSimVars(fluidType: 2),
            NullLogger<GsxGroundOpsSignalRelay>.Instance);
        _signals.RefuelServiceActive += () => _raised.Add("refuel-active");
        _signals.RefuelCompleted += () => _raised.Add("refuel-completed");
        _signals.BoardingStarted += () => _raised.Add("boarding-started");
        _signals.BoardingCompleted += () => _raised.Add("boarding-completed");
        _signals.ArrivalCompleted += () => _raised.Add("deboarding-completed");
        _signals.DeiceCompleted += type => _raised.Add($"deice-completed:{type}");
    }

    public void Dispose() => _relay.Dispose();

    private static Dictionary<string, GsxServiceInfo> Services(params (string Id, GsxServiceState State)[] entries)
        => entries.ToDictionary(
            entry => entry.Id,
            entry => new GsxServiceInfo(entry.Id, entry.Id, null, entry.State, false, false, null, null),
            StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void NormalCycle_RelaysEachMilestoneOnce()
    {
        _tracker.Process(Services(("Refueling", GsxServiceState.Active)));
        _tracker.Process(Services(("Refueling", GsxServiceState.Completed)));
        _tracker.Process(Services(("Refueling", GsxServiceState.Completed), ("Boarding", GsxServiceState.Active)));
        _tracker.Process(Services(("Refueling", GsxServiceState.Completed), ("Boarding", GsxServiceState.Completed)));
        _tracker.Process(Services(("Refueling", GsxServiceState.Completed), ("Boarding", GsxServiceState.Completed), ("DeIce", GsxServiceState.Completed)));

        Assert.Equal(
            ["refuel-active", "refuel-completed", "boarding-started", "boarding-completed", "deice-completed:2"],
            _raised);
    }

    [Fact]
    public void LeakPath1_MirrorVanishAndReturn_DoesNotRefireMilestones()
    {
        _tracker.Process(Services(("Boarding", GsxServiceState.Active)));
        _tracker.Process(Services(("Boarding", GsxServiceState.Completed)));

        // Couatl restart mid-turnaround: the mirror empties, then the services reappear with
        // the same states. The tracker's cycle for Boarding is gone, so it re-fires both edges.
        _tracker.Process(Services(("GPU", GsxServiceState.Callable)));
        _tracker.Process(Services(("Boarding", GsxServiceState.Active)));
        _tracker.Process(Services(("Boarding", GsxServiceState.Completed)));

        Assert.Single(_raised, "boarding-completed");
        // Phase evidence passes through every time — the engine is idempotent and must not be
        // starved after a sim reload that never crossed the arrival reset.
        Assert.Equal(2, _raised.Count(r => r == "boarding-started"));
    }

    [Fact]
    public void LeakPath1_RefuelVanishAndReturn_SingleActiveAndCompleted()
    {
        _tracker.Process(Services(("Refueling", GsxServiceState.Active)));
        _tracker.Process(Services(("Refueling", GsxServiceState.Completed)));
        _tracker.Process(Services(("Catering", GsxServiceState.Callable)));           // Refueling vanished
        _tracker.Process(Services(("Refueling", GsxServiceState.Active)));            // tracker: fresh cycle
        _tracker.Process(Services(("Refueling", GsxServiceState.Completed)));

        Assert.Equal(["refuel-active", "refuel-completed"], _raised);
    }

    [Fact]
    public void LeakPath2_TwoDeiceSpellingsInOneMirror_RelayOnce()
    {
        _tracker.Process(Services(("DeIce", GsxServiceState.Completed), ("De-Ice", GsxServiceState.Completed)));

        Assert.Single(_raised, "deice-completed:2");
    }

    [Fact]
    public void FlightCycleReset_ReArmsEveryMilestone()
    {
        _tracker.Process(Services(("Boarding", GsxServiceState.Completed), ("DeIce", GsxServiceState.Completed)));

        // The GSX arrival reset: tracker cycles cleared, then the hub's reset.
        _tracker.ResetCycle();
        _signals.RaiseFlightCycleReset();
        _tracker.Process(Services(("Boarding", GsxServiceState.Completed), ("DeIce", GsxServiceState.Completed)));

        Assert.Equal(
            ["boarding-completed", "deice-completed:2", "boarding-completed", "deice-completed:2"],
            _raised);
    }

    [Fact]
    public void RefuelTopUp_SecondRunInOneCycle_DoesNotRelayAgain()
    {
        // The fuel top-up path (2026-09-19) re-arms Refueling for a second run so the refuel
        // sync hears its edges directly; the cross-feature milestones stay once per cycle —
        // the prelim loadsheet and the "refuel complete" notification already guard the same way.
        _tracker.Process(Services(("Refueling", GsxServiceState.Active)));
        _tracker.Process(Services(("Refueling", GsxServiceState.Completed)));
        _tracker.RearmCycle("Refueling");
        _tracker.Process(Services(("Refueling", GsxServiceState.Active)));
        _tracker.Process(Services(("Refueling", GsxServiceState.Completed)));

        Assert.Equal(["refuel-active", "refuel-completed"], _raised);
    }

    [Fact]
    public void Core_MapsOnlyTheKnownEdges()
    {
        Assert.Equal(GroundOpsRelaySignal.RefuelServiceActive, GroundOpsSignalRelayCore.Map("refueling", GsxServiceLifecycleEvent.Active));
        Assert.Equal(GroundOpsRelaySignal.DeiceCompleted, GroundOpsSignalRelayCore.Map("Deicing", GsxServiceLifecycleEvent.Completed));
        Assert.Null(GroundOpsSignalRelayCore.Map("Deicing", GsxServiceLifecycleEvent.Active));
        Assert.Null(GroundOpsSignalRelayCore.Map("Refueling", GsxServiceLifecycleEvent.Requested));
        Assert.Null(GroundOpsSignalRelayCore.Map("Catering", GsxServiceLifecycleEvent.Completed));
    }

    [Fact]
    public void Core_SuppressesSecondTakeUntilReset()
    {
        var core = new GroundOpsSignalRelayCore();

        Assert.Equal(GroundOpsRelaySignal.BoardingCompleted, core.TryTake("Boarding", GsxServiceLifecycleEvent.Completed, out var suppressed));
        Assert.False(suppressed);
        Assert.Null(core.TryTake("Boarding", GsxServiceLifecycleEvent.Completed, out suppressed));
        Assert.True(suppressed);
        Assert.True(core.HasRaised(GroundOpsRelaySignal.BoardingCompleted));

        core.Reset();
        Assert.Equal(GroundOpsRelaySignal.BoardingCompleted, core.TryTake("Boarding", GsxServiceLifecycleEvent.Completed, out suppressed));
        Assert.False(suppressed);
    }

    private sealed class StubSimVars(double fluidType) : ISimVars
    {
        public IDataRefSubscription SubscribeDynamic(string simVarName, string unit, DataRefTier tier)
            => new StubSubscription { Name = simVarName, RawValue = fluidType };

        public Task WriteAsync(string simVarName, double value, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubSubscription : IDataRefSubscription
    {
        public required string Name { get; init; }
        public object? RawValue { get; init; }
        public bool IsStale => false;
        public DateTimeOffset? LastUpdatedUtc => null;
        public event EventHandler? ValueChanged { add { } remove { } }
        public T GetValue<T>(T fallback) => RawValue is T typed ? typed : fallback;
        public void Dispose() { }
    }
}
