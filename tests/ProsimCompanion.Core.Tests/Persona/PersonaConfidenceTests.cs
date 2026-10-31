using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Persona;
using ProsimCompanion.Core.Tests.Speech;
using ProsimCompanion.Speech.Persona;
using Xunit;

namespace ProsimCompanion.Core.Tests.Persona;

/// <summary>The confidence axis and the roster override of the persona fragment (issue #194).</summary>
public sealed class PersonaConfidenceTests
{
    public static IEnumerable<object[]> Combinations()
    {
        foreach (var experience in FoRosterEntry.Experiences)
        {
            foreach (var formality in FoRosterEntry.Formalities)
            {
                foreach (var confidence in FoRosterEntry.Confidences)
                {
                    yield return [experience, formality, confidence];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void EveryCombination_CarriesTheNeverChangeAFactRule(string experience, string formality, string confidence)
    {
        var fragment = PersonaService.Fragment(new EffectivePersona("Sam", experience, formality, 1, confidence, FromRoster: true), 1);

        Assert.Contains(PersonaService.FactLockSentence.Trim(), fragment, StringComparison.Ordinal);
        Assert.Contains("Your name is Sam.", fragment, StringComparison.Ordinal);
        Assert.EndsWith(PersonaService.FactLockSentence, fragment, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfidenceClause_NervousAsks_AssuredStates_SteadyIsSilent()
    {
        Assert.Contains("quick to ask the captain", PersonaService.ConfidenceClause("nervous"), StringComparison.Ordinal);
        Assert.Contains("you state things, you don't ask", PersonaService.ConfidenceClause("assured"), StringComparison.Ordinal);
        Assert.Equal("", PersonaService.ConfidenceClause("steady"));
        Assert.Equal("", PersonaService.ConfidenceClause(null));
    }

    [Fact]
    public void SteadyPersona_ReadsExactlyAsBeforeTheAxis()
    {
        var persona = SpeechTestSupport.Persona(new PersonaOptions { Enabled = true, Name = "Alex", Experience = "senior", Formality = "casual" });

        var fragment = persona.SystemPromptFragment(PersonaStyleCategory.Briefing);

        Assert.Equal(
            "Your name is Alex. You are a seasoned, senior First Officer; your manner is relaxed and personable. Keep it economical. " + PersonaService.FactLockSentence,
            fragment);
    }

    [Fact]
    public void RosterActive_TheDrawnFoOverridesTheOptions_AndOffFallsBack()
    {
        var options = new PersonaOptions { Enabled = true, Name = "Configured", Experience = "junior", Formality = "formal", Chattiness = 0, Confidence = "nervous", Roster = { Mode = PersonaRosterOptions.ModeDaily } };
        var roster = new Mock<IFoRosterService>();
        roster.SetupGet(r => r.IsActive).Returns(() => options.Roster.IsActive(options.Enabled));
        roster.SetupGet(r => r.Current).Returns(new FoRosterEntry("priya", "Priya", "senior", "casual", 3, "assured", 5600, "EGCC", null));
        var monitor = new Mock<IOptionsMonitor<PersonaOptions>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(() => options);
        var persona = new PersonaService(monitor.Object, SpeechTestSupport.PhraseBank(), new QuietState(), roster.Object);

        var effective = persona.Effective;
        Assert.True(effective.FromRoster);
        Assert.Equal(("Priya", "senior", "casual", 3, "assured"), (effective.Name, effective.Experience, effective.Formality, effective.Chattiness, effective.Confidence));
        Assert.Equal(3, persona.Chattiness);
        Assert.Contains("Your name is Priya.", persona.SystemPromptFragment(PersonaStyleCategory.Briefing), StringComparison.Ordinal);
        Assert.Contains("you state things", persona.SystemPromptFragment(PersonaStyleCategory.Briefing), StringComparison.Ordinal);

        options.Roster.Mode = PersonaRosterOptions.ModeOff;
        Assert.False(persona.Effective.FromRoster);
        Assert.Equal("Configured", persona.Effective.Name);
        Assert.Contains("quick to ask the captain", persona.SystemPromptFragment(PersonaStyleCategory.Briefing), StringComparison.Ordinal);
    }

    [Fact]
    public void Options_RosterBlock_DefaultsOff_AndIsActiveNeedsThePersona()
    {
        var options = new PersonaOptions();
        Assert.Equal("off", options.Roster.Mode);
        Assert.True(options.Roster.GateIntro);
        Assert.Equal("steady", options.Confidence);
        Assert.False(options.Roster.IsActive(personaEnabled: true));

        options.Roster.Mode = "daily";
        Assert.False(options.Roster.IsActive(personaEnabled: false));
        Assert.True(options.Roster.IsActive(personaEnabled: true));
        Assert.False(options.Roster.IsPinned);
        options.Roster.Mode = "Pinned";
        Assert.True(options.Roster.IsPinned);
    }

    [Fact]
    public void StateFile_RoundTrips_AndAnEmptyOrCorruptFileIsNoDraw()
    {
        var dir = Directory.CreateTempSubdirectory("pc-roster-state-").FullName;
        try
        {
            var path = Path.Combine(dir, "state.json");
            var file = new FoRosterStateFile(NullLogger<FoRosterStateFile>.Instance, path);
            file.Save(new FoRosterState { CurrentId = "a", PreviousId = "b", DrawnFor = "day:x", IntroDone = true, PinnedId = null });

            var loaded = file.Load();
            Assert.Equal(("a", "b", "day:x", true), (loaded.CurrentId, loaded.PreviousId, loaded.DrawnFor, loaded.IntroDone));

            File.WriteAllText(path, "");
            Assert.Null(file.Load().CurrentId);
            File.WriteAllText(path, "{ not json");
            Assert.Null(file.Load().CurrentId);
            Assert.Single(Directory.GetFiles(dir, "state.json.corrupt-*.bak"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void EventLogIsNotNeededForTheFragment_ButTheServiceRecordsEveryDraw()
    {
        var dir = Directory.CreateTempSubdirectory("pc-roster-events-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, FoRosterService.FileName), """{ "officers": [ { "id": "a", "name": "A" }, { "id": "b", "name": "B" } ] }""");
            var options = new PersonaOptions { Enabled = true, Roster = { Mode = "daily" } };
            var monitor = new Mock<IOptionsMonitor<PersonaOptions>>();
            monitor.SetupGet(m => m.CurrentValue).Returns(options);
            var eventLog = new JsonlEventLog(Path.Combine(dir, "sessions"), NullLogger<JsonlEventLog>.Instance);
            var service = new FoRosterService(monitor.Object, new FoRosterStateFile(NullLogger<FoRosterStateFile>.Instance, Path.Combine(dir, "state.json")), eventLog, NullLogger<FoRosterService>.Instance, configDirectory: dir);

            service.Draw("test", DateTimeOffset.UtcNow, "sim:2026-11-01");

            eventLog.DisposeAsync().AsTask().GetAwaiter().GetResult(); // flush the async writer
            var lines = File.ReadAllLines(eventLog.Path);
            Assert.Contains(lines, l => l.Contains("\"persona.rosterDraw\"", StringComparison.Ordinal) && l.Contains("\"reason\":\"test\"", StringComparison.Ordinal));
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
