using ProsimCompanion.Speech.Callouts;
using ProsimCompanion.Speech.Recognition;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Owner request 2026-10-25: after the threat brief's "icing conditions, engine
/// anti-ice" the pilot says "engine anti-ice on" — the FO acknowledges and checks the switches.</summary>
public sealed class AntiIceCallDeciderTests
{
    [Theory]
    [InlineData("Engine anti-ice on.", AntiIceSystem.Engine, true)]
    [InlineData("engine anti ice off", AntiIceSystem.Engine, false)]
    [InlineData("Engine antiice on", AntiIceSystem.Engine, true)]
    [InlineData("engines anti-ice on", AntiIceSystem.Engine, true)]
    [InlineData("anti-ice on", AntiIceSystem.Engine, true)]
    [InlineData("Wing anti-ice on", AntiIceSystem.Wing, true)]
    [InlineData("wing anti ice off", AntiIceSystem.Wing, false)]
    public void Classify_ReadsSystemAndState(string heard, AntiIceSystem system, bool on)
    {
        var call = AntiIceCallDecider.Classify(CommandMatcher.Normalize(heard));

        Assert.NotNull(call);
        Assert.Equal(system, call.System);
        Assert.Equal(on, call.On);
    }

    [Theory]
    [InlineData("gear up")]
    [InlineData("anti-ice")]
    [InlineData("engine anti-ice on and off")]
    [InlineData("de-icing is complete")]
    public void Classify_IgnoresOtherWords(string heard)
        => Assert.Null(AntiIceCallDecider.Classify(CommandMatcher.Normalize(heard)));

    private static readonly AntiIceCall EngineOn = new(AntiIceSystem.Engine, true);
    private static readonly AntiIceCall EngineOff = new(AntiIceSystem.Engine, false);
    private static readonly AntiIceCall WingOn = new(AntiIceSystem.Wing, true);

    [Fact]
    public void Respond_BothSwitchesAgree_Checked()
        => Assert.Equal("Engine anti-ice on, checked.", AntiIceCallDecider.Respond(EngineOn, 1, 1, 0));

    [Fact]
    public void Respond_OneSwitchDisagrees_NamesIt()
    {
        Assert.Equal("Engine anti-ice on... I show engine two off.", AntiIceCallDecider.Respond(EngineOn, 1, 0, 0));
        Assert.Equal("Engine anti-ice on... I show engine one off.", AntiIceCallDecider.Respond(EngineOn, 0, 1, 0));
        Assert.Equal("Engine anti-ice on... I show both engines off.", AntiIceCallDecider.Respond(EngineOn, 0, 0, 0));
        Assert.Equal("Engine anti-ice off... I show engine one on.", AntiIceCallDecider.Respond(EngineOff, 1, 0, 0));
    }

    [Fact]
    public void Respond_NoProsim_PlainAcknowledgement()
    {
        Assert.Equal("Engine anti-ice on.", AntiIceCallDecider.Respond(EngineOn, null, null, null));
        Assert.Equal("Wing anti-ice on.", AntiIceCallDecider.Respond(WingOn, 1, 1, null));
    }

    [Fact]
    public void Respond_Wing_CheckedOrCorrected()
    {
        Assert.Equal("Wing anti-ice on, checked.", AntiIceCallDecider.Respond(WingOn, 0, 0, 1));
        Assert.Equal("Wing anti-ice on... I show it off.", AntiIceCallDecider.Respond(WingOn, 1, 1, 0));
    }

    [Fact]
    public void Phrases_AreCataloguePhrasesTheRouterCanMatchExactly()
    {
        foreach (var phrase in AntiIceCallDecider.Phrases)
        {
            Assert.NotNull(AntiIceCallDecider.Classify(CommandMatcher.Normalize(phrase)));
        }
    }
}
