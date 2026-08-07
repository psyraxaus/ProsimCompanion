using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.TechLog;
using ProsimCompanion.Core.Tests.TechLog;
using ProsimCompanion.Speech.Recognition;
using ProsimCompanion.Speech.TechLog;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Scripted speak/listen seam: prompts are recorded, listens are answered from a
/// queue (missing entries read as timeout/null).</summary>
internal sealed class FakeDialogueIo : ITechLogDialogueIo
{
    public List<string> Spoken { get; } = [];

    public List<(IReadOnlyList<string> Grammar, TimeSpan Timeout)> Listens { get; } = [];

    public Queue<string?> Replies { get; } = new();

    public Task SpeakAsync(string text, CancellationToken cancellationToken)
    {
        Spoken.Add(text);
        return Task.CompletedTask;
    }

    public Task<string?> ListenAsync(
        IReadOnlyList<string> grammar, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Listens.Add((grammar, timeout));
        return Task.FromResult(Replies.Count > 0 ? Replies.Dequeue() : null);
    }
}

public sealed class TechLogDialogueCoreTests : IDisposable
{
    private readonly string _dir;
    private readonly TechLogOptions _options;
    private readonly JsonlEventLog _eventLog;
    private readonly TechLogService _techLog;
    private readonly FakeDialogueIo _io = new();
    private readonly TechLogDialogueCore _core;

    public TechLogDialogueCoreTests()
    {
        _dir = Directory.CreateTempSubdirectory("pc-techlog-dialogue-").FullName;
        _options = new TechLogOptions { Path = Path.Combine(_dir, "techlog.json") };
        _eventLog = SpeechTestSupport.TempEventLog();
        _techLog = new TechLogService(
            OptionsSupport.Monitor(_options), _eventLog, new FakePhaseSource(),
            NullLogger<TechLogService>.Instance, new TestTimeProvider());
        _techLog.Start();
        _core = new TechLogDialogueCore(_techLog, _io, _eventLog);
    }

    public void Dispose()
    {
        _techLog.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    private string SessionId => Path.GetFileNameWithoutExtension(_eventLog.Path);

    // ---- raise ----

    [Fact]
    public async Task Raise_HappyPath_SpeaksExactSequence_AndRaisesTheDefect()
    {
        _io.Replies.Enqueue("Cabin door seal worn");
        _io.Replies.Enqueue("bravo");
        _io.Replies.Enqueue("affirm");

        await _core.RunRaiseAsync(CancellationToken.None);

        Assert.Equal(
            [
                "Go ahead with the defect.",
                "What M E L category — Bravo, Charlie, or Delta?",
                "Logging Cabin door seal worn. M E L category Bravo, 3 day interval. Confirm?",
                "Tech log entry made. 3 days to rectify.",
            ],
            _io.Spoken);

        var defect = Assert.Single(_techLog.OpenDefects);
        Assert.Equal("Cabin door seal worn", defect.Title);
        Assert.Equal(MelCategory.B, defect.Category);
        Assert.Equal("manual", defect.Source);
    }

    [Fact]
    public async Task Raise_TitleCapture_IsFreeForm15Seconds_ThenClosedGrammar8Seconds()
    {
        _io.Replies.Enqueue("APU inoperative");
        _io.Replies.Enqueue("charlie");
        _io.Replies.Enqueue("affirm");

        await _core.RunRaiseAsync(CancellationToken.None);

        Assert.Equal(3, _io.Listens.Count);
        Assert.Empty(_io.Listens[0].Grammar); // free-form — no command snapping
        Assert.Equal(TimeSpan.FromSeconds(15), _io.Listens[0].Timeout);
        Assert.Equal(TechLogDialogueCore.CategoryGrammar, _io.Listens[1].Grammar);
        Assert.Equal(TimeSpan.FromSeconds(8), _io.Listens[1].Timeout);
        Assert.Equal(ConfirmVocabulary.All, _io.Listens[2].Grammar);
        Assert.Equal(TimeSpan.FromSeconds(8), _io.Listens[2].Timeout);
    }

    [Fact]
    public async Task Raise_TitleTimeout_AbortsToTheWebPage()
    {
        _io.Replies.Enqueue(null);

        await _core.RunRaiseAsync(CancellationToken.None);

        Assert.Equal(
            [
                "Go ahead with the defect.",
                "I didn't catch that — you can enter it on the tech log page.",
            ],
            _io.Spoken);
        Assert.Single(_io.Listens);
        Assert.Empty(_techLog.Defects);
    }

    [Theory]
    [InlineData("alpha", MelCategory.A, 3)]
    [InlineData("category bravo", MelCategory.B, 3)]
    [InlineData("charlie", MelCategory.C, 10)]
    [InlineData("category delta", MelCategory.D, 120)]
    [InlineData("make it bravo please", MelCategory.B, 3)] // raw transcription still maps
    [InlineData("no idea", MelCategory.C, 10)] // unclear defaults to C
    [InlineData(null, MelCategory.C, 10)] // timeout defaults to C
    public async Task Raise_CategoryAnswers_MapWithDefaultC(
        string? said, MelCategory expected, int expectedDays)
    {
        _io.Replies.Enqueue("Wing light u/s");
        _io.Replies.Enqueue(said);
        _io.Replies.Enqueue("affirm");

        await _core.RunRaiseAsync(CancellationToken.None);

        var defect = Assert.Single(_techLog.OpenDefects);
        Assert.Equal(expected, defect.Category);
        Assert.Equal(expectedDays, defect.RepairIntervalDays);
        Assert.Contains(
            $"M E L category {TechLogDialogueCore.SpokenCategory(expected)}, {expectedDays} day interval",
            _io.Spoken[2], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("negative")]
    [InlineData("something unrelated")]
    [InlineData(null)] // confirm timeout
    public async Task Raise_NonAffirmConfirm_DisregardsWithoutRaising(string? confirm)
    {
        _io.Replies.Enqueue("Wing light u/s");
        _io.Replies.Enqueue("charlie");
        _io.Replies.Enqueue(confirm);

        await _core.RunRaiseAsync(CancellationToken.None);

        Assert.Equal("Disregarded — nothing entered.", _io.Spoken[^1]);
        Assert.Empty(_techLog.Defects);
    }

    // ---- rectify ----

    [Fact]
    public async Task Rectify_CleanLog_SaysSoWithoutListening()
    {
        await _core.RunRectifyAsync(CancellationToken.None);

        Assert.Equal("The tech log is already clean — nothing to rectify.", Assert.Single(_io.Spoken));
        Assert.Empty(_io.Listens);
    }

    [Fact]
    public async Task Rectify_Affirm_RectifiesTheMostDueDefect()
    {
        // B defers 3 days, D defers 120 — the B item is most due and must be offered first.
        // Ids are set explicitly: the frozen test clock would otherwise mint the same
        // timestamp id twice and the second raise would replace the first.
        var gear = _techLog.NewDraft("manual", "Slow gear retraction", MelCategory.D);
        gear.Id = "def-gear";
        _techLog.RaiseDefect(gear);
        var apu = _techLog.NewDraft("manual", "APU inoperative", MelCategory.B);
        apu.Id = "def-apu";
        _techLog.RaiseDefect(apu);
        _io.Replies.Enqueue("affirm");

        await _core.RunRectifyAsync(CancellationToken.None);

        Assert.Equal(
            [
                "Rectify APU inoperative? Affirm or negative.",
                "Maintenance actioned. APU inoperative — tech log entry cleared.",
            ],
            _io.Spoken);
        var remaining = Assert.Single(_techLog.OpenDefects);
        Assert.Equal("Slow gear retraction", remaining.Title);
    }

    [Theory]
    [InlineData("negative")]
    [InlineData(null)] // timeout
    public async Task Rectify_NonAffirm_LeavesTheDefectOpen(string? confirm)
    {
        _techLog.RaiseDefect(_techLog.NewDraft("manual", "APU inoperative", MelCategory.C));
        _io.Replies.Enqueue(confirm);

        await _core.RunRectifyAsync(CancellationToken.None);

        Assert.Equal("Left open.", _io.Spoken[^1]);
        Assert.Single(_techLog.OpenDefects);
    }

    // ---- post-abnormal shutdown offer ----

    [Fact]
    public async Task Offers_Affirm_RaisesCategoryCFromAbnormal()
    {
        _io.Replies.Enqueue("affirm");

        await _core.RunAbnormalOffersAsync(
            [new("apu-fault", "APU FAULT")], SessionId, CancellationToken.None);

        Assert.Equal(
            [
                "We had the APU FAULT this flight. Shall I enter it in the tech log? Affirm or negative.",
                "Entered in the tech log, category Charlie.",
            ],
            _io.Spoken);
        Assert.Equal(ConfirmVocabulary.All, Assert.Single(_io.Listens).Grammar);

        var defect = Assert.Single(_techLog.OpenDefects);
        Assert.Equal("fromAbnormal:apu-fault", defect.Source);
        Assert.Equal(MelCategory.C, defect.Category);
        Assert.Equal(SessionId, defect.RaisedFlight);
    }

    [Theory]
    [InlineData("negative")]
    [InlineData(null)] // timeout declines
    public async Task Offers_NonAffirm_RaisesNothing(string? confirm)
    {
        _io.Replies.Enqueue(confirm);

        await _core.RunAbnormalOffersAsync(
            [new("apu-fault", "APU FAULT")], SessionId, CancellationToken.None);

        Assert.Single(_io.Spoken); // the offer only — no "entered" line
        Assert.Empty(_techLog.Defects);
    }

    [Fact]
    public async Task Offers_SkipAbnormalsAlreadyLoggedThisFlight()
    {
        _techLog.RaiseDefect(_techLog.NewDraft("fromAbnormal:apu-fault", "APU FAULT", MelCategory.C));
        _io.Replies.Enqueue("negative");

        await _core.RunAbnormalOffersAsync(
            [new("apu-fault", "APU FAULT"), new("pack-1-fault", "PACK 1 FAULT")],
            SessionId, CancellationToken.None);

        var offer = Assert.Single(_io.Spoken);
        Assert.Contains("PACK 1 FAULT", offer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Offers_SameAbnormalFromAnEarlierFlight_IsOfferedAgain()
    {
        var previous = _techLog.NewDraft("fromAbnormal:apu-fault", "APU FAULT", MelCategory.C);
        previous.RaisedFlight = "session-earlier";
        _techLog.RaiseDefect(previous);
        _io.Replies.Enqueue("negative");

        await _core.RunAbnormalOffersAsync(
            [new("apu-fault", "APU FAULT")], SessionId, CancellationToken.None);

        Assert.Single(_io.Spoken); // a carry-over from another flight does not mute the offer
    }

    // ---- static helpers ----

    [Theory]
    [InlineData("affirm", true)]
    [InlineData("Affirmative.", true)]
    [InlineData("yes", true)]
    [InlineData("yes please", true)]
    [InlineData("confirmed", true)]
    [InlineData("negative", false)]
    [InlineData("no", false)]
    [InlineData("say again", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAffirm_RequiresAnExplicitYes(string? said, bool expected)
        => Assert.Equal(expected, TechLogDialogueCore.IsAffirm(said));
}
