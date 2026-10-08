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

    [Fact]
    public async Task NoAnswer_DeclinesAndHandsItBack()
    {
        var dialogue = Create(null);
        await dialogue.RunAsync(Ask());

        Assert.False(_store.Snapshot().RequestThisCycle);
        Assert.Equal("no answer to the FO's question", _store.Snapshot().Declined);
        Assert.Equal("Your call — de-icing not requested. Say request de-icing if you want it.", _arbiter.Requests[^1].Text);
    }

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
