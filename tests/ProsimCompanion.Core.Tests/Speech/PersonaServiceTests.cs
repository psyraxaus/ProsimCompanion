using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Speech.Persona;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

public sealed class PersonaServiceTests
{
    [Fact]
    public void PersonaOff_FragmentIsEmpty_AndAcknowledgeReturnsFallback()
    {
        var persona = SpeechTestSupport.Persona(new PersonaOptions { Enabled = false });

        Assert.Equal("", persona.SystemPromptFragment(PersonaStyleCategory.Briefing));
        Assert.Equal("fallback", persona.Acknowledge(AckKind.AreYouSure, "fallback"));
    }

    [Fact]
    public void PersonaOn_FragmentCarriesNameExperienceAndFactLock()
    {
        var persona = SpeechTestSupport.Persona(new PersonaOptions
        {
            Enabled = true,
            Name = "Alex",
            Experience = "senior",
            Formality = "casual",
        });

        var fragment = persona.SystemPromptFragment(PersonaStyleCategory.Briefing);
        Assert.Contains("Your name is Alex.", fragment);
        Assert.Contains("seasoned, senior First Officer", fragment);
        Assert.Contains("relaxed and personable", fragment);
        Assert.Contains("NEVER add, remove, or change", fragment);
    }

    [Fact]
    public void CategoryToggleOff_SuppressesThatFragmentOnly()
    {
        var persona = SpeechTestSupport.Persona(new PersonaOptions
        {
            Enabled = true,
            StyleBriefings = false,
        });

        Assert.Equal("", persona.SystemPromptFragment(PersonaStyleCategory.Briefing));
        Assert.NotEqual("", persona.SystemPromptFragment(PersonaStyleCategory.Debrief));
    }

    [Fact]
    public void PersonaOn_AcknowledgeDrawsFromTheSharedPools()
    {
        var persona = SpeechTestSupport.Persona(new PersonaOptions { Enabled = true });

        // Default pools (no phrases.json in the temp dir) — the pick must come from them,
        // never be the round-robin fallback marker.
        for (var i = 0; i < 20; i++)
        {
            var ack = persona.Acknowledge(AckKind.DidNotCatch, "marker-not-in-pool");
            Assert.NotEqual("marker-not-in-pool", ack);
            Assert.Contains(ack, new[] { "Say again?", "Didn't catch that.", "Repeat please." });
        }
    }

    [Fact]
    public void QuietPlease_ZeroesEffectiveChattiness()
    {
        var quiet = new QuietState();
        var phraseBank = SpeechTestSupport.PhraseBank();
        var monitor = new Moq.Mock<Microsoft.Extensions.Options.IOptionsMonitor<PersonaOptions>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(new PersonaOptions { Enabled = true, Chattiness = 3 });
        var persona = new PersonaService(monitor.Object, phraseBank, quiet);

        Assert.Equal(3, persona.Chattiness);
        quiet.Engage();
        Assert.Equal(0, persona.Chattiness);
    }
}
