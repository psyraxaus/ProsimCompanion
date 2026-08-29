using ProsimCompanion.Speech.Crew;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

public sealed class DroppedCallAdvisoryTests
{
    [Fact]
    public void WithoutAMenu_AsksForACheckAndARecall()
    {
        var text = DroppedCallAdvisoryService.ComposeAdvisory("Deboarding", null);

        Assert.Equal("Captain, ground did not pick up the deboarding call. Check the GSX menu and call it again.", text);
    }

    [Fact]
    public void WithAStandingMenu_NamesIt()
    {
        var text = DroppedCallAdvisoryService.ComposeAdvisory("GPU", "Change parking or service");

        Assert.Contains("GPU call", text);
        Assert.Contains("\"Change parking or service\" is still open", text);
    }

    [Theory]
    [InlineData("OperateJetways", "operate jetways")]
    [InlineData("Refueling", "refueling")]
    [InlineData("GPU", "GPU")]
    public void ServiceIds_SpeakAsWords(string id, string spoken)
        => Assert.Equal(spoken, DroppedCallAdvisoryService.SpeakableService(id));
}
