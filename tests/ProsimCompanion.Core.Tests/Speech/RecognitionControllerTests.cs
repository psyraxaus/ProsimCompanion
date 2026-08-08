using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Recognition;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>
/// The listen decision (windowOpen &amp;&amp; !atcMuted &amp;&amp; (continuous || pttPressed))
/// against a fake engine — the regression here is the settings hot-reload path: a
/// listening-mode flip used to wait for the next window or PTT edge, which in practice
/// meant an app restart.
/// </summary>
public sealed class RecognitionControllerTests : IDisposable
{
    public void Dispose() => _recognizer.Dispose();

    private sealed class FakeRecognizer : IVoiceRecognizer
    {
        public bool Listening { get; private set; }

        public event EventHandler<RecognizedEventArgs>? Accepted { add { } remove { } }

        public event EventHandler<RecognizedEventArgs>? Rejected { add { } remove { } }

        public void SetGrammar(IReadOnlyList<string> phrases)
        {
        }

        public void StartListening() => Listening = true;

        public void StopListening() => Listening = false;

        public void Dispose()
        {
        }
    }

    private readonly SpeechOptions _options = new();
    private readonly FakeRecognizer _recognizer = new();
    private Action<SpeechOptions, string?>? _reload;

    private RecognitionController Controller()
    {
        var monitor = new Mock<IOptionsMonitor<SpeechOptions>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(() => _options);
        monitor.Setup(m => m.OnChange(It.IsAny<Action<SpeechOptions, string?>>()))
            .Callback<Action<SpeechOptions, string?>>(listener => _reload = listener)
            .Returns(Mock.Of<IDisposable>());

        var ptt = new PushToTalkService(monitor.Object, NullLogger<PushToTalkService>.Instance);
        return new RecognitionController(
            monitor.Object, ptt, new SpeechStatusStore(), NullLoggerFactory.Instance, () => _recognizer);
    }

    [Fact]
    public void PushToTalkMode_OpenWindowAlone_DoesNotListen()
    {
        using var controller = Controller();
        controller.OpenListeningWindow(["call up the checklist"]);

        Assert.False(_recognizer.Listening);
    }

    [Fact]
    public void ContinuousMode_OpenWindow_Listens()
    {
        _options.RecognitionMode = "continuous";
        using var controller = Controller();
        controller.OpenListeningWindow(["call up the checklist"]);

        Assert.True(_recognizer.Listening);
    }

    [Fact]
    public void SwitchToContinuous_WhileWindowOpen_StartsListeningWithoutRestart()
    {
        using var controller = Controller();
        controller.OpenListeningWindow(["call up the checklist"]);
        Assert.False(_recognizer.Listening);

        _options.RecognitionMode = "continuous";
        _reload!.Invoke(_options, null);

        Assert.True(_recognizer.Listening);
    }

    [Fact]
    public void SwitchBackToPushToTalk_WhileListening_StopsWithoutRestart()
    {
        _options.RecognitionMode = "continuous";
        using var controller = Controller();
        controller.OpenListeningWindow(["call up the checklist"]);
        Assert.True(_recognizer.Listening);

        _options.RecognitionMode = "pushToTalk";
        _reload!.Invoke(_options, null);

        Assert.False(_recognizer.Listening);
    }

    [Fact]
    public void CloseWindow_InContinuousMode_StopsListening()
    {
        _options.RecognitionMode = "continuous";
        using var controller = Controller();
        controller.OpenListeningWindow(["call up the checklist"]);
        controller.CloseListeningWindow();

        Assert.False(_recognizer.Listening);
    }
}
