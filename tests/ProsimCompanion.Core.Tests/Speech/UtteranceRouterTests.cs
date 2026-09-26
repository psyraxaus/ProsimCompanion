using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Checklists;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Abnormals;
using ProsimCompanion.Speech.Checklists;
using ProsimCompanion.Speech.Recognition;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>
/// The 6-level precedence ladder, table-tested through <see cref="IChecklistRoutingHost"/>
/// alone (campaign #82) — previously reaching this logic required an 18-argument checklist
/// engine.
/// </summary>
public sealed class UtteranceRouterTests
{
    private sealed class FakeHost : IChecklistRoutingHost
    {
        public ChecklistItemDefinition? AwaitingItem { get; set; }
        public bool ResponsePending { get; set; }
        public bool IsIdle { get; set; } = true;
        public bool MonitorSkipActive { get; set; }
        public List<(RoutedResponseKind Kind, string Text)> Completed { get; } = [];
        public List<string> Started { get; } = [];
        public int Cancelled { get; private set; }
        public int Restarted { get; private set; }
        public int MonitorSkipsCancelled { get; private set; }
        public Func<string, bool> Accepts { get; set; } = _ => false;

        public bool TryCancelMonitorSkip()
        {
            if (!MonitorSkipActive)
            {
                return false;
            }

            MonitorSkipsCancelled++;
            return true;
        }

        public bool IsAcceptedAnswer(ChecklistItemDefinition item, string text) => Accepts(text);

        public void Complete(RoutedResponseKind kind, string text) => Completed.Add((kind, text));

        public void CancelChecklist() => Cancelled++;

        public void RestartActive() => Restarted++;

        public void StartChecklist(string name) => Started.Add(name);
    }

    private sealed class ScriptedFeature(string phrase, bool enabled = true, bool valueParse = false) : IVoiceFeature
    {
        public List<string> Handled { get; } = [];
        public bool Enabled { get; } = enabled;
        public IEnumerable<string> Phrases => [phrase];
        public bool ValueParse { get; } = valueParse;

        public bool TryHandle(string utterance)
        {
            if (!string.Equals(utterance.Trim(), Phrases.First(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            Handled.Add(utterance);
            return true;
        }
    }

    /// <summary>A value parser the way the real ones behave (issue #137): it claims any text
    /// containing its keyword, whether or not it can read a value from it.</summary>
    private sealed class GreedyValueFeature(string keyword) : IVoiceFeature
    {
        public List<string> Handled { get; } = [];
        public bool Enabled => true;
        public IEnumerable<string> Phrases => [$"set {keyword}"];
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

    private readonly FakeRecognitionWindow _window = new();
    private readonly MicOwnership _mic;
    private readonly FakeArbiter _arbiter = new();
    private readonly FakeHost _host = new();

    public UtteranceRouterTests()
    {
        _mic = new MicOwnership(_window, NullLogger<MicOwnership>.Instance);
    }

    private UtteranceRouter CreateRouter(params IVoiceFeature[] features)
    {
        var options = SpeechTestSupport.SpeechMonitor(new SpeechOptions());
        var dataRefs = Mock.Of<IProsimDataRefs>();
        var eventLog = SpeechTestSupport.TempEventLog();
        var router = new UtteranceRouter(
            new UtteranceInterpreter(options),
            new ChecklistService(
                dataRefs,
                SpeechTestSupport.ChecklistMonitor(new ChecklistOptions()),
                NullLogger<ChecklistService>.Instance),
            new FailureMonitor(
                _arbiter, dataRefs, new FakePhaseSource(), eventLog, NullLogger<FailureMonitor>.Instance),
            features,
            _window,
            _mic,
            _arbiter,
            SpeechTestSupport.PhraseBank(),
            SpeechTestSupport.Persona(),
            new LlmHealthStore(),
            NullLogger<UtteranceRouter>.Instance);
        router.Attach(_host);
        router.Start();
        return router;
    }

    private static ChecklistItemDefinition Item(params string[] accepted)
        => new()
        {
            Say = "Test item",
            AcceptedPhrases = [.. accepted],
        };

    [Fact]
    public void IdleUtterance_ReachesAnEnabledFeature()
    {
        var feature = new ScriptedFeature("request refueling");
        using var router = CreateRouter(feature);

        _window.Hear("request refueling");

        Assert.Single(feature.Handled);
    }

    [Theory]
    [InlineData("tune the ils", "tune")]           // McduRadNavTuner vs RadioExecutor
    [InlineData("say v speeds", "speed")]          // commands.json vs FcuExecutor
    [InlineData("altitude star", "altitude")]      // PilotAnnouncement vs FcuExecutor
    [InlineData("one hundred knots", "knots")]     // RoleManager PM call vs FcuExecutor
    public void ExactPhrase_OutranksAGreedyValueParser(string phrase, string keyword)
    {
        // Issue #137: the value parsers used to get the raw text first and swallowed these
        // with "Say again — couldn't read the …" before their owner ever saw them.
        var greedy = new GreedyValueFeature(keyword);
        var owner = new ScriptedFeature(phrase);
        using var router = CreateRouter(greedy, owner);

        _window.Hear(phrase);

        Assert.Single(owner.Handled);
        Assert.Empty(greedy.Handled);
    }

    [Fact]
    public void FreeFormInstruction_StillReachesTheValueParserFirst()
    {
        var greedy = new GreedyValueFeature("speed");
        var owner = new ScriptedFeature("say v speeds");
        using var router = CreateRouter(greedy, owner);

        _window.Hear("set speed two one zero");

        Assert.Single(greedy.Handled);
        Assert.Empty(owner.Handled);
    }

    [Fact]
    public void ExactPhrase_OfADisabledOwner_StillGoesToTheValueParser()
    {
        // A switched-off feature owns nothing — its phrase is free-form text again.
        var greedy = new GreedyValueFeature("speed");
        var owner = new ScriptedFeature("say v speeds", enabled: false);
        using var router = CreateRouter(greedy, owner);

        _window.Hear("say v speeds");

        Assert.Single(greedy.Handled);
        Assert.Empty(owner.Handled);
    }

    [Fact]
    public void DisabledFeature_IsNeverOffered_AndItsPhraseIdleMisses()
    {
        var feature = new ScriptedFeature("request refueling", enabled: false);
        using var router = CreateRouter(feature);

        _window.Hear("request refueling");

        Assert.Empty(feature.Handled);
        // The utterance fell through the whole ladder → the did-not-catch flow spoke.
        Assert.NotEmpty(_arbiter.Requests);
    }

    [Fact]
    public void AcceptedAnswer_OutranksEverything_WhileAnItemIsPending()
    {
        var feature = new ScriptedFeature("checked");
        _host.AwaitingItem = Item("checked");
        _host.ResponsePending = true;
        _host.Accepts = text => text.Contains("checked", StringComparison.OrdinalIgnoreCase);
        using var router = CreateRouter(feature);

        _window.Hear("checked");

        Assert.Empty(feature.Handled);
        Assert.Contains(_host.Completed, c => c.Kind == RoutedResponseKind.Phrase);
    }

    [Fact]
    public void Feature_StaysReachable_WhileAnItemIsPending()
    {
        // Reference semantics: a handover or radio call must not become a failed answer.
        var feature = new ScriptedFeature("my aircraft");
        _host.AwaitingItem = Item("checked");
        _host.ResponsePending = true;
        using var router = CreateRouter(feature);

        _window.Hear("my aircraft");

        Assert.Single(feature.Handled);
        Assert.Empty(_host.Completed);
    }

    [Fact]
    public void Skip_GoesToTheMonitor_WhenOneIsActive_ElseToTheRunLoop()
    {
        _host.AwaitingItem = Item("checked");
        _host.ResponsePending = true;
        _host.MonitorSkipActive = true;
        using (CreateRouter())
        {
            _window.Hear("skip");
        }
        Assert.Equal(1, _host.MonitorSkipsCancelled);
        Assert.Empty(_host.Completed);

        _host.MonitorSkipActive = false;
        using (CreateRouter())
        {
            _window.Hear("skip");
        }
        Assert.Contains(_host.Completed, c => c.Kind == RoutedResponseKind.Skip);
    }

    [Fact]
    public void CancelAndRestart_RouteToTheRunLoop()
    {
        _host.AwaitingItem = Item("checked");
        _host.ResponsePending = true;
        using var router = CreateRouter();

        _window.Hear("cancel checklist");
        _window.Hear("restart checklist");

        Assert.Equal(1, _host.Cancelled);
        Assert.Equal(1, _host.Restarted);
    }

    [Fact]
    public void BorrowedMic_SuppressesRouting()
    {
        var feature = new ScriptedFeature("request refueling");
        using var router = CreateRouter(feature);

        using (_mic.Borrow("dialogue"))
        {
            _window.Hear("request refueling");
        }

        Assert.Empty(feature.Handled);
    }
}
