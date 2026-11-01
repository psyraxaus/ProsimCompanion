using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Automation;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>Restart after boarding (EHAM 2026-11-01): a cycle whose every configured service
/// is seeded complete is a finished cycle, not an unstarted one.</summary>
public sealed class ResyncCycleRestoreTests
{
    private static DepartureServiceStep Step(string id) => new(id, GsxServiceActivation.AfterCalled);

    [Fact]
    public void EveryConfiguredServiceSeededComplete_IsAFinishedCycle()
    {
        var steps = new[] { Step(GsxServiceIds.Refueling), Step(GsxServiceIds.Catering), Step(GsxServiceIds.Boarding) };
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { GsxServiceIds.Refueling, GsxServiceIds.Catering, GsxServiceIds.Boarding, GsxServiceIds.Deboarding };

        Assert.True(ResyncCycleRestore.AllConfiguredCompleted(steps, done.Contains));
    }

    [Fact]
    public void OneOpenService_LeavesTheCycleToTheSequencer()
    {
        // The 14:34Z restart at the stand: refuel seeded, catering and boarding still to run.
        var steps = new[] { Step(GsxServiceIds.Refueling), Step(GsxServiceIds.Catering), Step(GsxServiceIds.Boarding) };
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { GsxServiceIds.Refueling };

        Assert.False(ResyncCycleRestore.AllConfiguredCompleted(steps, done.Contains));
    }

    [Fact]
    public void NoConfiguredSteps_IsNeverFinished()
    {
        Assert.False(ResyncCycleRestore.AllConfiguredCompleted(Array.Empty<DepartureServiceStep>(), _ => true));
    }
}
