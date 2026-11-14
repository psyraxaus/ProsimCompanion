using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Tests.TechLog;
using ProsimCompanion.Speech.FoSide;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Issue #221, the restart part: "the automatic cross-check has run for this plan"
/// is a part of the flight-state snapshot, and the leg edge of the restored plan does not
/// clear it (EGLL 2026-11-14 04:32:48Z: the second repeat).</summary>
public sealed class FmgcCrossCheckSnapshotPartTests : IAsyncDisposable
{
    private const string Plan = "req:193487122";

    private readonly JsonlEventLog _eventLog = SpeechTestSupport.TempEventLog();
    private readonly Mock<ILegBoundaryEvents> _legBoundary = new();
    private readonly GroundOpsSignals _signals = new();
    private readonly FmgcCrossCheckService _service;
    private int _changed;

    public FmgcCrossCheckSnapshotPartTests()
    {
        _service = new FmgcCrossCheckService(
            OptionsSupport.Monitor(new SpeechOptions()),
            new FakeDataRefs(),
            new FakePhaseSource(),
            new OfpStore(),
            new TakeoffPerfStore(),
            new FakeArbiter(),
            _eventLog,
            NullLogger<FmgcCrossCheckService>.Instance,
            _signals,
            _legBoundary.Object,
            new LegBoundaryStore());
        _service.Changed += () => _changed++;
        _service.Start();
    }

    public async ValueTask DisposeAsync()
    {
        _service.Dispose();
        await _eventLog.DisposeAsync();
    }

    private static System.Text.Json.Nodes.JsonNode Saved(bool done, string? planId)
        => JsonSerializer.SerializeToNode(new CrossCheckAutoSnapshot(done, planId), FlightStateSnapshotStore.JsonOptions)!;

    private void RaiseLegBegan(string planId)
        => _legBoundary.Raise(l => l.LegBegan += null, _legBoundary.Object, new LegBeganEventArgs(planId, null, LegBoundaryState.None));

    [Fact]
    public void ThePart_IsNamed_AndRestoresAfterTheOfpAndTheDepartureCycle()
    {
        IFlightStatePart part = _service;

        Assert.Equal("fmgcCrossCheck", part.Key);
        Assert.True(part.RestoreOrder > 50);
        Assert.False(part.PersistsAcrossLegs);       // a new leg checks again
    }

    [Fact]
    public void NothingDone_NothingToKeep()
        => Assert.Null(_service.Capture());

    [Fact]
    public void Restored_ThenTheSamePlanBeginsTheLeg_StaysDone()
    {
        _service.Restore(Saved(done: true, Plan));

        RaiseLegBegan(Plan);

        var kept = _service.Capture().Deserialize<CrossCheckAutoSnapshot>(FlightStateSnapshotStore.JsonOptions);
        Assert.Equal(new CrossCheckAutoSnapshot(true, Plan), kept);
        Assert.Equal(0, _changed);
    }

    [Fact]
    public void Restored_ThenADifferentPlanBeginsTheLeg_ClearsIt_AndTellsTheSnapshot()
    {
        _service.Restore(Saved(done: true, Plan));

        RaiseLegBegan("req:193500001");

        Assert.Null(_service.Capture());
        Assert.Equal(1, _changed);
    }

    [Fact]
    public void Restored_ThenTheArrivalCompletes_ClearsIt()
    {
        _service.Restore(Saved(done: true, Plan));

        _signals.RaiseArrivalCompleted();

        Assert.Null(_service.Capture());
        Assert.False(_service.Armed);
        Assert.Equal(1, _changed);
    }

    [Fact]
    public void ASnapshotThatSaysNotDone_RestoresNothing()
    {
        _service.Restore(Saved(done: false, null));

        Assert.Null(_service.Capture());
    }
}
