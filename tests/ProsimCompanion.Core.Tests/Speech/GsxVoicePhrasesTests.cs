using ProsimCompanion.Core.Commands.Handlers;
using ProsimCompanion.Speech.Crew;
using ProsimCompanion.Speech.Gsx;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>The phrase → command catalog (ADR-0006): every ground-power wording from issue
/// #170 lands on the GPU commands, every bound command really exists, and the hail aliases
/// and the ground grammar stay in step.</summary>
public sealed class GsxVoicePhrasesTests
{
    [Theory]
    [InlineData("connect ground power", "gsx.requestGpu")]
    [InlineData("connect the gpu", "gsx.requestGpu")]
    [InlineData("connect the ground power unit", "gsx.requestGpu")]
    [InlineData("request ground power", "gsx.requestGpu")]
    [InlineData("connect external power", "gsx.requestGpu")]
    [InlineData("disconnect ground power", "gsx.disconnectGpu")]
    [InlineData("remove the gpu", "gsx.disconnectGpu")]
    [InlineData("Connect Ground Power", "gsx.requestGpu")]
    public void GroundPowerPhrases_MapToTheGpuCommands(string phrase, string command)
    {
        Assert.True(GsxVoicePhrases.Bindings.TryGetValue(phrase, out var binding));
        Assert.Equal(command, binding!.Command);
        Assert.StartsWith("Copied", binding.CrewAckPhrase, StringComparison.Ordinal);
    }

    [Fact]
    public void GpuAck_NamesGroundPowerComingUp()
    {
        Assert.Equal("Copied — ground power coming up.", GsxVoicePhrases.Bindings["connect ground power"].CrewAckPhrase);
    }

    [Fact]
    public void EveryBoundCommand_IsARegisteredGsxCommand()
    {
        // gsx.forceNextService is a GsxCommandHandlers command and the two de-icing answers
        // (issue #166) are Core store writes; every other binding is a per-service command —
        // a typo here would answer "not registered" at the mic.
        var known = GsxServiceCommandHandlers.Commands.Keys
            .Append("gsx.forceNextService").Append("gsx.declineDeice").Append("gsx.acceptDeice")
            .ToHashSet(StringComparer.Ordinal);
        var unknown = GsxVoicePhrases.Bindings.Values.Select(b => b.Command).Distinct().Where(c => !known.Contains(c)).ToList();

        Assert.True(unknown.Count == 0, "Bound to no command: " + string.Join(", ", unknown));
    }

    [Fact]
    public void GroundHailGrammar_CarriesEveryBindingAndTheCancelWords()
    {
        foreach (var phrase in GsxVoicePhrases.Bindings.Keys.Concat(GsxVoicePhrases.CancelPhrases))
        {
            Assert.Contains(phrase, GsxVoicePhrases.GroundHailGrammar, StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void UnmappedRequestText_ListsWhatTheHailCanDo()
    {
        foreach (var word in new[] { "boarding", "fuel", "catering", "ground power", "pushback", "de-icing" })
        {
            Assert.Contains(word, GsxVoicePhrases.UnmappedRequestText, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("cockpit to ground")]
    [InlineData("flight deck to ground")]
    [InlineData("ground from cockpit")]
    [InlineData("ground, cockpit")]
    [InlineData("ground cockpit")]
    [InlineData("ground crew")]
    public void GroundHailAliases_AreListed(string phrase)
        => Assert.Contains(phrase, CrewHailService.GroundHailPhrases, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void HailPhrases_NeverCollideWithServicePhrases()
    {
        var hails = CrewHailService.GroundHailPhrases.Concat(CrewHailService.CabinHailPhrases);
        var collisions = hails.Where(h => GsxVoicePhrases.Bindings.ContainsKey(h)
            || GsxVoicePhrases.StartGroundServicesPhrases.Contains(h, StringComparer.OrdinalIgnoreCase)).ToList();

        Assert.Empty(collisions);
    }
}
