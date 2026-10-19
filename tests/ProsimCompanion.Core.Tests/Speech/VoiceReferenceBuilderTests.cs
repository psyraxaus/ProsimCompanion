using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Checklists;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.VoiceReference;
using ProsimCompanion.Speech.Callouts;
using ProsimCompanion.Speech.Monitoring;
using ProsimCompanion.Speech.Recognition;
using ProsimCompanion.Speech.VoiceReference;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Issue #136: the drawer is built from the LIVE grammar — described rows survive
/// only while the feature contributes their phrases, unknown live phrases still show. Since
/// the roster (C5) the rows are each feature's own declaration, named by pack text ids.</summary>
public sealed class VoiceReferenceBuilderTests
{
    [Fact]
    public void Matches_ExactOrNamePlusValue()
    {
        Assert.True(VoiceReferenceBuilder.Matches("set heading", "set heading"));
        Assert.True(VoiceReferenceBuilder.Matches("set heading", "Set Heading"));
        Assert.True(VoiceReferenceBuilder.Matches("landing stats for", "landing stats for egll"));
        Assert.False(VoiceReferenceBuilder.Matches("set heading", "set headings"));
        Assert.False(VoiceReferenceBuilder.Matches("heading", "set heading"));
    }

    [Theory]
    [InlineData("keyboard", "RightCtrl", null, null, "", "key RightCtrl")]
    [InlineData("joystickButton", "", 1, 4, "Thrustmaster TCA", "Thrustmaster TCA · button 4")]
    [InlineData("joystickButton", "", 1, 4, "", "joystick 1 · button 4")]
    [InlineData("", "", null, null, "", "not bound — continuous listening")]
    public void PttBindingText_ReadsTheBinding(string kind, string key, int? device, int? button, string deviceName, string expected)
    {
        var options = new SpeechOptions
        {
            PttBinding = new PttBindingOptions { Kind = kind, Key = key, JoystickDevice = device, Button = button, JoystickDeviceName = deviceName },
        };

        Assert.Equal(expected, VoiceReferenceBuilder.PttBindingText(options));
    }

    [Fact]
    public void Build_KeepsDescribedRowsOnlyWhileLive_AndShowsUnknownLivePhrases()
    {
        // The real gear declaration describes "gear up" and "gear down"; this feature
        // (hypothetically) lost "gear down" and gained "gear check".
        var gear = new FakeFeature(GearCallFeature.Declared, enabled: true, "gear up", "gear check");

        var snapshot = Builder([gear]).Build();

        var group = Assert.Single(snapshot.Groups, g => g.Id == "gear");
        Assert.Equal("voiceReference.group.gear.title", group.Title.TextId);
        Assert.True(group.Enabled);
        Assert.Equal(2, group.Entries.Count);
        var described = group.Entries[0];
        Assert.Equal(["gear up"], described.Phrases);
        Assert.Equal("voiceReference.group.gear.up", described.What!.TextId);
        Assert.Equal("Gear up.", described.Answer);
        var unknown = group.Entries[1];
        Assert.Equal(["gear check"], unknown.Phrases);
        Assert.Null(unknown.What);
        Assert.DoesNotContain(group.Entries, e => e.Phrases.Contains("gear down"));
    }

    [Fact]
    public void Build_AlwaysAvailable_CarriesTheRouterGlobals()
    {
        var snapshot = Builder([]).Build();

        var phrases = snapshot.AlwaysAvailable.SelectMany(e => e.Phrases).ToList();
        foreach (var command in VoiceCommands.All)
        {
            Assert.Contains(command, phrases);
        }
        Assert.Contains("belay that", phrases);
        Assert.All(snapshot.AlwaysAvailable, e => Assert.StartsWith("voiceReference.always.", e.What!.TextId, StringComparison.Ordinal));
    }

    [Fact]
    public void Build_ChecklistStarts_OneRowPerChecklist_WithoutRequestTwins()
    {
        var snapshot = Builder([]).Build();

        var checklists = Assert.Single(snapshot.Groups, g => g.Id == "checklists");
        Assert.Equal(VoiceReferenceTab.Checklists, checklists.Tab);
        var beforeStart = Assert.Single(checklists.Entries, e => e.Phrases.Contains("before start checklist"));
        Assert.Equal("voiceReference.group.checklists.start", beforeStart.What!.TextId);
        Assert.Equal(["Before Start"], beforeStart.What.Arguments);
        Assert.DoesNotContain(checklists.Entries, e => e.Phrases.Any(p => p.StartsWith("request ", StringComparison.Ordinal)));

        var drills = Assert.Single(snapshot.Groups, g => g.Id == "drills");
        var drill = Assert.Single(drills.Entries);
        Assert.Equal(["windshear escape", "windshear drill"], drill.Phrases);
        Assert.Equal("voiceReference.group.drills.rehearse", drill.What!.TextId);
        Assert.Equal(["Windshear escape"], drill.What.Arguments);
    }

    [Fact]
    public void Build_DisabledFeature_StaysListedDimmed_WithItsGatesReason()
    {
        var alwaysOn = new FakeFeature(FakeVoiceFeatureDeclaration.Plain, enabled: false, "quiet please");
        var gated = new FakeFeature(StandaloneReadbacks.Declared, enabled: false, "altimeter");

        var snapshot = Builder([alwaysOn, gated]).Build();

        // An always-on declaration that is nonetheless off falls back to the drawer's own word.
        var plain = Assert.Single(snapshot.Groups, g => g.Id == "fake");
        Assert.False(plain.Enabled);
        Assert.Equal(VoiceReferenceTextIds.SwitchedOff, plain.DisabledReason!.TextId);
        Assert.Single(plain.Entries);

        // A gated one names its option through the gate's text.
        var readbacks = Assert.Single(snapshot.Groups, g => g.Id == "readbacks");
        Assert.False(readbacks.Enabled);
        Assert.Equal(VoiceReferenceTextIds.OffMonitoring, readbacks.DisabledReason!.TextId);
        Assert.Equal(["sop.monitoring.readbacks.enabled"], readbacks.DisabledReason.Arguments);
    }

    [Fact]
    public void Build_ListsFeaturesInRosterOrder_NotRegistrationOrder()
    {
        var later = new FakeFeature(FakeVoiceFeatureDeclaration.At(20, "later"), enabled: true, "later phrase");
        var earlier = new FakeFeature(FakeVoiceFeatureDeclaration.At(10, "earlier"), enabled: true, "earlier phrase");

        var groups = Builder([later, earlier]).Build().Groups.Where(g => g.Id is "later" or "earlier").Select(g => g.Id).ToList();

        Assert.Equal(["earlier", "later"], groups);
    }

    [Fact]
    public void Declarations_InFlightMonitoring_PointAtTheSettingsCard_AndMarkReadbackValues()
    {
        // Issue #148: the three voice-facing monitors (the weather watch has no phrases).
        var fuel = FuelCheckMonitor.Declared;
        var gross = GrossErrorCheckMonitor.Declared;
        var readbacks = StandaloneReadbacks.Declared;

        foreach (var declaration in new[] { fuel, gross, readbacks })
        {
            Assert.Equal(VoiceReferenceTextIds.OffMonitoring, declaration.Gate.OffTextId);
            Assert.Equal(VoiceReferenceTab.FirstOfficer, Assert.IsType<DescribedVoiceReference>(declaration.Description).Tab);
        }

        Assert.Contains("fuel check", Rows(fuel).SelectMany(r => r.Phrases));
        Assert.Contains("gross error check", Rows(gross).SelectMany(r => r.Phrases));
        // Four read-backs with a figure, plus the figure-less "altimeter standard" (#148, 2026-10-11).
        var readbackRows = Rows(readbacks);
        Assert.Equal(5, readbackRows.Count);
        var withFigure = readbackRows.Where(r => !r.Phrases.Contains("altimeter standard")).ToList();
        Assert.Equal(4, withFigure.Count);
        Assert.All(withFigure, r =>
        {
            Assert.Contains(VoiceBadges.Value, r.Badges!);
            Assert.NotNull(r.ValueHint);
        });
        Assert.Contains(readbackRows, r => r.Phrases.Contains("read back altimeter"));
        // Every described lead-in is one the parser accepts.
        Assert.All(readbackRows.SelectMany(r => r.Phrases), p => Assert.Contains(p, ReadbackCore.LeadIns));
    }

    // ---- fixture ---------------------------------------------------------------------------

    private static IReadOnlyList<VoiceReferenceRow> Rows(VoiceFeatureDeclaration declaration)
        => Assert.IsType<DescribedVoiceReference>(declaration.Description).Rows;

    private static VoiceReferenceBuilder Builder(IEnumerable<IVoiceFeature> features)
    {
        var checklists = new Mock<IChecklistDefinitionSource>();
        checklists.Setup(c => c.Definitions(ChecklistService.DefaultSetName)).Returns(
        [
            new ChecklistDefinition { Checklist = "Before Start" },
            new ChecklistDefinition { Checklist = "Taxi", StartPhrases = ["taxi checklist", "taxi list"] },
        ]);
        var drills = new Mock<ProsimCompanion.Speech.Abnormals.IDrillSource>();
        drills.SetupGet(d => d.Drills).Returns(
        [
            ("Windshear escape", (IReadOnlyList<string>)["windshear escape", "windshear drill"]),
        ]);
        return new VoiceReferenceBuilder(
            new VoiceFeatureRoster(features),
            checklists.Object,
            drills.Object,
            Options(new SpeechOptions()),
            Options(new GroundCrewOptions()),
            Options(new CabinOptions()),
            NullLogger<VoiceReferenceBuilder>.Instance);
    }

    private static IOptionsMonitor<T> Options<T>(T value) where T : class
    {
        var monitor = new Mock<IOptionsMonitor<T>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(value);
        return monitor.Object;
    }

    private sealed class FakeFeature(VoiceFeatureDeclaration declaration, bool enabled, params string[] phrases) : IVoiceFeature
    {
        public VoiceFeatureDeclaration Declaration => declaration;
        public bool Enabled => enabled;
        public IEnumerable<string> Phrases => phrases;
        public bool ValueParse => false;
        public bool TryHandle(string utterance) => false;
    }
}
