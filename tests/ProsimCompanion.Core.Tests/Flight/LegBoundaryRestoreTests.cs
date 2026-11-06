using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Diagnostics.Ecam;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Tests.Speech;
using Xunit;

namespace ProsimCompanion.Core.Tests.Flight;

/// <summary>ADR-0019 §9 (issue #220): the leg-boundary state is the <c>turnaround</c> part of
/// the flight-state snapshot — a restart in a turnaround lands in the same state, the flown
/// plan's id comes back with it (no false "first plan" edge when the OFP follows), and the GSX
/// resync's tracking LVARs are the fallback only while the machine knows nothing.</summary>
public sealed class LegBoundaryRestoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 1, 50, 0, TimeSpan.Zero);

    private static OfpData Plan(string requestId, string origin, string destination) => new()
    {
        RequestId = requestId,
        OriginIcao = origin,
        DestinationIcao = destination,
        FlightNumber = "BAW432",
    };

    private sealed class Harness : IDisposable
    {
        public GroundOpsSignals Signals { get; } = new();
        public FakeDataRefs Refs { get; } = new();
        public OfpStore Ofp { get; } = new();
        public FakePhaseSource Phase { get; } = new();
        public LegBoundaryStore Store { get; } = new();
        public Core.Day.DutyDaySignals DutyDay { get; } = new();
        public LegBoundaryService Service { get; }
        public List<LegBeganEventArgs> LegsBegan { get; } = [];

        public Harness()
        {
            Service = new LegBoundaryService(
                Signals,
                new FmsPlanWatcher(Refs, NullLogger<FmsPlanWatcher>.Instance),
                Ofp,
                Phase,
                Store,
                SpeechTestSupport.TempEventLog(),
                new DiagEventLog(),
                NullLogger<LegBoundaryService>.Instance,
                DutyDay);
            Service.LegBegan += (_, e) => LegsBegan.Add(e);
        }

        public void Dispose() => Service.Dispose();
    }

    [Fact]
    public void Snapshot_RoundTripsThroughJson_AndRestoresIntoTheCore()
    {
        var saved = new LegBoundarySnapshot(LegBoundaryState.AwaitingPlan, "req:191949682", true, true, T0, "FMS plan cleared");
        var node = JsonSerializer.SerializeToNode(saved, FlightStateSnapshotStore.JsonOptions)!;
        Assert.Equal("awaitingPlan", node["state"]!.GetValue<string>());

        var back = node.Deserialize<LegBoundarySnapshot>(FlightStateSnapshotStore.JsonOptions)!;
        Assert.Equal(saved, back);

        var core = new LegBoundaryCore();
        core.Restore(back);
        Assert.Equal(LegBoundaryState.AwaitingPlan, core.Snapshot.State);
        Assert.Equal("req:191949682", core.Snapshot.PlanId);

        // The flown plan coming back from the OFP part is the same leg — nothing happens.
        Assert.Null(core.PlanLoaded("req:191949682", T0.AddMinutes(1)));
        // The new plan begins the leg from the restored state.
        var change = core.PlanLoaded("req:191950000", T0.AddMinutes(2));
        Assert.NotNull(change);
        Assert.True(change.LegBegan);
        Assert.Equal(LegBoundaryState.AwaitingPlan, change.From);
    }

    [Fact]
    public void Part_CapturesNothingOutsideABoundary_AndTheBoundaryWhileInOne()
    {
        using var h = new Harness();
        Assert.Equal(LegBoundaryService.SnapshotPartName, h.Service.Key);
        Assert.True(h.Service.PersistsAcrossLegs);
        Assert.True(h.Service.RestoreOrder < 10, "the boundary restores before the OFP part");
        Assert.Null(h.Service.Capture());

        h.Ofp.Set(Plan("191949682", "EGLL", "EHAM"));
        Assert.Null(h.Service.Capture());            // a planned leg is the OFP part's business

        h.Signals.RaiseArrivalCompleted();
        var node = h.Service.Capture();
        Assert.NotNull(node);
        Assert.Equal("turnaround", node!["state"]!.GetValue<string>());
        Assert.Equal("req:191949682", node["planId"]!.GetValue<string>());
    }

    [Fact]
    public void Restore_LandsInTheSavedState_TheFlownOfpIsNotANewLeg_TheNextOneIs()
    {
        using var h = new Harness();
        var saved = new LegBoundarySnapshot(LegBoundaryState.Turnaround, "req:191949682", true, false, T0, "deboarding complete");
        h.Service.Restore(JsonSerializer.SerializeToNode(saved, FlightStateSnapshotStore.JsonOptions)!);

        Assert.Equal(LegBoundaryState.Turnaround, h.Store.Snapshot().State);
        Assert.Equal(LegBoundaryService.RestoredReason, h.Store.Snapshot().Reason);
        Assert.Empty(h.LegsBegan);

        h.Ofp.Set(Plan("191949682", "EGLL", "EHAM"));  // the OFP part restores the flown plan
        Assert.Empty(h.LegsBegan);
        Assert.Equal(LegBoundaryState.Turnaround, h.Store.Snapshot().State);

        h.Ofp.Set(Plan("191950000", "EHAM", "EGLL"));  // the next leg's plan
        var began = Assert.Single(h.LegsBegan);
        Assert.Equal("req:191950000", began.PlanId);
        Assert.Equal("req:191949682", began.PreviousPlanId);
        Assert.Equal(LegBoundaryState.Turnaround, began.From);
        Assert.Equal(LegBoundaryState.None, h.Store.Snapshot().State);
    }

    [Fact]
    public void ResyncEvidence_IsAdoptedOnlyWhileTheMachineKnowsNothing()
    {
        using var fresh = new Harness();
        Assert.True(fresh.Service.AdoptDeboardedFromEvidence("startup resync: tracking LVARs"));
        Assert.Equal(LegBoundaryState.Turnaround, fresh.Store.Snapshot().State);
        Assert.Contains("startup resync", fresh.Store.Snapshot().Reason, StringComparison.Ordinal);
        Assert.False(fresh.Service.AdoptDeboardedFromEvidence("again"));

        using var restored = new Harness();
        restored.Service.Restore(JsonSerializer.SerializeToNode(
            new LegBoundarySnapshot(LegBoundaryState.AwaitingPlan, "req:1", true, true, T0, "x"), FlightStateSnapshotStore.JsonOptions)!);
        Assert.False(restored.Service.AdoptDeboardedFromEvidence("startup resync: tracking LVARs"));
        Assert.Equal(LegBoundaryState.AwaitingPlan, restored.Store.Snapshot().State);

        using var planned = new Harness();
        planned.Ofp.Set(Plan("191950000", "EHAM", "EGLL"));
        Assert.True(planned.Service.AdoptDeboardedFromEvidence("startup resync: tracking LVARs"));
        Assert.Equal(LegBoundaryState.Turnaround, planned.Store.Snapshot().State);
    }

    // Review 2026-11-05 (ADR-0019 §2 / §8): the end of duty clears the boundary state and the step
    // flags but KEEPS the flown plan id as the baseline — a forced re-import of the flown plan
    // after the end of duty used to read as "first plan of the session" and begin a new leg
    // (a new day on the flown plan, the cross-check re-armed, the purser latch reset).
    [Fact]
    public void EndOfDuty_KeepsTheFlownPlanIdAsTheBaseline()
    {
        using var h = new Harness();
        h.Ofp.Set(Plan("191949682", "EGLL", "EHAM"));
        Assert.Single(h.LegsBegan);
        h.Signals.RaiseArrivalCompleted();
        Assert.Equal(LegBoundaryState.Turnaround, h.Store.Snapshot().State);

        h.DutyDay.RaiseDayEnded(new Core.Day.DutyDayEnded("day-1", "voice", T0));
        var after = h.Store.Snapshot();
        Assert.Equal(LegBoundaryState.None, after.State);
        Assert.False(after.Deboarded);
        Assert.Equal("req:191949682", after.PlanId);

        h.Ofp.Set(Plan("191949682", "EGLL", "EHAM")); // the flown plan re-imported: the same leg
        Assert.Single(h.LegsBegan);

        h.Ofp.Set(Plan("191950000", "EHAM", "EGLL")); // a different plan: the next leg
        Assert.Equal(2, h.LegsBegan.Count);
    }
}
