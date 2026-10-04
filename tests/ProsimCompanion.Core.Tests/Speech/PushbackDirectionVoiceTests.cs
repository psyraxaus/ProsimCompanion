using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Airports.Parking;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Gsx;
using ProsimCompanion.Speech.Recognition;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Pushback direction by voice (2026-10-04): the phrases, the per-flight store, the
/// read-back and the FO's "tail left or tail right?" question.</summary>
public sealed class PushbackDirectionVoiceTests
{
    private readonly FakeArbiter _arbiter = new();
    private readonly GsxOptions _options = new() { VoiceControlEnabled = true };
    private readonly PushbackChoiceStore _store = new(new OfpStore(), new GroundOpsSignals());

    private static readonly PushbackOption[] EfhkOptions =
    [
        new("Facing SW on Taxi AV", PushbackOptionKind.Left, 227, "profile"),
        new("Facing NE on Taxi AT", PushbackOptionKind.Right, 47, "profile"),
    ];

    private PushbackDirectionVoiceFeature CreateFeature()
    {
        var options = new Mock<IOptionsMonitor<GsxOptions>>();
        options.SetupGet(o => o.CurrentValue).Returns(() => _options);
        return new PushbackDirectionVoiceFeature(_store, _arbiter, options.Object, SpeechTestSupport.TempEventLog(), NullLogger<PushbackDirectionVoiceFeature>.Instance);
    }

    [Theory]
    [InlineData("push back tail left", PushbackWish.TailLeft, null)]
    [InlineData("tail right", PushbackWish.TailRight, null)]
    [InlineData("pushback nose left", PushbackWish.TailRight, null)]
    [InlineData("push back nose right", PushbackWish.TailLeft, null)]
    [InlineData("straight back", PushbackWish.Straight, null)]
    [InlineData("push back facing north east", PushbackWish.Heading, 45.0)]
    [InlineData("facing south west", PushbackWish.Heading, 225.0)]
    public void Parser_ReadsTheWish(string said, PushbackWish wish, double? heading)
    {
        var choice = PushbackPhraseParser.Parse(said)!;
        Assert.Equal(wish, choice.Wish);
        Assert.Equal(heading, choice.HeadingDeg);
        Assert.Equal("voice", choice.Source);
    }

    [Theory]
    [InlineData("request pushback")]
    [InlineData("facing the music")]
    [InlineData("")]
    public void Parser_IgnoresOtherText(string said) => Assert.Null(PushbackPhraseParser.Parse(said));

    [Fact]
    public void EveryPhrase_Parses()
    {
        foreach (var phrase in PushbackPhraseParser.Phrases)
        {
            Assert.NotNull(PushbackPhraseParser.Parse(phrase));
        }
    }

    [Fact]
    public void TryHandle_StoresTheChoice_AndReadsBack()
    {
        var feature = CreateFeature();
        _store.SetSuggestion(null, EfhkOptions, "Gate 40", "22L");

        Assert.True(feature.TryHandle("push back facing north east"));

        var state = _store.Snapshot();
        Assert.Equal(PushbackWish.Heading, state.Choice!.Wish);
        Assert.Equal(45, state.Choice.HeadingDeg);
        var spoken = Assert.Single(_arbiter.Requests);
        Assert.Equal("Pushback facing north-east — that is tail right here.", spoken.Text);
    }

    [Fact]
    public void TryHandle_CompassWithNoRoute_SaysSo()
    {
        var feature = CreateFeature();
        _store.SetSuggestion(null, EfhkOptions, "Gate 40", "22L");

        Assert.True(feature.TryHandle("push back facing south"));
        Assert.Contains("no route faces south", _arbiter.Requests.Single().Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TryHandle_Query_AnswersWithSuggestion()
    {
        var feature = CreateFeature();
        _store.SetSuggestion(
            new PushbackSuggestion(EfhkOptions[1], PushbackConfidence.High, "faces the runway", 50),
            EfhkOptions, "Gate 40", "22L");

        Assert.True(feature.TryHandle("which way is the pushback"));
        Assert.Equal("I suggest tail right for runway 22L — it faces the runway.", _arbiter.Requests.Single().Text);
    }

    [Fact]
    public void TryHandle_Disabled_DoesNothing()
    {
        _options.VoiceControlEnabled = false;
        Assert.False(CreateFeature().TryHandle("tail left"));
        Assert.Null(_store.Snapshot().Choice);
    }

    [Fact]
    public void Query_WithNothingKnown_AsksForADirection()
        => Assert.StartsWith("No pushback direction yet", PushbackPhraseParser.Answer(PushbackChoiceSnapshot.Empty), StringComparison.Ordinal);

    [Fact]
    public void Prompt_DefaultLines_TailLeftOrRight()
    {
        var options = PushbackAdvisor.Options(null, [PushbackAdvisor.DefaultLeftLabel, PushbackAdvisor.DefaultRightLabel, "Straight pushback"]);
        Assert.Equal("Pushback — tail left or tail right?", PushbackQuestionDialogue.Prompt(options));
    }

    [Fact]
    public void Prompt_CustomLines_NamesThem()
        => Assert.Equal("Pushback — which way? GSX offers Facing SW on Taxi AV, or Facing NE on Taxi AT.", PushbackQuestionDialogue.Prompt(EfhkOptions));

    [Fact]
    public async Task Question_HeardTailLeft_StoresAndReadsBack()
    {
        var feature = CreateFeature();
        var mic = new Mock<IMicOwnership>();
        mic.Setup(m => m.Borrow(It.IsAny<string>())).Returns(Mock.Of<IDisposable>());
        mic.Setup(m => m.ListenAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("tail left");
        var options = new Mock<IOptionsMonitor<GsxOptions>>();
        options.SetupGet(o => o.CurrentValue).Returns(() => _options);
        var dialogue = new PushbackQuestionDialogue(_store, feature, mic.Object, _arbiter, options.Object, SpeechTestSupport.TempEventLog(), NullLogger<PushbackQuestionDialogue>.Instance);

        _store.AskPilot(EfhkOptions);
        await dialogue.RunAsync(_store.Snapshot().Question!);

        Assert.Equal(PushbackWish.TailLeft, _store.Snapshot().Choice!.Wish);
        Assert.Null(_store.Snapshot().Question);
        Assert.Equal("Pushback — which way? GSX offers Facing SW on Taxi AV, or Facing NE on Taxi AT.", _arbiter.Requests[0].Text);
        Assert.Equal("Pushback tail left.", _arbiter.Requests[1].Text);
    }

    [Fact]
    public async Task Question_Timeout_HandsTheMenuBack()
    {
        var feature = CreateFeature();
        var mic = new Mock<IMicOwnership>();
        mic.Setup(m => m.Borrow(It.IsAny<string>())).Returns(Mock.Of<IDisposable>());
        mic.Setup(m => m.ListenAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        var options = new Mock<IOptionsMonitor<GsxOptions>>();
        options.SetupGet(o => o.CurrentValue).Returns(() => _options);
        var dialogue = new PushbackQuestionDialogue(_store, feature, mic.Object, _arbiter, options.Object, SpeechTestSupport.TempEventLog(), NullLogger<PushbackQuestionDialogue>.Instance);

        _store.AskPilot(EfhkOptions);
        await dialogue.RunAsync(_store.Snapshot().Question!);

        Assert.Null(_store.Snapshot().Choice);
        Assert.Null(_store.Snapshot().Question);
        Assert.Equal("Your call — the GSX pushback menu is open.", _arbiter.Requests[^1].Text);
    }

    [Fact]
    public void Store_ResetsOnNewOfp_AndKeepsOnSameOfp()
    {
        var ofp = new OfpStore();
        var store = new PushbackChoiceStore(ofp, new GroundOpsSignals());
        store.Choose(PushbackChoice.TailLeft("web", "button"));

        ofp.Set(new OfpData { RequestId = "A" });
        Assert.Null(store.Snapshot().Choice);

        store.Choose(PushbackChoice.TailRight("web", "button"));
        ofp.Set(new OfpData { RequestId = "A" });
        Assert.NotNull(store.Snapshot().Choice);
    }
}
