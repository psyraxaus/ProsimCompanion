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

/// <summary>Scriptable recognition window standing in for RecognitionController.</summary>
internal sealed class FakeRecognitionWindow : IRecognitionWindow
{
    public event EventHandler<RecognizedEventArgs>? Accepted;

    public event EventHandler<RecognizedEventArgs>? Rejected;

    public bool WindowOpen { get; private set; }

    public IReadOnlyList<string> CurrentGrammar { get; private set; } = [];

    /// <summary>Every grammar passed to OpenListeningWindow, in order.</summary>
    public List<IReadOnlyList<string>> Opened { get; } = [];

    public int CloseCount { get; private set; }

    public void OpenListeningWindow(IReadOnlyList<string> grammar)
    {
        CurrentGrammar = grammar;
        WindowOpen = true;
        Opened.Add(grammar);
    }

    public void CloseListeningWindow()
    {
        WindowOpen = false;
        CloseCount++;
    }

    public void Hear(string text)
        => Accepted?.Invoke(this, new RecognizedEventArgs(text, 1.0, null, null));

    public void Reject(string text)
        => Rejected?.Invoke(this, new RecognizedEventArgs(text, 0.0, null, null));
}

public sealed class MicOwnershipTests
{
    private readonly FakeRecognitionWindow _window = new();
    private readonly MicOwnership _mic;

    public MicOwnershipTests()
    {
        _mic = new MicOwnership(_window, NullLogger<MicOwnership>.Instance);
    }

    // ---- borrow / restore ----

    [Fact]
    public void Borrow_SetsIsBorrowed_AndDisposeRestoresTheOpenWindow()
    {
        string[] idle = ["alpha checklist", "test phrase"];
        _window.OpenListeningWindow(idle);
        Assert.False(_mic.IsBorrowed);

        var borrow = _mic.Borrow("techlog:raise");
        Assert.True(_mic.IsBorrowed);

        // The dialogue swaps grammars freely while it owns the mic.
        _window.OpenListeningWindow(["affirm", "negative"]);
        _window.CloseListeningWindow();

        borrow.Dispose();
        Assert.False(_mic.IsBorrowed);
        Assert.True(_window.WindowOpen);
        Assert.Equal(idle, _window.CurrentGrammar);
    }

    [Fact]
    public void Borrow_WhenWindowWasClosed_DisposeRestoresClosed()
    {
        Assert.False(_window.WindowOpen);

        var borrow = _mic.Borrow("techlog:raise");
        _window.OpenListeningWindow(["affirm"]);
        borrow.Dispose();

        Assert.False(_window.WindowOpen);
    }

    [Fact]
    public void Borrow_Nested_Throws()
    {
        using var borrow = _mic.Borrow("first");
        var ex = Assert.Throws<InvalidOperationException>(() => _mic.Borrow("second"));
        Assert.Contains("first", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Borrow_DisposeIsIdempotent()
    {
        _window.OpenListeningWindow(["a"]);
        var borrow = _mic.Borrow("x");
        borrow.Dispose();
        var opensAfterFirstDispose = _window.Opened.Count;
        borrow.Dispose();
        Assert.Equal(opensAfterFirstDispose, _window.Opened.Count);

        using var next = _mic.Borrow("y"); // the mic is free again
        Assert.True(_mic.IsBorrowed);
    }

    [Fact]
    public void Borrow_RestoresOnException()
    {
        string[] idle = ["test phrase"];
        _window.OpenListeningWindow(idle);

        void ThrowingDialogue()
        {
            using var borrow = _mic.Borrow("techlog:raise");
            _window.OpenListeningWindow([]);
            throw new InvalidOperationException("dialogue blew up");
        }

        Assert.Throws<InvalidOperationException>(ThrowingDialogue);

        Assert.False(_mic.IsBorrowed);
        Assert.True(_window.WindowOpen);
        Assert.Equal(idle, _window.CurrentGrammar);
    }

    // ---- ListenAsync ----

    [Fact]
    public async Task ListenAsync_OpensGrammar_ResolvesOnFirstUtterance_Trimmed()
    {
        string[] grammar = ["affirm", "negative"];
        var listen = _mic.ListenAsync(grammar, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(grammar, _window.CurrentGrammar);
        Assert.True(_window.WindowOpen);

        _window.Hear("  affirm ");
        Assert.Equal("affirm", await listen);
        Assert.False(_window.WindowOpen); // its own window is closed afterwards
    }

    [Fact]
    public async Task ListenAsync_EmptyGrammar_PassesRawTranscriptionThrough()
    {
        var listen = _mic.ListenAsync([], TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Empty(_window.CurrentGrammar); // free-form: nothing for engines to snap onto

        _window.Hear("cabin door seal worn on door two left");
        Assert.Equal("cabin door seal worn on door two left", await listen);
    }

    [Fact]
    public async Task ListenAsync_Timeout_ReturnsNull()
    {
        var result = await _mic.ListenAsync(
            ["affirm"], TimeSpan.FromMilliseconds(50), CancellationToken.None);
        Assert.Null(result);
        Assert.False(_window.WindowOpen);
    }

    [Fact]
    public async Task ListenAsync_IgnoresBlankUtterances()
    {
        var listen = _mic.ListenAsync(["affirm"], TimeSpan.FromSeconds(5), CancellationToken.None);
        _window.Hear("   ");
        _window.Hear("affirm");
        Assert.Equal("affirm", await listen);
    }

    // ---- routing suppression (the actual seam in SpokenChecklistEngine) ----

    private sealed class ProbeFeature : IVoiceFeature
    {
        public int Handled { get; private set; }

        public bool Enabled => true;

        public IEnumerable<string> Phrases => ["test phrase"];

        public bool ValueParse => false;

        public bool TryHandle(string utterance)
        {
            if (!string.Equals(utterance, "test phrase", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            Handled++;
            return true;
        }
    }

    [Fact]
    public void Borrow_SuppressesUtteranceRouting_AndResumesAfterDispose()
    {
        var speechOptions = new SpeechOptions();
        var monitor = SpeechTestSupport.SpeechMonitor(speechOptions);
        var arbiter = new FakeArbiter();
        var dataRefs = Mock.Of<IProsimDataRefs>();
        var phases = new FakePhaseSource();
        var eventLog = SpeechTestSupport.TempEventLog();
        var probe = new ProbeFeature();

        using var checklists = new ChecklistService(
            dataRefs,
            SpeechTestSupport.ChecklistMonitor(new ChecklistOptions()),
            NullLogger<ChecklistService>.Instance);
        using var engine = new SpokenChecklistEngine(
            monitor,
            arbiter,
            _window,
            _mic,
            new UtteranceInterpreter(monitor),
            checklists,
            dataRefs,
            new ControlMonitor(dataRefs, NullLogger<ControlMonitor>.Instance),
            new ControlSweepService(dataRefs, NullLogger<ControlSweepService>.Instance),
            new FailureMonitor(arbiter, dataRefs, phases, eventLog, NullLogger<FailureMonitor>.Instance),
            [probe],
            new SpeechStatusStore(),
            eventLog,
            NullLogger<SpokenChecklistEngine>.Instance,
            new ProsimCompanion.Speech.Briefings.MinimaCaptureDialogue(
                SpeechTestSupport.BriefingMonitor(new BriefingOptions()),
                _mic,
                new ArrivalMinimaStore(),
                arbiter,
                eventLog,
                NullLogger<ProsimCompanion.Speech.Briefings.MinimaCaptureDialogue>.Instance),
            SpeechTestSupport.PhraseBank(),
            SpeechTestSupport.Persona(),
            new ProsimCompanion.Speech.Commands.SpokenTokenSource(
                dataRefs,
                SpeechTestSupport.BriefingMonitor(new BriefingOptions()),
                NullLogger<ProsimCompanion.Speech.Commands.SpokenTokenSource>.Instance),
            new LlmHealthStore());
        engine.Start();

        _window.Hear("test phrase");
        Assert.Equal(1, probe.Handled);

        using (_mic.Borrow("techlog:test"))
        {
            _window.Hear("test phrase");
            _window.Reject("mumble");
            Assert.Equal(1, probe.Handled); // routing stands down while borrowed
        }

        _window.Hear("test phrase");
        Assert.Equal(2, probe.Handled); // and resumes after the borrow ends
    }
}
