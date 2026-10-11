using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Gsx;
using ProsimCompanion.Speech.Recognition;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>The FO's "W36 is 36 Alpha or 36 Bravo — which?" question (issue #156): what counts
/// as an answer, and the dialogue's endings (answered, nobody answered, voice off).</summary>
public sealed class GateChoiceDialogueTests : IDisposable
{
    private static readonly IReadOnlyList<GateCandidate> Variants =
        [new("36A", "Gate W 36A", null), new("36B", "Gate W 36B", "Safedock")];

    private readonly FakeArbiter _arbiter = new();
    private readonly GsxOptions _options = new() { VoiceControlEnabled = true };
    private readonly GateChoiceStore _store = new(new GroundOpsSignals());

    public void Dispose() => _store.Dispose();

    [Theory]
    [InlineData("36 alpha", "36A")]
    [InlineData("thirty six alpha", "36A")]
    [InlineData("alpha", "36A")]
    [InlineData("bravo", "36B")]
    [InlineData("36B", "36B")]
    [InlineData("36a", "36A")]
    [InlineData("the first", "36A")]
    [InlineData("second one", "36B")]
    [InlineData("b", "36B")]
    [InlineData("36", null)]
    [InlineData("what", null)]
    [InlineData("alpha bravo", null)]
    public void Interpret_NamesTheCandidate(string heard, string? expectedLabel)
        => Assert.Equal(expectedLabel, GateChoiceDialogue.Interpret(heard, Variants)?.Label);

    [Theory]
    [InlineData("36A", "36 Alpha")]
    [InlineData("36B", "36 Bravo")]
    [InlineData("545R", "545 Romeo")]
    [InlineData("Gate W 36A", "Gate W 36A")]
    public void SpokenLabel_SpellsTheSuffix(string label, string expected)
        => Assert.Equal(expected, GateChoiceDialogue.SpokenLabel(label));

    private GateChoiceDialogue Create(string? heard)
    {
        var mic = new Mock<IMicOwnership>();
        mic.Setup(m => m.Borrow(It.IsAny<string>())).Returns(Mock.Of<IDisposable>());
        mic.Setup(m => m.ListenAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(heard);
        var options = new Mock<IOptionsMonitor<GsxOptions>>();
        options.SetupGet(o => o.CurrentValue).Returns(() => _options);
        return new GateChoiceDialogue(_store, mic.Object, _arbiter, options.Object, SpeechTestSupport.TempEventLog(), NullLogger<GateChoiceDialogue>.Instance);
    }

    private GateChoiceQuestion Ask()
    {
        _store.Ask("W36", Variants, "W36 is 36A or 36B in this scenery — which?");
        return _store.Snapshot().Question!;
    }

    [Fact]
    public async Task HeardAVariant_StoresTheChoice_AndReadsItBack()
    {
        var dialogue = Create("36 bravo");
        await dialogue.RunAsync(Ask());

        Assert.Equal("W36 is 36 Alpha or 36 Bravo in this scenery — which?", _arbiter.Requests[0].Text);
        Assert.Equal("36 Bravo it is.", _arbiter.Requests[^1].Text);
        Assert.Equal("36B", _store.Snapshot().Choice?.Candidate.Label);
        Assert.Equal("voice", _store.Snapshot().Choice?.Source);
        Assert.Null(_store.Snapshot().Question);
    }

    [Fact]
    public async Task NoAnswer_LeavesTheQuestionOpen_AndNamesTheDefault()
    {
        var dialogue = Create(null);
        await dialogue.RunAsync(Ask());

        Assert.NotNull(_store.Snapshot().Question);
        Assert.Null(_store.Snapshot().Choice);
        Assert.Equal("Your call — I'll take 36 Alpha at taxi-in unless you say otherwise.", _arbiter.Requests[^1].Text);
    }

    [Fact]
    public async Task HandedBack_LeavesTheQuestionOpen()
    {
        var dialogue = Create("your call");
        await dialogue.RunAsync(Ask());

        Assert.NotNull(_store.Snapshot().Question);
        Assert.Contains("Your call", _arbiter.Requests[^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VoiceControlOff_LeavesTheQuestionForTheWebButtons()
    {
        _options.VoiceControlEnabled = false;
        var dialogue = Create("alpha");
        await dialogue.RunAsync(Ask());

        Assert.Empty(_arbiter.Requests);
        Assert.NotNull(_store.Snapshot().Question);
    }
}
