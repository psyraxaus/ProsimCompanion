using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Gsx;
using ProsimCompanion.Speech.Recognition;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>
/// The one captain's-question dialogue over its four phrase tables (architecture review C2):
/// what each table counts as yes/no/a pick, the readbacks, the endings (answered, nobody
/// answered, handed back, answered elsewhere, voice off) and the #166 rule that the FO's
/// listen window never settles a question. The de-icing, turnaround and gate suites this
/// replaces are kept assertion for assertion; the pushback table's are in
/// <see cref="PushbackDirectionVoiceTests"/>.
/// </summary>
public sealed class CaptainsQuestionDialogueTests : IDisposable
{
    private static readonly IReadOnlyList<QuestionChoice> Variants =
        [new("36A", "Gate W 36A", null), new("36B", "Gate W 36B", "Safedock")];

    private readonly FakeArbiter _arbiter = new();
    private readonly GsxOptions _options = new() { VoiceControlEnabled = true };
    private readonly GroundOpsSignals _signals = new();
    private readonly DeiceRequestStore _deice;
    private readonly TurnaroundStartStore _turnaround;
    private readonly GateChoiceStore _gate;

    public CaptainsQuestionDialogueTests()
    {
        _deice = new DeiceRequestStore(_signals);
        _turnaround = new TurnaroundStartStore(_signals);
        _gate = new GateChoiceStore(_signals);
    }

    public void Dispose()
    {
        _deice.Dispose();
        _turnaround.Dispose();
        _gate.Dispose();
    }

    private CaptainsQuestionDialogue Create(ICaptainsQuestionStore store, CaptainsQuestionPhrases table, string? heard)
    {
        var mic = new Mock<IMicOwnership>();
        mic.Setup(m => m.Borrow(It.IsAny<string>())).Returns(Mock.Of<IDisposable>());
        mic.Setup(m => m.ListenAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(heard);
        var options = new Mock<IOptionsMonitor<GsxOptions>>();
        options.SetupGet(o => o.CurrentValue).Returns(() => _options);
        return new CaptainsQuestionDialogue([store], [table], mic.Object, _arbiter, options.Object, SpeechTestSupport.TempEventLog(), NullLogger<CaptainsQuestionDialogue>.Instance);
    }

    // ================================================================ de-icing (2026-10-09)

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
    [InlineData("negative de-icing", false)]
    [InlineData("decline de-icing", false)]
    public void Deice_Interpret_ClassifiesTheAnswer(string heard, bool? expected)
        => Assert.Equal(expected, DeiceQuestionPhrases.Interpret(heard));

    private CaptainsQuestion AskDeice()
    {
        _deice.Publish(DeicePolicyVerdict.Ask, "snow", -2, "snow", "EGLL", "Captain, conditions call for de-icing — OAT -2, snow. Request it?");
        return _deice.Snapshot().Question!;
    }

    [Fact]
    public async Task Deice_HeardYes_AcceptsAndConfirms()
    {
        var dialogue = Create(_deice, DeiceQuestionPhrases.Table, "affirm");
        await dialogue.RunAsync(AskDeice());

        Assert.True(_deice.Snapshot().RequestThisCycle);
        Assert.Null(_deice.Snapshot().Question);
        Assert.Equal("Captain, conditions call for de-icing — OAT -2, snow. Request it?", _arbiter.Requests[0].Text);
        Assert.Equal("Copied — requesting de-icing.", _arbiter.Requests[1].Text);
    }

    [Fact]
    public async Task Deice_HeardNo_DeclinesWithTheWords()
    {
        var dialogue = Create(_deice, DeiceQuestionPhrases.Table, "negative");
        await dialogue.RunAsync(AskDeice());

        Assert.False(_deice.Snapshot().RequestThisCycle);
        Assert.Equal("captain said 'negative'", _deice.Snapshot().Declined);
        Assert.Equal("Copied — no de-icing.", _arbiter.Requests[^1].Text);
    }

    /// <summary>Issue #166 (EFHK 2026-10-10): 32 s of silence declined the question and GSX's
    /// own offer was then accepted against that decline. Silence leaves the question open.</summary>
    [Fact]
    public async Task Deice_NoAnswer_LeavesTheQuestionOpen_AndHandsItBack()
    {
        var dialogue = Create(_deice, DeiceQuestionPhrases.Table, null);
        var question = AskDeice();
        await dialogue.RunAsync(question);

        Assert.False(_deice.Snapshot().RequestThisCycle);
        Assert.Null(_deice.Snapshot().Declined);
        Assert.NotNull(_deice.Snapshot().Question);
        Assert.True(_deice.Snapshot().QuestionOpen(DateTimeOffset.UtcNow));
        Assert.Equal(DeiceQuestionPhrases.QuestionStandsPhrase, _arbiter.Requests[^1].Text);

        // The web button (or a voice command, or the TTL sweep) answers it afterwards.
        _deice.Decline("web", "declined on the Status board");
        Assert.Equal("declined on the Status board", _deice.Snapshot().Declined);
    }

    [Fact]
    public void Deice_ReAsk_SaysItIsTheLastItem()
    {
        var first = new CaptainsQuestion("deice", DateTimeOffset.UtcNow, "Request it?", TimeSpan.FromMinutes(5), 1, [], null);
        Assert.Equal("Request it?", DeiceQuestionPhrases.SpokenPrompt(first));

        var again = first with { Attempt = 2 };
        Assert.StartsWith("Captain, de-icing is the last item before pushback and still needs your call.", DeiceQuestionPhrases.SpokenPrompt(again), StringComparison.Ordinal);
        Assert.EndsWith("Request it?", DeiceQuestionPhrases.SpokenPrompt(again), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deice_VoiceControlOff_LeavesTheQuestionForTheWebButtons()
    {
        _options.VoiceControlEnabled = false;
        var dialogue = Create(_deice, DeiceQuestionPhrases.Table, "affirm");
        await dialogue.RunAsync(AskDeice());

        Assert.NotNull(_deice.Snapshot().Question);
        Assert.Empty(_arbiter.Requests);
    }

    [Fact]
    public async Task Deice_SayAgain_RepeatsTheBarePrompt_ThenHandsBack()
    {
        var dialogue = Create(_deice, DeiceQuestionPhrases.Table, "say again");
        var question = AskDeice();
        _deice.MarkStepNext(); // attempt 2 — the spoken prompt carries the preamble
        await dialogue.RunAsync(_deice.Snapshot().Question!);

        Assert.StartsWith("Captain, de-icing is the last item", _arbiter.Requests[0].Text, StringComparison.Ordinal);
        Assert.Equal(question.Prompt, _arbiter.Requests[1].Text); // "say again" → the bare prompt
        Assert.Equal(DeiceQuestionPhrases.QuestionStandsPhrase, _arbiter.Requests[^1].Text);
        Assert.NotNull(_deice.Snapshot().Question);
    }

    [Fact]
    public async Task Deice_Unclear_AsksAgain_ThenHandsBack()
    {
        var dialogue = Create(_deice, DeiceQuestionPhrases.Table, "what");
        await dialogue.RunAsync(AskDeice());

        Assert.Equal("Say again — request de-icing, or negative?", _arbiter.Requests[1].Text);
        Assert.Equal("Say again — request de-icing, or negative?", _arbiter.Requests[2].Text);
        Assert.Equal(DeiceQuestionPhrases.QuestionStandsPhrase, _arbiter.Requests[^1].Text);
    }

    // ================================================================ turnaround start (issue #169)

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
    public void Turnaround_Interpret_ClassifiesTheAnswer(string heard, bool? expected)
        => Assert.Equal(expected, TurnaroundStartPhrases.Interpret(heard));

    private CaptainsQuestion AskTurnaround()
    {
        _turnaround.Ask("Next plan loaded — commence ground services?");
        return _turnaround.Snapshot().Question!;
    }

    [Fact]
    public async Task Turnaround_HeardYes_AcceptsAsVoice()
    {
        (bool Accepted, string Source)? answer = null;
        _turnaround.Answered += (_, a) => answer = a;
        var dialogue = Create(_turnaround, TurnaroundStartPhrases.Table, "affirm");

        await dialogue.RunAsync(AskTurnaround());

        Assert.Equal((true, "voice"), answer);
        Assert.Contains(_arbiter.Requests, r => r.Text == "Copied — commencing ground services.");
    }

    [Fact]
    public async Task Turnaround_HeardNo_DeclinesAndStandsBy()
    {
        var dialogue = Create(_turnaround, TurnaroundStartPhrases.Table, "not yet");

        await dialogue.RunAsync(AskTurnaround());

        Assert.False(_turnaround.Snapshot().Accepted);
        Assert.Contains("not yet", _turnaround.Snapshot().Declined, StringComparison.Ordinal);
        Assert.Contains(_arbiter.Requests, r => r.Text == "Copied — standing by for your call.");
    }

    /// <summary>C2: the #166 rule reaches the turnaround question. Before, two unanswered
    /// listens declined it for the cycle ("no answer to the FO's question"); now the question
    /// stays open for the Start button / "commence ground services", and only the TTL
    /// declines it (source timeout).</summary>
    [Fact]
    public async Task Turnaround_NoAnswer_LeavesTheQuestionOpen_UntilTheTtlDeclinesIt()
    {
        (bool Accepted, string Source)? answer = null;
        _turnaround.Answered += (_, a) => answer = a;
        var dialogue = Create(_turnaround, TurnaroundStartPhrases.Table, heard: null);

        await dialogue.RunAsync(AskTurnaround());

        Assert.False(_turnaround.Snapshot().Accepted);
        Assert.Null(_turnaround.Snapshot().Declined);
        Assert.True(_turnaround.Snapshot().QuestionStands);
        Assert.Contains(_arbiter.Requests, r => r.Text.StartsWith("Your call", StringComparison.Ordinal));
        Assert.Null(answer);

        var asked = _turnaround.Snapshot().Question!.AskedAtUtc;
        _turnaround.ExpireOverdue(asked + TimeSpan.FromMinutes(5));
        Assert.Equal((false, "timeout"), answer);
        Assert.Equal("no answer within 5 minutes", _turnaround.Snapshot().Declined);
    }

    [Fact]
    public async Task Turnaround_AnsweredElsewhereFirst_SpeaksNothingMore()
    {
        var dialogue = Create(_turnaround, TurnaroundStartPhrases.Table, "yes");
        var question = AskTurnaround();
        _turnaround.Accept("web"); // the Start button beat the FO

        await dialogue.RunAsync(question);

        Assert.DoesNotContain(_arbiter.Requests, r => r.Text.StartsWith("Copied", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Turnaround_VoiceControlOff_NeverSpeaks()
    {
        _options.VoiceControlEnabled = false;
        var dialogue = Create(_turnaround, TurnaroundStartPhrases.Table, "yes");

        await dialogue.RunAsync(AskTurnaround());

        Assert.Empty(_arbiter.Requests);
        Assert.True(_turnaround.Snapshot().QuestionStands);
    }

    // ================================================================ arrival gate suffix (issue #156)

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
    public void Gate_Interpret_NamesTheCandidate(string heard, string? expectedLabel)
        => Assert.Equal(expectedLabel, GateChoicePhrases.Interpret(heard, Variants)?.Label);

    [Theory]
    [InlineData("36A", "36 Alpha")]
    [InlineData("36B", "36 Bravo")]
    [InlineData("545R", "545 Romeo")]
    [InlineData("Gate W 36A", "Gate W 36A")]
    public void Gate_SpokenLabel_SpellsTheSuffix(string label, string expected)
        => Assert.Equal(expected, GateChoicePhrases.SpokenLabel(label));

    private CaptainsQuestion AskGate()
    {
        _gate.Ask("W36", Variants, "W36 is 36A or 36B in this scenery — which?");
        return _gate.Snapshot().Question!;
    }

    [Fact]
    public async Task Gate_HeardAVariant_StoresTheChoice_AndReadsItBack()
    {
        var dialogue = Create(_gate, GateChoicePhrases.Table, "36 bravo");
        await dialogue.RunAsync(AskGate());

        Assert.Equal("W36 is 36 Alpha or 36 Bravo in this scenery — which?", _arbiter.Requests[0].Text);
        Assert.Equal("36 Bravo it is.", _arbiter.Requests[^1].Text);
        Assert.Equal("36B", _gate.Snapshot().Chosen?.Label);
        Assert.Equal("voice", _gate.Snapshot().Answer?.Source);
        Assert.Null(_gate.Snapshot().Question);
    }

    [Fact]
    public async Task Gate_NoAnswer_LeavesTheQuestionOpen_AndNamesTheDefault()
    {
        var dialogue = Create(_gate, GateChoicePhrases.Table, null);
        await dialogue.RunAsync(AskGate());

        Assert.NotNull(_gate.Snapshot().Question);
        Assert.Null(_gate.Snapshot().Answer);
        Assert.Equal("Your call — I'll take 36 Alpha at taxi-in unless you say otherwise.", _arbiter.Requests[^1].Text);
    }

    [Fact]
    public async Task Gate_HandedBack_LeavesTheQuestionOpen()
    {
        var dialogue = Create(_gate, GateChoicePhrases.Table, "your call");
        await dialogue.RunAsync(AskGate());

        Assert.NotNull(_gate.Snapshot().Question);
        Assert.Contains("Your call", _arbiter.Requests[^1].Text, StringComparison.Ordinal);
        Assert.Equal(2, _arbiter.Requests.Count); // the prompt and the hand-back — no second listen
    }

    [Fact]
    public async Task Gate_Unclear_AsksWithTheSpokenList()
    {
        var dialogue = Create(_gate, GateChoicePhrases.Table, "36");
        await dialogue.RunAsync(AskGate());

        Assert.Equal("Say again — 36 Alpha or 36 Bravo?", _arbiter.Requests[1].Text);
        Assert.NotNull(_gate.Snapshot().Question);
    }

    [Fact]
    public async Task Gate_VoiceControlOff_LeavesTheQuestionForTheWebButtons()
    {
        _options.VoiceControlEnabled = false;
        var dialogue = Create(_gate, GateChoicePhrases.Table, "alpha");
        await dialogue.RunAsync(AskGate());

        Assert.Empty(_arbiter.Requests);
        Assert.NotNull(_gate.Snapshot().Question);
    }

    // ================================================================ the dialogue itself

    [Fact]
    public async Task AQuestionWithoutATable_IsNotVoiced()
    {
        var dialogue = Create(_gate, GateChoicePhrases.Table, "affirm"); // no de-icing table wired
        await dialogue.RunAsync(AskDeice());

        Assert.Empty(_arbiter.Requests);
        Assert.NotNull(_deice.Snapshot().Question);
    }

    [Fact]
    public async Task Start_VoicesARaisedQuestion_AndDisposeStopsListening()
    {
        var dialogue = Create(_turnaround, TurnaroundStartPhrases.Table, "affirm");
        dialogue.Start();

        _turnaround.Ask("Next plan loaded — commence ground services?");
        await WaitUntilAsync(() => _turnaround.Snapshot().Accepted);
        Assert.True(_turnaround.Snapshot().Accepted);

        dialogue.Dispose();
        _signals.RaiseFlightCycleReset();
        _arbiter.Requests.Clear();
        _turnaround.Ask("again");
        await Task.Delay(50);
        Assert.Empty(_arbiter.Requests);
    }

    [Fact]
    public void TwoTablesForOneTopic_IsAWiringBug()
    {
        var mic = Mock.Of<IMicOwnership>();
        var options = Mock.Of<IOptionsMonitor<GsxOptions>>();
        Assert.Throws<ArgumentException>(() => new CaptainsQuestionDialogue(
            [_gate], [GateChoicePhrases.Table, GateChoicePhrases.Table], mic, _arbiter, options, SpeechTestSupport.TempEventLog(), NullLogger<CaptainsQuestionDialogue>.Instance));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(10);
        }
    }
}
