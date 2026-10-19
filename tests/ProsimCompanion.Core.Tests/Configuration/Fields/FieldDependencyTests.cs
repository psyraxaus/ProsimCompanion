using ProsimCompanion.Core.Configuration;
using Xunit;

namespace ProsimCompanion.Core.Tests.Configuration.Fields;

/// <summary>The disabled-when grammar of <see cref="OptionFieldAttribute.DependsOn"/> (C3).</summary>
public sealed class FieldDependencyTests
{
    [Theory]
    [InlineData("Enabled", "Enabled", 0, false, null)]
    [InlineData("!Enabled", "Enabled", 0, true, null)]
    [InlineData(" VadEngine = silero ", "VadEngine", 0, false, "silero")]
    [InlineData("../Enabled", "Enabled", 1, false, null)]
    [InlineData("!../CommsHandover.Enabled", "CommsHandover.Enabled", 1, true, null)]
    [InlineData("!PttSource=none", "PttSource", 0, true, "none")]
    public void Parse_ReadsNegationUpsPathAndValue(string text, string path, int ups, bool negated, string? equalTo)
    {
        var condition = FieldCondition.Parse(text);

        Assert.Equal(path, condition.Path);
        Assert.Equal(ups, condition.Ups);
        Assert.Equal(negated, condition.Negated);
        Assert.Equal(equalTo, condition.EqualTo);
    }

    [Fact]
    public void Parse_RejectsAnEmptyPath()
        => Assert.Throws<FormatException>(() => FieldCondition.Parse("!"));

    [Fact]
    public void ParseAll_SplitsEntriesOnSemicolon_AndAlternativesOnBar()
    {
        var rules = FieldDependency.ParseAll("Enabled; AutoDeIce | Deice.AutoRequest=ask | Deice.AutoRequest=auto");

        Assert.Equal(2, rules.Count);
        Assert.Single(rules[0].Alternatives);
        Assert.Equal(3, rules[1].Alternatives.Count);
        Assert.Empty(FieldDependency.ParseAll(null));
        Assert.Empty(FieldDependency.ParseAll("  "));
    }

    [Theory]
    [InlineData("Enabled", true, "", "none", true)]
    [InlineData("Enabled", false, "", "none", false)]
    [InlineData("!Enabled", false, "", "none", true)]
    [InlineData("AsrBaseUrl", true, "http://x", "none", true)]
    [InlineData("AsrBaseUrl", true, "   ", "none", false)]
    [InlineData("AtcMuteSource=captainSidestick", true, "", "captainSidestick", true)]
    [InlineData("AtcMuteSource=CAPTAINSIDESTICK", true, "", "captainSidestick", true)]
    [InlineData("!AtcMuteSource=none", true, "", "none", false)]
    public void Holds_ReadsBoolsStringsAndEqualities(string text, bool enabled, string url, string source, bool expected)
    {
        var speech = new SpeechOptions { Enabled = enabled, AsrBaseUrl = url, AtcMuteSource = source };

        Assert.Equal(expected, FieldCondition.Parse(text).Holds([speech]));
    }

    [Fact]
    public void Holds_ComparesEnumsByTheirCamelCaseJsonName()
    {
        var briefing = new BriefingOptions { LlmApi = LlmApiKind.Ollama };

        Assert.True(FieldCondition.Parse("LlmApi=ollama").Holds([briefing]));
        Assert.False(FieldCondition.Parse("LlmApi=openAi").Holds([briefing]));
    }

    [Fact]
    public void Holds_StepsUpToTheOwnersOwner()
    {
        var sayIntentions = new SayIntentionsOptions { Enabled = false, CommsHandover = { Enabled = true } };

        // The hand-over block's own switch is on, the section's master switch is off.
        Assert.True(FieldCondition.Parse("Enabled").Holds([sayIntentions, sayIntentions.CommsHandover]));
        Assert.False(FieldCondition.Parse("../Enabled").Holds([sayIntentions, sayIntentions.CommsHandover]));
    }

    [Fact]
    public void Holds_WalksAChildPath()
    {
        var sop = new SopOptions { FlowMonitor = { GearStillDown = { Enabled = false } } };

        Assert.False(FieldCondition.Parse("GearStillDown.Enabled").Holds([sop, sop.FlowMonitor]));
    }

    [Fact]
    public void Holds_ThrowsForAPathAboveTheRoot_AndForAnUnknownProperty()
    {
        var speech = new SpeechOptions();

        Assert.Throws<InvalidOperationException>(() => FieldCondition.Parse("../Enabled").Holds([speech]));
        Assert.Throws<InvalidOperationException>(() => FieldCondition.Parse("NoSuchProperty").Holds([speech]));
    }

    [Fact]
    public void AllHold_NeedsEveryEntry_AndOneAlternativePerEntry()
    {
        var gsx = new GsxOptions { AutoDeIce = false, Deice = { AutoRequest = GsxDeiceOptions.AutoRequestAuto } };
        var rules = FieldDependency.ParseAll("AutoDeIce | Deice.AutoRequest=ask | Deice.AutoRequest=auto");

        Assert.True(FieldDependency.AllHold(rules, [gsx]));

        gsx.Deice.AutoRequest = GsxDeiceOptions.AutoRequestOff;
        Assert.False(FieldDependency.AllHold(rules, [gsx]));

        gsx.AutoDeIce = true;
        Assert.True(FieldDependency.AllHold(rules, [gsx]));
        Assert.False(FieldDependency.AllHold(FieldDependency.ParseAll("AutoDeIce; RefuelSyncEnabled"), [new GsxOptions { AutoDeIce = true, RefuelSyncEnabled = false }]));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData(LlmApiKind.OpenAi, "openAi")]
    [InlineData(3.5, "3.5")]
    [InlineData("silero", "silero")]
    public void ValueText_IsTheSettingsFileSpelling(object? value, string expected)
        => Assert.Equal(expected, FieldCondition.ValueText(value));
}
