using ProsimCompanion.Core.State;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>The one-shot resync verdict learns a turnaround that happens AFTER it ran
/// (issue #168): an app started in flight assesses "no turnaround" at cruise and must still
/// treat the leg after the arrival reset as one.</summary>
public sealed class GsxResyncStateTests
{
    [Fact]
    public void MarkTurnaround_AfterAssessment_SetsTheFlag()
    {
        var state = new GsxResyncState();
        state.MarkAssessed(turnaroundDetected: false, loadsheetPrelimEdition: 0, loadsheetFinalSent: false);
        Assert.False(state.TurnaroundDetected);

        state.MarkTurnaround();

        Assert.True(state.TurnaroundDetected);
        Assert.True(state.IsAssessed);
    }

    [Fact]
    public void MarkTurnaround_BeforeAssessment_SurvivesTheVerdict()
    {
        var state = new GsxResyncState();
        state.MarkTurnaround();

        state.MarkAssessed(turnaroundDetected: false, loadsheetPrelimEdition: 0, loadsheetFinalSent: false);

        Assert.True(state.TurnaroundDetected);
    }

    [Fact]
    public void MarkAssessed_IsOneShot()
    {
        var raised = 0;
        var state = new GsxResyncState();
        state.Assessed += () => raised++;

        state.MarkAssessed(turnaroundDetected: true, loadsheetPrelimEdition: 2, loadsheetFinalSent: true);
        state.MarkAssessed(turnaroundDetected: false, loadsheetPrelimEdition: 0, loadsheetFinalSent: false);

        Assert.Equal(1, raised);
        Assert.True(state.TurnaroundDetected);
        Assert.Equal(2, state.LoadsheetPrelimEdition);
    }
}
