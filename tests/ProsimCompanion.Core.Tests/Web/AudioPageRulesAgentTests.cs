using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Web.Settings;
using Xunit;

namespace ProsimCompanion.Core.Tests.Web;

/// <summary>The Audio page's agent rules (#178) and the shared-target check (#179).</summary>
public sealed class AudioPageRulesAgentTests
{
    [Fact]
    public void MigrateLegacyAgent_LiftsHostPortTokenAndPanelLists_IntoAgentZero_AndClearsThem()
    {
        var mixer = new MixerOptions
        {
            Enabled = true,
            Host = " 10.0.1.40 ",
            Port = 5090,
            Token = "tok",
            PanelStrips = [3, 1, 3],
            PanelBuses = [0],
            Mappings = [new MixerMapping { Channel = AudioChannel.Vhf1 }],
        };

        Assert.True(AudioPageRules.MigrateLegacyAgent(mixer));

        var agent = Assert.Single(mixer.Agents);
        Assert.Equal(MixerAgentOptions.LegacyName, agent.Name);
        Assert.Equal("10.0.1.40", agent.Host);
        Assert.Equal(5090, agent.Port);
        Assert.Equal("tok", agent.Token);
        Assert.Equal([1, 3], agent.PanelStrips);
        Assert.Equal([0], agent.PanelBuses);
        Assert.Equal("", mixer.Host);
        Assert.Equal("", mixer.Token);
        Assert.Equal(5088, mixer.Port);
        Assert.Empty(mixer.PanelStrips);
        Assert.Empty(mixer.PanelBuses);
        Assert.True(mixer.Enabled);
        Assert.Equal("", mixer.Mappings[0].Agent); // "the first agent" — nothing to rewrite
        Assert.False(mixer.HasLegacyAgent);
    }

    [Fact]
    public void MigrateLegacyAgent_NothingToLift_IsANoOp()
    {
        var fresh = new MixerOptions();
        var listed = new MixerOptions { Host = "legacy", Agents = [new MixerAgentOptions { Name = "PC1", Host = "pc1" }] };

        Assert.False(AudioPageRules.MigrateLegacyAgent(fresh));
        Assert.False(AudioPageRules.MigrateLegacyAgent(listed));
        Assert.Single(listed.Agents);
        Assert.Equal("legacy", listed.Host); // the list wins; the stale field is harmless and left alone
    }

    [Fact]
    public void TidyAgents_NamesBlanks_SuffixesDuplicates_SortsPanelLists_AndRepointsRows()
    {
        var mixer = new MixerOptions
        {
            Agents =
            [
                new MixerAgentOptions { Name = "  ", PanelStrips = [2, 0, 2] },
                new MixerAgentOptions { Name = " PC1 " },
                new MixerAgentOptions { Name = "pc1", PanelBuses = [1, 0] },
            ],
            Mappings =
            [
                new MixerMapping { Agent = " PC1 " },
                new MixerMapping { Agent = "" },
            ],
        };

        AudioPageRules.TidyAgents(mixer);

        Assert.Equal(["Mixer PC 1", "PC1", "pc1 (2)"], mixer.Agents.Select(a => a.Name));
        Assert.Equal([0, 2], mixer.Agents[0].PanelStrips);
        Assert.Equal([0, 1], mixer.Agents[2].PanelBuses);
        Assert.Equal("PC1", mixer.Mappings[0].Agent);
        Assert.Equal("", mixer.Mappings[1].Agent);
    }

    [Fact]
    public void TidyAgents_KeepsARowWhoseAgentIsGone_NeverRepointsItSilently()
    {
        var mixer = new MixerOptions
        {
            Agents = [new MixerAgentOptions { Name = "PC1" }],
            Mappings = [new MixerMapping { Agent = "PC2" }],
        };

        AudioPageRules.TidyAgents(mixer);

        Assert.Equal("PC2", Assert.Single(mixer.Mappings).Agent);
    }

    [Fact]
    public void BeforeSave_RunsTheAgentTidy()
    {
        var mixer = new MixerOptions { Agents = [new MixerAgentOptions { Name = "" }] };

        AudioPageRules.BeforeSave(new AudioOptions(), mixer);

        Assert.Equal("Mixer PC 1", mixer.Agents[0].Name);
    }

    [Fact]
    public void RenameAgent_MovesNamedRows_LeavesFirstAgentRowsAlone()
    {
        var mixer = new MixerOptions
        {
            Mappings =
            [
                new MixerMapping { Agent = "PC1" },
                new MixerMapping { Agent = "pc1" },
                new MixerMapping { Agent = "" },
                new MixerMapping { Agent = "PC2" },
            ],
        };

        Assert.Equal(2, AudioPageRules.RenameAgent(mixer, "PC1", "Captain PC"));

        Assert.Equal(["Captain PC", "Captain PC", "", "PC2"], mixer.Mappings.Select(m => m.Agent));
        Assert.Equal(0, AudioPageRules.RenameAgent(mixer, "", "anything"));
    }

    [Fact]
    public void SharedTargets_GroupsRowsOnOneStripOrBus_PerAgent()
    {
        var rows = new[]
        {
            new AudioPageRules.TargetRow("PC1", true, 1, "Captain VHF1"),
            new AudioPageRules.TargetRow("PC1", false, 1, "Captain INT"),
            new AudioPageRules.TargetRow(" pc1 ", true, 1, "First Officer VHF1"),
            new AudioPageRules.TargetRow("PC2", true, 1, "First Officer VHF2"),
            new AudioPageRules.TargetRow("", false, 3, "Captain PA"),
            new AudioPageRules.TargetRow("", false, 3, "Observer PA"),
        };

        var shared = AudioPageRules.SharedTargets(rows);

        Assert.Equal(2, shared.Count);
        Assert.Equal("PC1", shared[0].Agent);
        Assert.True(shared[0].IsBus);
        Assert.Equal(1, shared[0].Index);
        Assert.Equal(["Captain VHF1", "First Officer VHF1"], shared[0].Owners);
        Assert.Equal("", shared[1].Agent);
        Assert.Equal(["Captain PA", "Observer PA"], shared[1].Owners);
    }

    [Fact]
    public void SharedTargets_NothingShared_IsEmpty()
    {
        var rows = new[]
        {
            new AudioPageRules.TargetRow("", true, 1, "Captain VHF1"),
            new AudioPageRules.TargetRow("", true, 2, "First Officer VHF1"),
            new AudioPageRules.TargetRow("", false, 1, "Captain INT"),
        };

        Assert.Empty(AudioPageRules.SharedTargets(rows));
    }
}
