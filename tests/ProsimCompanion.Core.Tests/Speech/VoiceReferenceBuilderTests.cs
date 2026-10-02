using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Checklists;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.VoiceReference;
using ProsimCompanion.Speech.Recognition;
using ProsimCompanion.Speech.VoiceReference;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Issue #136: the drawer is built from the LIVE grammar — described rows survive
/// only while the feature contributes their phrases, unknown live phrases still show.</summary>
public sealed class VoiceReferenceBuilderTests
{
    [Fact]
    public void EveryDescribedFeatureType_ImplementsIVoiceFeature()
    {
        foreach (var type in VoiceReferenceDescribers.ByFeatureType.Keys)
        {
            Assert.True(typeof(IVoiceFeature).IsAssignableFrom(type), $"{type.Name} is described but is not a voice feature");
        }
    }

    [Fact]
    public void EveryRegisteredFeatureType_IsDescribedOrComposedLive()
    {
        // Every IVoiceFeature the speech assembly ships must either have a describer row or be
        // one of the four composed from their own live data — a new feature added without a
        // describer would land on the FO tab undescribed.
        var composedLive = new[]
        {
            typeof(ProsimCompanion.Speech.Gsx.GsxVoiceService),
            typeof(ProsimCompanion.Speech.Crew.CrewHailService),
            typeof(ProsimCompanion.Speech.Commands.ConfiguredVoiceCommands),
            typeof(ProsimCompanion.Speech.SayIntentions.SayIntentionsService),
        };
        var featureTypes = typeof(IVoiceFeature).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IVoiceFeature).IsAssignableFrom(t))
            .Where(t => !t.IsNested) // test fakes and private helpers are never registered
            .ToList();

        Assert.NotEmpty(featureTypes);
        var missing = featureTypes
            .Where(t => !VoiceReferenceDescribers.ByFeatureType.ContainsKey(t) && !composedLive.Contains(t))
            .Select(t => t.Name)
            .ToList();
        Assert.True(missing.Count == 0, "Undescribed voice features: " + string.Join(", ", missing));
    }

    [Fact]
    public void Matches_ExactOrKeyPlusValue()
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
        // A gear feature that (hypothetically) lost "gear down" and gained "gear check".
        var gear = new FakeFeature(typeof(ProsimCompanion.Speech.Callouts.GearCallFeature), enabled: true, "gear up", "gear check");
        var builder = Builder([gear]);

        var snapshot = builder.Build();

        // The fake is not the real type, so it lands as an unknown feature — every phrase listed.
        var group = Assert.Single(snapshot.Groups, g => g.Id == nameof(FakeFeature));
        Assert.Equal(["gear up", "gear check"], group.Entries.SelectMany(e => e.Phrases));
        Assert.True(group.Enabled);
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
    }

    [Fact]
    public void Build_ChecklistStarts_OneRowPerChecklist_WithoutRequestTwins()
    {
        var snapshot = Builder([]).Build();

        var checklists = Assert.Single(snapshot.Groups, g => g.Id == "checklists");
        Assert.Equal(VoiceReferenceTab.Checklists, checklists.Tab);
        Assert.Contains(checklists.Entries, e => e.Phrases.Contains("before start checklist"));
        Assert.DoesNotContain(checklists.Entries, e => e.Phrases.Any(p => p.StartsWith("request ", StringComparison.Ordinal)));

        var drills = Assert.Single(snapshot.Groups, g => g.Id == "drills");
        var drill = Assert.Single(drills.Entries);
        Assert.Equal(["windshear escape", "windshear drill"], drill.Phrases);
        Assert.Contains("Windshear escape", drill.What, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_DisabledFeature_StaysListedDimmed()
    {
        var off = new FakeFeature(typeof(FakeFeature), enabled: false, "quiet please");

        var group = Assert.Single(Builder([off]).Build().Groups, g => g.Id == nameof(FakeFeature));

        Assert.False(group.Enabled);
        Assert.Equal("switched off", group.DisabledReason);
        Assert.Single(group.Entries);
    }

    [Fact]
    public void Describers_InFlightMonitoring_PointAtTheSettingsCard_AndMarkReadbackValues()
    {
        // Issue #148: the three voice-facing monitors (the weather watch has no phrases). The
        // builder keys on the real feature type, so the table itself is checked here.
        var fuel = VoiceReferenceDescribers.ByFeatureType[typeof(ProsimCompanion.Speech.Monitoring.FuelCheckMonitor)];
        var gross = VoiceReferenceDescribers.ByFeatureType[typeof(ProsimCompanion.Speech.Monitoring.GrossErrorCheckMonitor)];
        var readbacks = VoiceReferenceDescribers.ByFeatureType[typeof(ProsimCompanion.Speech.Monitoring.StandaloneReadbacks)];

        foreach (var describer in new[] { fuel, gross, readbacks })
        {
            Assert.Contains("In-flight monitoring", describer.DisabledReason, StringComparison.Ordinal);
            Assert.Equal(VoiceReferenceTab.FirstOfficer, describer.Tab);
        }

        Assert.Contains("fuel check", fuel.Entries.SelectMany(e => e.Phrases));
        Assert.Contains("gross error check", gross.Entries.SelectMany(e => e.Phrases));
        Assert.Equal(4, readbacks.Entries.Length);
        Assert.All(readbacks.Entries, e =>
        {
            Assert.Contains(VoiceReferenceDescribers.Value, e.Badges!);
            Assert.NotNull(e.ValueHint);
        });
        // Every described lead-in is one the parser accepts.
        Assert.All(readbacks.Entries.SelectMany(e => e.Phrases), p => Assert.Contains(p, ProsimCompanion.Speech.Monitoring.ReadbackCore.LeadIns));
    }

    // ---- fixture ---------------------------------------------------------------------------

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
            features,
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

    private sealed class FakeFeature(Type mimics, bool enabled, params string[] phrases) : IVoiceFeature
    {
        public Type Mimics { get; } = mimics;
        public bool Enabled => enabled;
        public IEnumerable<string> Phrases => phrases;
        public bool ValueParse => false;
        public bool TryHandle(string utterance) => false;
    }
}
