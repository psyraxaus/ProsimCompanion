using ProsimCompanion.Core.Configuration;
using Xunit;

namespace ProsimCompanion.Core.Tests.Configuration;

/// <summary>#178: the agent list and the legacy single-agent fallback every consumer reads
/// through <see cref="MixerOptions.EffectiveAgents"/>.</summary>
public sealed class MixerOptionsTests
{
    [Fact]
    public void EffectiveAgents_LegacyFields_BecomeOneAgent_NamedMixerPc()
    {
        var options = new MixerOptions { Host = " 10.0.1.40 ", Port = 5090, Token = "tok", PanelStrips = [2, 0], PanelBuses = [1] };

        var agents = options.EffectiveAgents();

        var agent = Assert.Single(agents);
        Assert.Equal(MixerAgentOptions.LegacyName, agent.Name);
        Assert.True(agent.Enabled);
        Assert.Equal(" 10.0.1.40 ", agent.Host); // trimming is the page's job (declared normalisation), not the fallback's
        Assert.Equal(5090, agent.Port);
        Assert.Equal("tok", agent.Token);
        Assert.Equal([2, 0], agent.PanelStrips);
        Assert.Equal([1], agent.PanelBuses);
        Assert.True(options.HasLegacyAgent);
    }

    [Fact]
    public void EffectiveAgents_NoHostAndNoList_IsEmpty()
    {
        var options = new MixerOptions { Token = "tok" };

        Assert.Empty(options.EffectiveAgents());
        Assert.False(options.HasLegacyAgent);
    }

    [Fact]
    public void EffectiveAgents_ListWins_OverLegacyFields()
    {
        var options = new MixerOptions
        {
            Host = "legacy",
            Agents = [new MixerAgentOptions { Name = "PC1", Host = "pc1" }, new MixerAgentOptions { Name = "PC2", Host = "pc2" }],
        };

        var agents = options.EffectiveAgents();

        Assert.Equal(["PC1", "PC2"], agents.Select(a => a.Name));
        Assert.False(options.HasLegacyAgent);
    }

    [Fact]
    public void FindAgent_EmptyName_IsTheFirstAgent_SoPre178RowsKeepWorking()
    {
        var options = new MixerOptions { Agents = [new MixerAgentOptions { Name = "PC1" }, new MixerAgentOptions { Name = "PC2" }] };

        Assert.Same(options.Agents[0], options.FindAgent(""));
        Assert.Same(options.Agents[0], options.FindAgent(null));
        Assert.Same(options.Agents[0], options.FindAgent("  "));
    }

    [Fact]
    public void FindAgent_MatchesTrimmedAndCaseInsensitively_AndReturnsNullForAStranger()
    {
        var options = new MixerOptions { Agents = [new MixerAgentOptions { Name = "PC1" }, new MixerAgentOptions { Name = "PC2" }] };

        Assert.Same(options.Agents[1], options.FindAgent(" pc2 "));
        Assert.Null(options.FindAgent("PC3"));
    }

    [Fact]
    public void FindAgent_LegacyFallback_AnswersToEmptyAndToTheLegacyName()
    {
        var options = new MixerOptions { Host = "legacy" };

        Assert.NotNull(options.FindAgent(""));
        Assert.NotNull(options.FindAgent(MixerAgentOptions.LegacyName));
        Assert.Null(options.FindAgent("PC1"));
    }

    [Fact]
    public void Mapping_DefaultsToTheFirstAgent()
    {
        Assert.Equal("", new MixerMapping().Agent);
    }
}
