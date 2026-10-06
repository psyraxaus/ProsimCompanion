using ProsimCompanion.Core.Aircraft.Setup;
using ProsimCompanion.Core.Configuration;
using Xunit;

namespace ProsimCompanion.Core.Tests.Prosim;

/// <summary>The ProSim setup check's catalogue (2026-10-06): which IOS options are checked
/// under which feature switches, and how a gateway reading becomes a verdict.</summary>
public sealed class ProsimSetupRecommendationsTests
{
    [Fact]
    public void For_GsxOn_ListsAllFiveRows_WithTheOwnersValues()
    {
        var rows = ProsimSetupRecommendations.For(new GsxOptions());

        Assert.Equal(
            [
                ProsimSetupRecommendations.DoorLogic,
                ProsimSetupRecommendations.AutomaticGroundPower,
                ProsimSetupRecommendations.DatalinkLoadCargo,
                ProsimSetupRecommendations.DatalinkLoadFuel,
                ProsimSetupRecommendations.RefuelRate,
            ],
            rows.Select(row => row.DataRef));
        Assert.Equal(["false", "false", "false", "false", "Realistic"], rows.Select(row => row.Recommended));
        Assert.All(rows, row => Assert.Equal(ProsimSetupStatus.Unknown, row.Status));
        Assert.All(rows, row => Assert.False(string.IsNullOrWhiteSpace(row.IosPath)));
    }

    [Fact]
    public void For_GsxPillarOff_KeepsOnlyTheLoadsheetRows()
    {
        // A voice-FO-only install must not be told to turn off ProSim's door logic or GPU.
        var rows = ProsimSetupRecommendations.For(new GsxOptions { Enabled = false });

        Assert.Equal(
            [ProsimSetupRecommendations.DatalinkLoadCargo, ProsimSetupRecommendations.DatalinkLoadFuel],
            rows.Select(row => row.DataRef));
    }

    [Fact]
    public void For_DoorAutomationOff_DropsTheDoorLogicRow_KeepsTheRest()
    {
        var rows = ProsimSetupRecommendations.For(new GsxOptions { DoorAutomationEnabled = false });

        Assert.DoesNotContain(rows, row => row.DataRef == ProsimSetupRecommendations.DoorLogic);
        Assert.Contains(rows, row => row.DataRef == ProsimSetupRecommendations.AutomaticGroundPower);
        Assert.Contains(rows, row => row.DataRef == ProsimSetupRecommendations.RefuelRate);
    }

    [Fact]
    public void WritableDataRefs_AreExactlyTheRowsForAFullInstall()
    {
        var rows = ProsimSetupRecommendations.For(new GsxOptions()).Select(row => row.DataRef);
        Assert.Equal(rows.OrderBy(name => name), ProsimSetupRecommendations.WritableDataRefs.OrderBy(name => name));
    }

    private static ProsimSetupItem DoorLogic => ProsimSetupRecommendations.For(new GsxOptions())[0];

    private static ProsimSetupItem RefuelRate => ProsimSetupRecommendations.For(new GsxOptions())[4];

    [Theory]
    [InlineData("false")]
    [InlineData("False")]   // JsonNode.ToString() of a bool prints lowercase; the SDK path prints "False"
    public void Evaluate_BoolMatch_IsOk_CaseInsensitive(string actual)
    {
        var item = ProsimSetupRecommendations.Evaluate(DoorLogic, actual, ignored: false);
        Assert.Equal(ProsimSetupStatus.Ok, item.Status);
        Assert.False(item.NeedsWrite);
    }

    [Fact]
    public void Evaluate_BoolMismatch_IsMismatch_AndNeedsWrite()
    {
        var item = ProsimSetupRecommendations.Evaluate(DoorLogic, "true", ignored: false);
        Assert.Equal(ProsimSetupStatus.Mismatch, item.Status);
        Assert.Equal("true", item.Actual);
        Assert.True(item.NeedsWrite);
    }

    [Fact]
    public void Evaluate_Null_IsNotPresent_NeverAMismatch()
    {
        // The gateway's "no such dataref" answer: value null. A real off reads "false".
        var item = ProsimSetupRecommendations.Evaluate(DoorLogic, null, ignored: false);
        Assert.Equal(ProsimSetupStatus.NotPresent, item.Status);
        Assert.False(item.NeedsWrite);
    }

    [Fact]
    public void Evaluate_Ignored_WinsOverAMismatch_AndKeepsTheReading()
    {
        var item = ProsimSetupRecommendations.Evaluate(DoorLogic, "true", ignored: true);
        Assert.Equal(ProsimSetupStatus.Ignored, item.Status);
        Assert.Equal("true", item.Actual);
        Assert.False(item.NeedsWrite);
    }

    [Theory]
    [InlineData("Realistic", ProsimSetupStatus.Ok)]
    [InlineData("Quick", ProsimSetupStatus.Mismatch)]
    [InlineData("realistic", ProsimSetupStatus.Mismatch)]   // drop-down text is exact — the catalogue spelling is what ProSim accepts
    public void Evaluate_DropDownText_ComparesExactly(string actual, ProsimSetupStatus expected)
        => Assert.Equal(expected, ProsimSetupRecommendations.Evaluate(RefuelRate, actual, ignored: false).Status);

    [Fact]
    public void ApplyResult_Summary_ReadsLikeASentence()
    {
        Assert.Equal("Nothing to change.", new ProsimSetupApplyResult([], []).Summary);
        Assert.Equal("Set 1 option in ProSim.", new ProsimSetupApplyResult(["Door logic"], []).Summary);
        Assert.Equal("Set 2 options in ProSim.", new ProsimSetupApplyResult(["a", "b"], []).Summary);
        Assert.Equal("ProSim did not accept: Door logic.", new ProsimSetupApplyResult([], ["Door logic"]).Summary);
        Assert.Equal("Set 1; ProSim did not accept: Refuelling rate.", new ProsimSetupApplyResult(["a"], ["Refuelling rate"]).Summary);
    }
}
