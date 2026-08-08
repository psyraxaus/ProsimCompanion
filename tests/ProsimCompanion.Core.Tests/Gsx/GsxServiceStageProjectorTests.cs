using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Mirror;
using ProsimCompanion.Gsx.Protocol;
using ProsimCompanion.Gsx.Services;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>
/// The latched stage projection behind every status surface (issue #29): the lifecycle latch
/// must win over GSX's raw state for one-shot services — quick services flip back to
/// "available" after finishing — but must never apply to the positional jetway/stairs toggles.
/// </summary>
public sealed class GsxServiceStageProjectorTests
{
    private static GsxServiceInfo Service(string id, GsxServiceState state)
        => new(id, id, null, state, CanTrigger: true, Waiting: false, null, null);

    private static GsxServiceLifecycleTracker.ServiceCycleSnapshot Cycle(
        bool called = false, bool requested = false, bool active = false, bool completed = false)
        => new(called, requested, active, completed);

    [Fact]
    public void CompletedLatch_WinsOverReturnToAvailable()
        // The reported regression: catering/fueling finished, GSX reads "available" again.
        => Assert.Equal(
            GsxServiceStage.Completed,
            GsxServiceStageProjector.Project(
                Service(GsxServiceIds.Catering, GsxServiceState.Callable),
                Cycle(active: true, completed: true)));

    [Fact]
    public void Toggles_NeverLatch_SoRetractionShowsRetracted()
        // A docked-then-retracted jetway reads Callable again — the latch must not keep it
        // "connected".
        => Assert.Equal(
            GsxServiceStage.Waiting,
            GsxServiceStageProjector.Project(
                Service(GsxServiceIds.OperateJetways, GsxServiceState.Callable),
                Cycle(called: true, active: true, completed: true)));

    [Fact]
    public void Toggles_StillReportPositionalStates()
        => Assert.Equal(
            GsxServiceStage.Completed,
            GsxServiceStageProjector.Project(
                Service(GsxServiceIds.OperateStairs, GsxServiceState.Completed),
                Cycle()));

    [Theory]
    [InlineData(GsxServiceState.Active, GsxServiceStage.Active)]
    [InlineData(GsxServiceState.Requested, GsxServiceStage.Requested)]
    [InlineData(GsxServiceState.Completed, GsxServiceStage.Completed)]
    [InlineData(GsxServiceState.NotAvailable, GsxServiceStage.Skipped)]
    [InlineData(GsxServiceState.Bypassed, GsxServiceStage.Skipped)]
    [InlineData(GsxServiceState.Callable, GsxServiceStage.Waiting)]
    [InlineData(GsxServiceState.Unknown, GsxServiceStage.Waiting)]
    public void FreshCycle_MapsRawStates(GsxServiceState state, GsxServiceStage expected)
        => Assert.Equal(
            expected,
            GsxServiceStageProjector.Project(Service(GsxServiceIds.Refueling, state), Cycle()));

    [Fact]
    public void CalledButNotYetAcknowledged_ShowsCalled()
        => Assert.Equal(
            GsxServiceStage.Called,
            GsxServiceStageProjector.Project(
                Service(GsxServiceIds.Water, GsxServiceState.Callable),
                Cycle(called: true)));
}
