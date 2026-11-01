using ProsimCompanion.Core.Aircraft;
using Xunit;

namespace ProsimCompanion.Core.Tests.Aircraft;

/// <summary>ADR-0019 §2 / #211: both airports value → empty is a clear; a subscription drop
/// (null) or one side alone is not; a clear fires once per armed plan.</summary>
public sealed class FmsClearDetectorTests
{
    [Fact]
    public void PresentThenBothEmpty_IsAClear_Once()
    {
        var d = new FmsClearDetector();

        Assert.False(d.Observe("EGLL", "EHAM"));
        Assert.True(d.Observe("", ""));
        Assert.Equal(("EGLL", "EHAM"), d.LastPresent);
        Assert.False(d.Observe("", ""));
    }

    [Fact]
    public void TheDashAndNullSentinels_ReadAsEmpty()
    {
        var d = new FmsClearDetector();
        Assert.False(d.Observe("EGLL", "EHAM"));
        Assert.True(d.Observe("----", "Null"));
    }

    [Fact]
    public void OneSideAlone_IsNotAClear_UntilTheOtherFollows()
    {
        // ProSim publishes the two refs a push apart; the pilot may re-type one airport.
        var d = new FmsClearDetector();
        Assert.False(d.Observe("EGLL", "EHAM"));
        Assert.False(d.Observe("", "EHAM"));
        Assert.True(d.Observe("", ""));
    }

    [Fact]
    public void OneSideRetyped_ThenAPlanAgain_NeverFires()
    {
        var d = new FmsClearDetector();
        Assert.False(d.Observe("EGLL", "EHAM"));
        Assert.False(d.Observe("", "EHAM"));
        Assert.False(d.Observe("EGKK", "EHAM"));
        Assert.Equal(("EGKK", "EHAM"), d.LastPresent);
    }

    [Fact]
    public void ASubscriptionDrop_IsNotAClear_AndForgetsThePlan()
    {
        // A ProSim restart: values go null, then come back empty before the pilot types.
        var d = new FmsClearDetector();
        Assert.False(d.Observe("EGLL", "EHAM"));
        Assert.False(d.Observe(null, null));
        Assert.Null(d.LastPresent);
        Assert.False(d.Observe("", ""));
    }

    [Fact]
    public void EmptyFromTheStart_NeverFires()
    {
        var d = new FmsClearDetector();
        Assert.False(d.Observe("", ""));
        Assert.False(d.Observe("----", "----"));
    }

    [Fact]
    public void ANewPlanAfterAClear_ArmsAgain()
    {
        var d = new FmsClearDetector();
        Assert.False(d.Observe("EGLL", "EHAM"));
        Assert.True(d.Observe("", ""));
        Assert.False(d.Observe("EHAM", "EGLL"));
        Assert.True(d.Observe("", ""));
        Assert.Equal(("EHAM", "EGLL"), d.LastPresent);
    }
}
