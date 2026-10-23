using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Checklists;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Localization;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.VoiceReference;
using ProsimCompanion.Speech.Abnormals;
using ProsimCompanion.Speech.Checklists;
using ProsimCompanion.Speech.Gsx;
using ProsimCompanion.Speech.Recognition;
using ProsimCompanion.Speech.VoiceReference;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>
/// The voice-feature roster (candidate 5 of the 2026-10 architecture review): every shipped
/// feature declares its own precedence, gate and drawer rows, and these tests are where the
/// whole order is visible. The pinned table below IS the dispatch order the DI file used to
/// spell out as registration order — any change to a declaration's precedence, a new feature
/// without a slot, or a text id the English pack lacks fails here.
/// </summary>
public sealed partial class VoiceFeatureRosterTests
{
    /// <summary>The dispatch order, first to last, with each feature's declared precedence.
    /// Reproduces the pre-roster registration order exactly (SpeechServiceCollectionExtensions
    /// at 3f98344).</summary>
    private static readonly (Type Feature, int Precedence)[] PinnedOrder =
    [
        (typeof(ProsimCompanion.Speech.Monitoring.FuelCheckMonitor), 10),
        // Issue #183: "fuel decision" / "extra fuel …" / "how long can we hold", beside the fuel check.
        (typeof(ProsimCompanion.Speech.Monitoring.FuelDecisionService), 15),
        (typeof(ProsimCompanion.Speech.Monitoring.GrossErrorCheckMonitor), 20),
        (typeof(ProsimCompanion.Speech.Roles.RoleManager), 30),
        (typeof(ProsimCompanion.Speech.Monitoring.StandaloneReadbacks), 40),
        (typeof(ProsimCompanion.Speech.Radios.RadioExecutor), 50),
        (typeof(ProsimCompanion.Speech.Fcu.FcuExecutor), 60),
        (typeof(ProsimCompanion.Speech.Commands.ConfiguredVoiceCommands), 70),
        (typeof(ProsimCompanion.Speech.Callouts.EngineFlapCallFeature), 80),
        (typeof(ProsimCompanion.Speech.Callouts.GearCallFeature), 90),
        (typeof(ProsimCompanion.Speech.Callouts.ChronoCallFeature), 100),
        (typeof(ProsimCompanion.Speech.Callouts.PilotAnnouncementFeature), 110),
        (typeof(ProsimCompanion.Speech.Mcdu.McduReader), 120),
        (typeof(ProsimCompanion.Speech.Mcdu.McduRadNavTuner), 130),
        (typeof(ProsimCompanion.Speech.Mcdu.McduArrivalChanger), 140),
        (typeof(ProsimCompanion.Speech.Briefings.BriefingService), 150),
        (typeof(ProsimCompanion.Speech.Briefings.MissedApproachVoiceFeature), 160),
        (typeof(ProsimCompanion.Speech.Briefings.MinimaQueryVoiceFeature), 170),
        (typeof(ProsimCompanion.Speech.Company.CompanyChannelService), 180),
        (typeof(ProsimCompanion.Speech.Day.CompanyDayService), 190),
        (typeof(ProsimCompanion.Speech.SayIntentions.SayIntentionsService), 200),
        (typeof(GsxVoiceService), 210),
        (typeof(PushbackDirectionVoiceFeature), 220),
        (typeof(ProsimCompanion.Speech.Crew.CrewHailService), 230),
        (typeof(ProsimCompanion.Speech.Crew.AircraftStateVoiceService), 240),
        (typeof(ProsimCompanion.Speech.Crew.DisregardVoiceFeature), 250),
        (typeof(ProsimCompanion.Speech.TechLog.TechLogVoiceService), 260),
        (typeof(ProsimCompanion.Speech.Logbook.LogbookVoiceService), 270),
        (typeof(ProsimCompanion.Speech.TechLog.TechLogDialogueService), 280),
        (typeof(ProsimCompanion.Speech.Debrief.DebriefService), 290),
        (typeof(ProsimCompanion.Speech.Persona.SmallTalkService), 300),
    ];

    private static IReadOnlyList<Type> ShippedFeatureTypes()
        => [.. typeof(IVoiceFeature).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IVoiceFeature).IsAssignableFrom(t))
            .Where(t => !t.IsNested) // test fakes and private helpers are never registered
            .OrderBy(t => t.FullName, StringComparer.Ordinal)];

    /// <summary>The convention the declaration record documents: a public static
    /// <c>Declared</c> on the class, readable without constructing the service.</summary>
    private static VoiceFeatureDeclaration Declared(Type feature)
    {
        var property = feature.GetProperty("Declared", BindingFlags.Public | BindingFlags.Static);
        Assert.True(property is not null, $"{feature.Name} has no public static VoiceFeatureDeclaration Declared");
        var declaration = property!.GetValue(null) as VoiceFeatureDeclaration;
        Assert.True(declaration is not null, $"{feature.Name}.Declared is not a VoiceFeatureDeclaration");
        return declaration!;
    }

    // ---- the pinned order -------------------------------------------------------------------

    [Fact]
    public void ShippedFeatures_DispatchInThePinnedOrder()
    {
        var shipped = ShippedFeatureTypes();
        Assert.NotEmpty(shipped);

        var unlisted = shipped.Except(PinnedOrder.Select(p => p.Feature)).Select(t => t.Name).ToList();
        Assert.True(unlisted.Count == 0, "Voice features without a slot in the pinned order: " + string.Join(", ", unlisted));
        var gone = PinnedOrder.Select(p => p.Feature).Except(shipped).Select(t => t.Name).ToList();
        Assert.True(gone.Count == 0, "Pinned features that no longer exist: " + string.Join(", ", gone));

        var actual = shipped.OrderBy(t => Declared(t).Precedence).Select(t => t.Name).ToList();
        Assert.Equal(PinnedOrder.Select(p => p.Feature.Name).ToList(), actual);
        foreach (var (feature, precedence) in PinnedOrder)
        {
            Assert.True(Declared(feature).Precedence == precedence, $"{feature.Name} declares {Declared(feature).Precedence}, pinned {precedence}");
        }
    }

    [Fact]
    public void ShippedPrecedences_AreUnique_SoRegistrationOrderNeverDecides()
    {
        var duplicates = ShippedFeatureTypes()
            .GroupBy(t => Declared(t).Precedence)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(t => t.Name))}")
            .ToList();

        Assert.True(duplicates.Count == 0, "Shared precedence: " + string.Join("; ", duplicates));
    }

    [Fact]
    public void Roster_SortsByDeclaredPrecedence_AndKeepsRegistrationOrderOnATie()
    {
        var third = new DeclaredFake(FakeVoiceFeatureDeclaration.At(30), "c");
        var firstA = new DeclaredFake(FakeVoiceFeatureDeclaration.At(10), "a1");
        var second = new DeclaredFake(FakeVoiceFeatureDeclaration.At(20), "b");
        var firstB = new DeclaredFake(FakeVoiceFeatureDeclaration.At(10), "a2");

        var roster = new VoiceFeatureRoster([third, firstA, second, firstB]);

        Assert.Equal<IVoiceFeature>([firstA, firstB, second, third], roster.All);
    }

    [Fact]
    public void Roster_GroupIds_AreUniqueAcrossShippedDeclarations()
    {
        var ids = ShippedFeatureTypes()
            .Select(t => Declared(t).Description)
            .OfType<DescribedVoiceReference>()
            .Select(d => d.Id)
            .ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    // ---- text ids against the English pack ---------------------------------------------------

    [Fact]
    public void EveryDeclaration_NamesOnlyTextsTheEnglishPackHas()
    {
        var english = EnglishVoiceReference();
        var problems = new List<string>();
        foreach (var feature in ShippedFeatureTypes())
        {
            var declaration = Declared(feature);
            if (declaration.Description is DescribedVoiceReference rows)
            {
                Check(rows.TitleTextId, feature, 0);
                foreach (var row in rows.Rows)
                {
                    Check(row.WhatTextId, feature, 0);
                }
            }

            if (declaration.Gate.OffTextId is { } off)
            {
                Check(off, feature, declaration.Gate.Option is null ? 0 : 1);
                if (english.TryGetValue(off, out var text) && text.Contains("{0}", StringComparison.Ordinal) && declaration.Gate.Option is null)
                {
                    problems.Add($"{feature.Name}: {off} takes an option path but the gate names none");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join('\n', problems));

        void Check(string textId, Type feature, int arguments)
        {
            if (!english.TryGetValue(textId, out var text))
            {
                problems.Add($"{feature.Name}: {textId} is not in en\\voiceReference.json");
            }
            else if (Placeholders(text) > arguments)
            {
                problems.Add($"{feature.Name}: {textId} has {Placeholders(text)} placeholder(s), {arguments} argument(s) are supplied");
            }
        }
    }

    [Fact]
    public void EveryGsxBindingCommand_HasADrawerText()
    {
        var english = EnglishVoiceReference();
        var missing = GsxVoicePhrases.Bindings.Values
            .Where(b => !b.CabinAck)
            .Select(b => GsxVoiceReference.CommandWhat(b.Command).TextId)
            .Distinct(StringComparer.Ordinal)
            .Where(id => !english.ContainsKey(id))
            .ToList();

        Assert.True(missing.Count == 0, "GSX commands without a drawer text: " + string.Join(", ", missing));
    }

    [Theory]
    [InlineData(true, true, true, true, true)]
    [InlineData(false, false, false, false, false)]
    [InlineData(true, false, true, false, true)]
    public void BuiltSnapshot_NamesOnlyTextsTheEnglishPackHas_WithEveryArgumentSupplied(
        bool featuresOn, bool gating, bool smallTalk, bool wikipedia, bool cabinOn)
    {
        // Every described declaration as a fake carrying its own rows (so each row is live),
        // plus the two composed ones whose composer reads only statics and options.
        var features = new List<IVoiceFeature>();
        foreach (var type in ShippedFeatureTypes())
        {
            var declaration = Declared(type);
            if (declaration.Description is DescribedVoiceReference rows)
            {
                features.Add(new DeclaredFake(declaration, "x", featuresOn, phrases: [.. rows.Rows.SelectMany(r => r.Phrases)]));
            }
        }

        features.Add(new DeclaredFake(GsxVoiceService.Declared, "gsx", featuresOn,
            phrases: [.. GsxVoicePhrases.StartGroundServicesPhrases, .. GsxVoicePhrases.Bindings.Keys]));
        features.Add(new DeclaredFake(ProsimCompanion.Speech.Crew.CrewHailService.Declared, "hail", featuresOn,
            phrases: [.. ProsimCompanion.Speech.Crew.CrewHailService.GroundHailPhrases, .. ProsimCompanion.Speech.Crew.CrewHailService.CabinHailPhrases]));

        var speech = new SpeechOptions { AcpTransmitGating = gating };
        speech.FoQuestions.SmallTalk = smallTalk;
        speech.FoQuestions.WhereAreWe = true;
        speech.FoQuestions.WikipediaFacts = wikipedia;
        speech.FoQuestions.Enabled = featuresOn;
        var snapshot = Builder(features, speech,
            new GroundCrewOptions { Enabled = cabinOn },
            new CabinOptions { Enabled = cabinOn, CruiseQuery = featuresOn },
            questions: featuresOn ? new ScriptedQuestions() : null).Build();

        var english = EnglishVoiceReference();
        var problems = new List<string>();
        foreach (var text in AllTexts(snapshot))
        {
            if (!english.TryGetValue(text.TextId, out var english1))
            {
                problems.Add($"{text.TextId} is not in en\\voiceReference.json");
            }
            else if (Placeholders(english1) > text.Arguments.Count)
            {
                problems.Add($"{text.TextId} has {Placeholders(english1)} placeholder(s), {text.Arguments.Count} supplied");
            }
        }

        Assert.True(problems.Count == 0, string.Join('\n', problems));
        Assert.True(snapshot.Groups.Count > 30, "every declared feature should produce at least one group");
    }

    // ---- issue #137 through the real declarations -------------------------------------------

    [Fact]
    public void Roster_RadioParserOutranksTheMcduTuner_ExceptOnTheTunersOwnPhrase()
    {
        // Registered tuner first to prove the order is the declaration's, not the registration.
        var tuner = new DeclaredFake(ProsimCompanion.Speech.Mcdu.McduRadNavTuner.Declared, "mcdu", phrases: ["tune the ils"]);
        var radio = new DeclaredFake(ProsimCompanion.Speech.Radios.RadioExecutor.Declared, "radio", valueParse: true, phrases: ["set standby"]);
        var roster = new VoiceFeatureRoster([tuner, radio]);

        Assert.Equal<IVoiceFeature>([radio, tuner], roster.All);
        Assert.Equal<IVoiceFeature>([radio], roster.EnabledValueParsers());
        Assert.True(roster.OwnsExactPhrase("Tune the ILS."));
        Assert.False(roster.OwnsExactPhrase("set standby one one eight decimal one"));
        Assert.Equal<IVoiceFeature>([tuner, radio], roster.Candidates("tune the ils"));
        Assert.Equal<IVoiceFeature>([radio, tuner], roster.Candidates("set standby one one eight decimal one"));
        Assert.Equal(VoiceClaim.ExactPhrase, VoiceFeatureRoster.ClaimOf(tuner, "tune the ils"));
        Assert.Equal(VoiceClaim.Declared, VoiceFeatureRoster.ClaimOf(radio, "tune the ils"));
    }

    [Fact]
    public void Routing_TuneTheIls_ReachesTheMcduTuner_NotTheRadioParser()
    {
        // 2026-09-27 (#137): "tune the ils" died as "Say again the frequency" because the
        // radio parser, first by registration, keys on "tune". Now through the real
        // declarations' precedence (radio 50, tuner 130) plus the roster's claim rule.
        var tuner = new DeclaredFake(ProsimCompanion.Speech.Mcdu.McduRadNavTuner.Declared, "mcdu", phrases: ["tune the ils"]);
        var radio = new GreedyFake(ProsimCompanion.Speech.Radios.RadioExecutor.Declared, "tune", phrase: "set standby");
        var window = new FakeRecognitionWindow();
        using var router = Router(window, [radio, tuner]);

        window.Hear("tune the ils");

        Assert.Equal(["tune the ils"], tuner.Handled);
        Assert.Empty(radio.Handled);
    }

    [Fact]
    public void Routing_SayVSpeeds_ReachesCommandsJson_NotTheFcuParser_WhileAnInstructionStillDoes()
    {
        // The second #137 scenario: a commands.json phrase (70) against the FCU parser (60),
        // which keys on "speed". The exact phrase wins; a free-form instruction still goes to
        // the parser first, with its raw digits.
        var commands = new DeclaredFake(ProsimCompanion.Speech.Commands.ConfiguredVoiceCommands.Declared, "cmd", phrases: ["say v speeds"]);
        var fcu = new GreedyFake(ProsimCompanion.Speech.Fcu.FcuExecutor.Declared, "speed", phrase: "reduce speed");
        var window = new FakeRecognitionWindow();
        using var router = Router(window, [commands, fcu]);

        window.Hear("say v speeds");
        Assert.Equal(["say v speeds"], commands.Handled);
        Assert.Empty(fcu.Handled);

        window.Hear("set speed two one zero");
        Assert.Equal(["set speed two one zero"], fcu.Handled);
        Assert.Single(commands.Handled);
    }

    // ---- fixture ---------------------------------------------------------------------------

    private static UtteranceRouter Router(FakeRecognitionWindow window, IEnumerable<IVoiceFeature> features)
    {
        var options = SpeechTestSupport.SpeechMonitor(new SpeechOptions());
        var dataRefs = Mock.Of<IProsimDataRefs>();
        var eventLog = SpeechTestSupport.TempEventLog();
        var arbiter = new FakeArbiter();
        var phases = new FakePhaseSource();
        var mic = new MicOwnership(window, NullLogger<MicOwnership>.Instance);
        var router = new UtteranceRouter(
            new UtteranceInterpreter(options),
            new ChecklistService(dataRefs, SpeechTestSupport.ChecklistMonitor(new ChecklistOptions()), NullLogger<ChecklistService>.Instance),
            new FailureMonitor(arbiter, dataRefs, phases, eventLog, NullLogger<FailureMonitor>.Instance),
            new VoiceFeatureRoster(features),
            window,
            mic,
            arbiter,
            SpeechTestSupport.PhraseBank(),
            SpeechTestSupport.Persona(),
            new LlmHealthStore(),
            NullLogger<UtteranceRouter>.Instance,
            flightPhase: phases);
        router.Attach(new IdleHost());
        router.Start();
        return router;
    }

    private static VoiceReferenceBuilder Builder(
        IEnumerable<IVoiceFeature> features, SpeechOptions speech, GroundCrewOptions groundCrew, CabinOptions cabin, IFreeFormQuestionHandler? questions)
    {
        var checklists = new Mock<IChecklistDefinitionSource>();
        checklists.Setup(c => c.Definitions(ChecklistService.DefaultSetName)).Returns([new ChecklistDefinition { Checklist = "Before Start" }]);
        var drills = new Mock<IDrillSource>();
        drills.SetupGet(d => d.Drills).Returns([("Windshear escape", (IReadOnlyList<string>)["windshear escape"])]);
        return new VoiceReferenceBuilder(
            new VoiceFeatureRoster(features), checklists.Object, drills.Object,
            Options(speech), Options(groundCrew), Options(cabin),
            NullLogger<VoiceReferenceBuilder>.Instance, questions);
    }

    private static IOptionsMonitor<T> Options<T>(T value) where T : class
    {
        var monitor = new Mock<IOptionsMonitor<T>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(value);
        return monitor.Object;
    }

    private static IEnumerable<VoiceReferenceText> AllTexts(VoiceReferenceSnapshot snapshot)
    {
        foreach (var group in snapshot.Groups)
        {
            yield return group.Title;
            if (group.DisabledReason is { } reason)
            {
                yield return reason;
            }

            foreach (var entry in group.Entries)
            {
                if (entry.What is { } what)
                {
                    yield return what;
                }
            }
        }

        foreach (var entry in snapshot.AlwaysAvailable)
        {
            if (entry.What is { } what)
            {
                yield return what;
            }
        }
    }

    private static IReadOnlyDictionary<string, string> EnglishVoiceReference()
        => LanguagePack.Load(Path.Combine(RepoRoot(), "src", "ProsimCompanion.App", "config", "lang", "en", "voiceReference.json"), "en").Strings;

    private static int Placeholders(string text)
        => PlaceholderPattern().Matches(text).Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).DefaultIfEmpty(-1).Max() + 1;

    [GeneratedRegex(@"\{(\d+)\}")]
    private static partial Regex PlaceholderPattern();

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ProsimCompanion.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repo root (ProsimCompanion.slnx) not found above the test assembly.");
    }

    /// <summary>A feature carrying a REAL declaration with scripted phrases: handles its own
    /// phrases exactly, like the described features do.</summary>
    private sealed class DeclaredFake(VoiceFeatureDeclaration declaration, string name, bool enabled = true, bool valueParse = false, params string[] phrases) : IVoiceFeature
    {
        public string Name => name;
        public List<string> Handled { get; } = [];
        public VoiceFeatureDeclaration Declaration => declaration;
        public bool Enabled => enabled;
        public IEnumerable<string> Phrases => phrases;
        public bool ValueParse => valueParse;

        public bool TryHandle(string utterance)
        {
            if (!phrases.Any(p => string.Equals(p, utterance.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            Handled.Add(utterance);
            return true;
        }

        public override string ToString() => name;
    }

    /// <summary>A value parser the way the real ones behave (issue #137): it claims any text
    /// containing its keyword, whether or not it can read a value from it.</summary>
    private sealed class GreedyFake(VoiceFeatureDeclaration declaration, string keyword, string phrase) : IVoiceFeature
    {
        public List<string> Handled { get; } = [];
        public VoiceFeatureDeclaration Declaration => declaration;
        public bool Enabled => true;
        public IEnumerable<string> Phrases => [phrase];
        public bool ValueParse => true;

        public bool TryHandle(string utterance)
        {
            if (!utterance.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            Handled.Add(utterance);
            return true;
        }
    }

    private sealed class ScriptedQuestions : IFreeFormQuestionHandler
    {
        public bool Enabled => true;
        public bool TryAsk(string rawUtterance) => false;
    }

    /// <summary>A fully idle checklist host: no run, no item, nothing pending.</summary>
    private sealed class IdleHost : IChecklistRoutingHost
    {
        public ChecklistItemDefinition? AwaitingItem => null;
        public bool ResponsePending => false;
        public bool IsIdle => true;
        public bool TryCancelMonitorSkip() => false;
        public bool IsAcceptedAnswer(ChecklistItemDefinition item, string text) => false;
        public void Complete(RoutedResponseKind kind, string text) { }
        public void CancelChecklist() { }
        public void RestartActive() { }
        public void StartChecklist(string name) { }
    }
}
