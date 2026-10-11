using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Gsx;
using ProsimCompanion.Speech.Recognition;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>The FO's "request de-icing?" question (2026-10-09): what counts as yes/no, and
/// the dialogue's three endings (yes, no, nobody answered).</summary>
public sealed class DeiceQuestionDialogueTests : IDisposable
{
    private readonly FakeArbiter _arbiter = new();
    private readonly GsxOptions _options = new() { VoiceControlEnabled = true };
    private readonly DeiceRequestStore _store = new(new GroundOpsSignals());

    public void Dispose() => _store.Dispose();

    [Theory]
    [InlineData("yes", true)]
    [InlineData("affirm", true)]
    [InlineData("request de-icing", true)]
    [InlineData("de-ice it", true)]
    [InlineData("no", false)]
    [InlineData("negative", false)]
    [InlineData("no de-icing", false)]
    [InlineData("not today", false)]
    [InlineData("your call", false)]
    [InlineData("what", null)]
    public void Interpret_ClassifiesTheAnswer(string heard, bool? expected)
        => Assert.Equal(expected, DeiceQuestionDialogue.Interpret(heard));

    private DeiceQuestionDialogue Create(string? heard)
    {
        var mic = new Mock<IMicOwnership>();
        mic.Setup(m => m.Borrow(It.IsAny<string>())).Returns(Mock.Of<IDisposable>());
        mic.Setup(m => m.ListenAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(heard);
        var options = new Mock<IOptionsMonitor<GsxOptions>>();
        options.SetupGet(o => o.CurrentValue).Returns(() => _options);
        return new DeiceQuestionDialogue(_store, mic.Object, _arbiter, options.Object, SpeechTestSupport.TempEventLog(), NullLogger<DeiceQuestionDialogue>.Instance);
    }

    private DeiceQuestion Ask()
    {
        _store.Publish(DeicePolicyVerdict.Ask, "snow", -2, "snow", "EGLL", "Captain, conditions call for de-icing — OAT -2, snow. Request it?");
        return _store.Snapshot().Question!;
    }

    [Fact]
    public async Task HeardYes_AcceptsAndConfirms()
    {
        var dialogue = Create("affirm");
        await dialogue.RunAsync(Ask());

        Assert.True(_store.Snapshot().RequestThisCycle);
        Assert.Null(_store.Snapshot().Question);
        Assert.Equal("Captain, conditions call for de-icing — OAT -2, snow. Request it?", _arbiter.Requests[0].Text);
        Assert.Equal("Copied — requesting de-icing.", _arbiter.Requests[1].Text);
    }

    [Fact]
    public async Task HeardNo_DeclinesWithTheWords()
    {
        var dialogue = Create("negative");
        await dialogue.RunAsync(Ask());

        Assert.False(_store.Snapshot().RequestThisCycle);
        Assert.Equal("captain said 'negative'", _store.Snapshot().Declined);
        Assert.Equal("Copied — no de-icing.", _arbiter.Requests[^1].Text);
    }

    /// <summary>Issue #166 (EFHK 2026-10-10): 32 s of silence declined the question and GSX's
    /// own offer was then accepted against that decline. Silence leaves the question open.</summary>
    [Fact]
    public async Task NoAnswer_LeavesTheQuestionOpen_AndHandsItBack()
    {
        var dialogue = Create(null);
        var question = Ask();
        await dialogue.RunAsync(question);

        Assert.False(_store.Snapshot().RequestThisCycle);
        Assert.Null(_store.Snapshot().Declined);
        Assert.NotNull(_store.Snapshot().Question);
        Assert.True(_store.Snapshot().QuestionOpen(DateTimeOffset.UtcNow));
        Assert.Equal(DeiceQuestionDialogue.QuestionStandsPhrase, _arbiter.Requests[^1].Text);

        // The web button (or a voice command, or the TTL sweep) answers it afterwards.
        _store.Decline("web", "declined on the Status board");
        Assert.Equal("declined on the Status board", _store.Snapshot().Declined);
    }

    [Fact]
    public void ReAsk_SaysItIsTheLastItem()
    {
        var first = new DeiceQuestion(DateTimeOffset.UtcNow, "Request it?", TimeSpan.FromMinutes(5));
        Assert.Equal("Request it?", DeiceQuestionDialogue.SpokenPrompt(first));

        var again = first with { Attempt = 2 };
        Assert.StartsWith("Captain, de-icing is the last item before pushback and still needs your call.", DeiceQuestionDialogue.SpokenPrompt(again), StringComparison.Ordinal);
        Assert.EndsWith("Request it?", DeiceQuestionDialogue.SpokenPrompt(again), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("negative de-icing", false)]
    [InlineData("decline de-icing", false)]
    public void Interpret_ClassifiesTheNewDeclinePhrases(string heard, bool? expected)
        => Assert.Equal(expected, DeiceQuestionDialogue.Interpret(heard));

    [Fact]
    public async Task VoiceControlOff_LeavesTheQuestionForTheWebButtons()
    {
        _options.VoiceControlEnabled = false;
        var dialogue = Create("affirm");
        await dialogue.RunAsync(Ask());

        Assert.NotNull(_store.Snapshot().Question);
        Assert.Empty(_arbiter.Requests);
    }
}
