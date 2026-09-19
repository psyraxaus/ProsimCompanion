using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Gsx.Mirror;
using ProsimCompanion.Gsx.Protocol;
using ProsimCompanion.Gsx.Services;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

public sealed class GsxServiceLifecycleTrackerTests
{
    private readonly GsxServiceLifecycleTracker _tracker = new(NullLogger<GsxServiceLifecycleTracker>.Instance);
    private readonly List<(string Id, GsxServiceLifecycleEvent Event)> _events = [];

    public GsxServiceLifecycleTrackerTests()
        => _tracker.ServiceEvent += (id, lifecycleEvent) => _events.Add((id, lifecycleEvent));

    private static Dictionary<string, GsxServiceInfo> Services(params (string Id, GsxServiceState State)[] entries)
        => entries.ToDictionary(
            entry => entry.Id,
            entry => new GsxServiceInfo(entry.Id, entry.Id, null, entry.State, false, false, null, null),
            StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void NormalFlow_FiresEachEventOnce()
    {
        _tracker.Process(Services(("Refueling", GsxServiceState.Callable)));
        _tracker.Process(Services(("Refueling", GsxServiceState.Requested)));
        _tracker.Process(Services(("Refueling", GsxServiceState.Requested)));   // repeat — no refire
        _tracker.Process(Services(("Refueling", GsxServiceState.Active)));
        _tracker.Process(Services(("Refueling", GsxServiceState.Active)));      // reconcile tick replay
        _tracker.Process(Services(("Refueling", GsxServiceState.Completed)));
        _tracker.Process(Services(("Refueling", GsxServiceState.Completed)));

        Assert.Equal(
            [("Refueling", GsxServiceLifecycleEvent.Requested),
             ("Refueling", GsxServiceLifecycleEvent.Active),
             ("Refueling", GsxServiceLifecycleEvent.Completed)],
            _events);
        Assert.True(_tracker.IsCompleted("refueling"));
    }

    [Fact]
    public void QuickService_ReturnToAvailableAfterActive_CountsAsCompleted()
    {
        _tracker.Process(Services(("Water", GsxServiceState.Active)));
        _tracker.Process(Services(("Water", GsxServiceState.Callable)));

        Assert.Equal(
            [("Water", GsxServiceLifecycleEvent.Active),
             ("Water", GsxServiceLifecycleEvent.Completed)],
            _events);
    }

    [Fact]
    public void CallableWithoutPriorActive_DoesNotComplete()
    {
        _tracker.Process(Services(("Water", GsxServiceState.Callable)));
        _tracker.Process(Services(("Water", GsxServiceState.Callable)));

        Assert.Empty(_events);
        Assert.False(_tracker.IsCompleted("Water"));
    }

    [Fact]
    public void CalledService_ReturnToAvailableAfterRequested_CountsAsCompleted()
    {
        // Water never shows an Active edge in GSX 4: called → requested → back to callable.
        // Round-4 smoke test: without this rule the sequencer re-called Water forever.
        _tracker.MarkCalled("Water");
        _tracker.Process(Services(("Water", GsxServiceState.Requested)));
        _tracker.Process(Services(("Water", GsxServiceState.Callable)));

        Assert.Equal(
            [("Water", GsxServiceLifecycleEvent.Requested),
             ("Water", GsxServiceLifecycleEvent.Completed)],
            _events);
        Assert.True(_tracker.IsCompleted("Water"));
        Assert.False(_tracker.IsPending("Water"));
    }

    [Fact]
    public void CalledService_StillCallableBeforeGsxReacts_IsPendingNotCompleted()
    {
        // Immediately after the trigger GSX has not processed it yet — the service must read
        // as pending (never re-trigger) but NOT completed (its cycle has not even started).
        _tracker.MarkCalled("Water");
        _tracker.Process(Services(("Water", GsxServiceState.Callable)));

        Assert.Empty(_events);
        Assert.False(_tracker.IsCompleted("Water"));
        Assert.True(_tracker.IsPending("Water"));
    }

    [Fact]
    public void SnapshotCycles_ReflectsCalledAndCompletedFlags()
    {
        _tracker.MarkCalled("Water");
        _tracker.Process(Services(("Water", GsxServiceState.Requested)));
        _tracker.Process(Services(("Water", GsxServiceState.Callable)));

        var cycles = _tracker.SnapshotCycles();
        Assert.True(cycles["Water"].Called);
        Assert.True(cycles["Water"].Requested);
        Assert.True(cycles["Water"].Completed);
    }

    [Fact]
    public void CompletedWithMissedActiveEdge_StillCompletes()
    {
        _tracker.Process(Services(("Boarding", GsxServiceState.Completed)));

        Assert.Equal([("Boarding", GsxServiceLifecycleEvent.Completed)], _events);
    }

    [Fact]
    public void ResetCycle_AllowsEventsToFireAgain()
    {
        _tracker.Process(Services(("Refueling", GsxServiceState.Completed)));
        _tracker.ResetCycle();
        _tracker.Process(Services(("Refueling", GsxServiceState.Completed)));

        Assert.Equal(2, _events.Count(e => e.Event == GsxServiceLifecycleEvent.Completed));
    }

    [Fact]
    public void ServiceVanishing_ResetsItsCycle()
    {
        _tracker.Process(Services(("GPU", GsxServiceState.Active)));
        _tracker.Process(Services(("Boarding", GsxServiceState.Callable)));     // GPU gone
        _tracker.Process(Services(("GPU", GsxServiceState.Active)));            // fresh cycle

        Assert.Equal(2, _events.Count(e => e.Id == "GPU" && e.Event == GsxServiceLifecycleEvent.Active));
    }

    [Fact]
    public void SubscriberThrowing_DoesNotBreakOtherNotifications()
    {
        _tracker.ServiceEvent += (_, _) => throw new InvalidOperationException("boom");

        _tracker.Process(Services(
            ("Refueling", GsxServiceState.Active),
            ("Catering", GsxServiceState.Active)));

        Assert.Equal(2, _events.Count);
    }

    [Fact]
    public void RearmCycle_LetsASecondRunFireItsEdgesAgain()
    {
        // Fuel top-up (2026-09-19): a completed Refueling ordered a second time must produce
        // a fresh Active edge (refuel sync latches the new target) and a fresh Completed edge
        // (crew upcall) — the latched flags would otherwise report the second run as history.
        _tracker.Process(Services(("Refueling", GsxServiceState.Active)));
        _tracker.Process(Services(("Refueling", GsxServiceState.Callable))); // return-to-available = completed
        Assert.True(_tracker.IsCompleted("Refueling"));
        _events.Clear();

        _tracker.RearmCycle("Refueling");
        _tracker.Process(Services(("Refueling", GsxServiceState.Callable))); // still idle — nothing fires
        Assert.Empty(_events);
        Assert.False(_tracker.IsCompleted("Refueling"));

        _tracker.Process(Services(("Refueling", GsxServiceState.Requested)));
        _tracker.Process(Services(("Refueling", GsxServiceState.Active)));
        _tracker.Process(Services(("Refueling", GsxServiceState.Callable)));

        Assert.Equal(
            [("Refueling", GsxServiceLifecycleEvent.Requested),
             ("Refueling", GsxServiceLifecycleEvent.Active),
             ("Refueling", GsxServiceLifecycleEvent.Completed)],
            _events);
        Assert.True(_tracker.IsCompleted("Refueling"));
    }
}
