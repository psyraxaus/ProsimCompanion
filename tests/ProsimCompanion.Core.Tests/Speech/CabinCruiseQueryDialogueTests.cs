using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Cabin;
using ProsimCompanion.Speech.Recognition;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>The purser's cruise query dialogue (2026-10-09): mic borrowed, chime step first,
/// the question in the purser voice, the reply window, the matching acknowledgement — and a
/// busy mic that rings nothing and hands the latch back.</summary>
public sealed class CabinCruiseQueryDialogueTests
{
    private readonly FakeArbiter _arbiter = new();
    private readonly FakePhaseSource _phases = new();
    private readonly Mock<IMicOwnership> _mic = new();
    private readonly CabinOptions _options = new() { CruiseQuery = true, CruiseQueryWindowSeconds = 20 };
    private readonly List<IReadOnlyList<string>> _grammars = [];
    private int _rings;

    public CabinCruiseQueryDialogueTests()
    {
        _phases.SetPhase(FlightPhase.Cruise);
        _mic.Setup(m => m.Borrow(It.IsAny<string>())).Returns(Mock.Of<IDisposable>());
    }

    private void Hears(string? reply)
        => _mic.Setup(m => m.ListenAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<string>, TimeSpan, CancellationToken>((g, _, _) => _grammars.Add(g))
            .ReturnsAsync(reply);

    private CabinCruiseQueryDialogue Dialogue(bool? freeForm = true)
    {
        var monitor = new Mock<IOptionsMonitor<CabinOptions>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(() => _options);
        IRecognitionWindow? window = null;
        if (freeForm is { } capable)
        {
            var w = new Mock<IRecognitionWindow>();
            w.SetupGet(x => x.FreeFormCapable).Returns(capable);
            window = w.Object;
        }

        return new CabinCruiseQueryDialogue(
            _mic.Object, _arbiter, monitor.Object, _phases, SpeechTestSupport.TempEventLog(),
            NullLogger<CabinCruiseQueryDialogue>.Instance, window);
    }

    private Task Ring(CancellationToken _)
    {
        _rings++;
        return Task.CompletedTask;
    }

    [Fact]
    public async Task EtaReply_GetsTheTimeAcknowledgement_InThePurserVoice()
    {
        Hears("about forty minutes, should be smooth");
        var result = await Dialogue().RunAsync(Ring, CancellationToken.None);

        Assert.Equal(CruiseQueryOutcome.Replied, result.Outcome);
        Assert.Equal(CruiseReplyKind.Both, result.Kind);
        Assert.Equal(1, _rings);
        Assert.Equal([_options.CruiseQueryText, _options.CruiseQueryEtaAckText], _arbiter.Requests.Select(r => r.Text));
        Assert.All(_arbiter.Requests, r => Assert.Equal(SpeechRole.Purser, r.Role));
        Assert.All(_arbiter.Requests, r => Assert.Equal(CabinCruiseQueryDialogue.Tag, r.Tag));
        // Free-form window with the LAN transcriber: an empty grammar, the raw sentence.
        Assert.Empty(Assert.Single(_grammars));
    }

    [Fact]
    public async Task Silence_GetsTheCheckBackLaterLine()
    {
        Hears(null);
        var result = await Dialogue().RunAsync(Ring, CancellationToken.None);

        Assert.Equal(CruiseQueryOutcome.NoReply, result.Outcome);
        Assert.Equal(_options.CruiseQueryNoReplyText, _arbiter.Requests[^1].Text);
    }

    [Fact]
    public async Task RideReply_GetsTheRideAcknowledgement()
    {
        Hears("expect some light chop over the mountains");
        var result = await Dialogue().RunAsync(Ring, CancellationToken.None);

        Assert.Equal(CruiseReplyKind.Ride, result.Kind);
        Assert.Equal(_options.CruiseQueryRideAckText, _arbiter.Requests[^1].Text);
    }

    [Fact]
    public async Task OfflineEngine_ListensWithTheClosedGrammar()
    {
        Hears("on time");
        await Dialogue(freeForm: false).RunAsync(Ring, CancellationToken.None);

        Assert.Equal(CabinCruiseQueryCore.OfflineGrammar, Assert.Single(_grammars));
    }

    [Fact]
    public async Task BusyMic_RingsNothing_SaysNothing()
    {
        _mic.Setup(m => m.Borrow(It.IsAny<string>())).Throws(new InvalidOperationException("borrowed"));
        var result = await Dialogue().RunAsync(Ring, CancellationToken.None);

        Assert.Equal(CruiseQueryOutcome.MicBusy, result.Outcome);
        Assert.Equal(0, _rings);
        Assert.Empty(_arbiter.Requests);
    }

    [Fact]
    public async Task TheQuestionIsOnlyValidInTheCruise()
    {
        Hears("on time");
        await Dialogue().RunAsync(Ring, CancellationToken.None);

        var question = _arbiter.Requests[0];
        Assert.True(question.IsStillValid!());
        _phases.SetPhase(FlightPhase.Descent);
        Assert.False(question.IsStillValid!());
    }
}
