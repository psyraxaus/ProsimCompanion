using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Gsx;
using ProsimCompanion.Speech.Recognition;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>The FO's "Next plan loaded — commence ground services?" question (issue #169,
/// ask-first): what counts as yes/no, and the dialogue's three endings.</summary>
public sealed class TurnaroundStartDialogueTests : IDisposable
{
    private readonly FakeArbiter _arbiter = new();
    private readonly GsxOptions _options = new() { VoiceControlEnabled = true };
    private readonly TurnaroundStartStore _store = new(new GroundOpsSignals());

    public void Dispose() => _store.Dispose();

    [Theory]
    [InlineData("yes", true)]
    [InlineData("affirm", true)]
    [InlineData("commence ground services", true)]
    [InlineData("go ahead", true)]
    [InlineData("no", false)]
    [InlineData("negative", false)]
    [InlineData("not yet", false)]
    [InlineData("stand by", false)]
    [InlineData("standby", false)]
    [InlineData("hold", false)]
    [InlineData("what", null)]
    public void Interpret_ClassifiesTheAnswer(string heard, bool? expected)
        => Assert.Equal(expected, TurnaroundStartDialogue.Interpret(heard));

    private TurnaroundStartDialogue Create(string? heard)
    {
        var mic = new Mock<IMicOwnership>();
        mic.Setup(m => m.Borrow(It.IsAny<string>())).Returns(Mock.Of<IDisposable>());
        mic.Setup(m => m.ListenAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(heard);
        var options = new Mock<IOptionsMonitor<GsxOptions>>();
        options.SetupGet(o => o.CurrentValue).Returns(() => _options);
        return new TurnaroundStartDialogue(
            _store, mic.Object, _arbiter, options.Object, SpeechTestSupport.TempEventLog(), NullLogger<TurnaroundStartDialogue>.Instance);
    }

    private TurnaroundStartQuestion Ask()
    {
        _store.Ask("Next plan loaded — commence ground services?");
        return _store.Snapshot().Question!;
    }

    [Fact]
    public async Task HeardYes_AcceptsAsVoice()
    {
        (bool Accepted, string Source)? answer = null;
        _store.Answered += (_, a) => answer = a;
        var dialogue = Create("affirm");

        await dialogue.RunAsync(Ask());

        Assert.Equal((true, "voice"), answer);
        Assert.Contains(_arbiter.Requests, r => r.Text == "Copied — commencing ground services.");
    }

    [Fact]
    public async Task HeardNo_DeclinesAndStandsBy()
    {
        var dialogue = Create("not yet");

        await dialogue.RunAsync(Ask());

        Assert.False(_store.Snapshot().Accepted);
        Assert.Contains("not yet", _store.Snapshot().Declined, StringComparison.Ordinal);
        Assert.Contains(_arbiter.Requests, r => r.Text == "Copied — standing by for your call.");
    }

    [Fact]
    public async Task NoAnswer_DeclinesAndHandsItBack()
    {
        var dialogue = Create(heard: null);

        await dialogue.RunAsync(Ask());

        Assert.False(_store.Snapshot().Accepted);
        Assert.Equal("no answer to the FO's question", _store.Snapshot().Declined);
        Assert.Contains(_arbiter.Requests, r => r.Text.StartsWith("Your call", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnsweredElsewhereFirst_SpeaksNothingMore()
    {
        var dialogue = Create("yes");
        var question = Ask();
        _store.Accept("web"); // the Start button beat the FO

        await dialogue.RunAsync(question);

        Assert.DoesNotContain(_arbiter.Requests, r => r.Text.StartsWith("Copied", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VoiceControlOff_NeverSpeaks()
    {
        _options.VoiceControlEnabled = false;
        var dialogue = Create("yes");

        await dialogue.RunAsync(Ask());

        Assert.Empty(_arbiter.Requests);
        Assert.True(_store.Snapshot().QuestionOpen);
    }
}
