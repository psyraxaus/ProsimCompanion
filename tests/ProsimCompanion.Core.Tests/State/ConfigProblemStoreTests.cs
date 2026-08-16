using ProsimCompanion.Core.State;
using Xunit;

namespace ProsimCompanion.Core.Tests.State;

public sealed class ConfigProblemStoreTests
{
    [Fact]
    public void Report_AddsEntry_AndFiresChanged()
    {
        var store = new ConfigProblemStore();
        var fired = 0;
        store.Changed += (_, _) => fired++;

        store.Report(ConfigAreas.Checklists, @"C:\cfg\cockpit-preparation.json", "bad JSON at line 42");

        var snapshot = store.Snapshot();
        var problem = Assert.Single(snapshot);
        Assert.Equal(ConfigAreas.Checklists, problem.Area);
        Assert.Equal(@"C:\cfg\cockpit-preparation.json", problem.SourceFile);
        Assert.Equal("bad JSON at line 42", problem.Message);
        Assert.Equal(1, fired);
    }

    [Fact]
    public void Report_SameAreaAndFile_Upserts_NotDuplicates()
    {
        var store = new ConfigProblemStore();
        store.Report(ConfigAreas.Commands, @"C:\cfg\commands.json", "first failure");
        store.Report(ConfigAreas.Commands, @"C:\CFG\COMMANDS.JSON", "second failure"); // case-insensitive key

        var problem = Assert.Single(store.Snapshot());
        Assert.Equal("second failure", problem.Message);
    }

    [Fact]
    public void ClearArea_RemovesOnlyThatArea_AndFiresChanged()
    {
        var store = new ConfigProblemStore();
        store.Report(ConfigAreas.Checklists, "a.json", "x");
        store.Report(ConfigAreas.Abnormals, "b.json", "y");
        var fired = 0;
        store.Changed += (_, _) => fired++;

        store.ClearArea(ConfigAreas.Checklists);

        var remaining = Assert.Single(store.Snapshot());
        Assert.Equal(ConfigAreas.Abnormals, remaining.Area);
        Assert.Equal(1, fired);
    }

    [Fact]
    public void ClearArea_WithNothingToRemove_DoesNotFireChanged()
    {
        var store = new ConfigProblemStore();
        var fired = 0;
        store.Changed += (_, _) => fired++;

        store.ClearArea(ConfigAreas.Themes);

        Assert.Equal(0, fired);
        Assert.Empty(store.Snapshot());
    }

    [Fact]
    public void Snapshot_IsACopy_NotALiveView()
    {
        var store = new ConfigProblemStore();
        store.Report(ConfigAreas.Phrases, "phrases.json", "oops");

        var before = store.Snapshot();
        store.ClearArea(ConfigAreas.Phrases);

        Assert.Single(before);
        Assert.Empty(store.Snapshot());
    }
}
