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
/// against a fake engine. Two regressions guarded here: the settings hot-reload path (a
/// listening-mode flip used to wait for the next window or PTT edge — an app restart in
/// practice), and issue #61's dead latch — a swallowed start failure used to short-circuit
/// Evaluate() forever, so continuous listening stayed dead all flight until a PTT toggle.
/// </summary>
public sealed class RecognitionControllerTests : IDisposable
{
    public void Dispose() => _recognizer.Dispose();

    private sealed class FakeRecognizer : IVoiceRecognizer
    {
        private readonly object _gate = new();
        private int _failStartsRemaining;

        public bool Listening { get; private set; }

        public int StartAttempts { get; private set; }

        public bool IsListening => Listening;

        public event EventHandler<RecognizedEventArgs>? Accepted { add { } remove { } }

        public event EventHandler<RecognizedEventArgs>? Rejected { add { } remove { } }

        /// <summary>Arms the next <paramref name="count"/> StartListening calls to fail —
        /// the mic-busy-at-boot shape issue #61 is about.</summary>
        public void FailNextStarts(int count)
        {
            lock (_gate)
            {
                _failStartsRemaining = count;
            }
        }

        /// <summary>Simulates the engine dropping capture behind the controller's back.</summary>
        public void DropListening()
        {
            lock (_gate)
            {
                Listening = false;
            }
        }

        public void SetGrammar(IReadOnlyList<string> phrases)
        {
        }

        public bool StartListening()
        {
            lock (_gate)
            {
                StartAttempts++;
                if (Listening)
                {
                    return true;
                }

                if (_failStartsRemaining > 0)
                {
                    _failStartsRemaining--;
                    return false;
                }

                Listening = true;
                return true;
            }
        }

        public void StopListening()
        {
            lock (_gate)
            {
                Listening = false;
            }
        }

        public void Dispose()
        {
        }
    }

    private static readonly IReadOnlyList<TimeSpan> TestBackoff =
        [TimeSpan.FromMilliseconds(10)];

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
            monitor.Object, ptt, new SpeechStatusStore(), NullLoggerFactory.Instance,
            () => _recognizer, TestBackoff);
    }

    private static void WaitUntil(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            Thread.Sleep(10);
        }

        Assert.Fail(because);
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

    // ---- Pilot "ear off" latch (Voice Pause key) ----

    [Fact]
    public void Pause_WhileListeningContinuous_StopsTheEngine_AndResumeRestartsIt()
    {
        _options.RecognitionMode = "continuous";
        using var controller = Controller();
        controller.OpenListeningWindow(["call up the checklist"]);
        Assert.True(_recognizer.Listening);

        Assert.True(controller.SetPaused(true));
        Assert.True(controller.Paused);
        Assert.False(_recognizer.Listening);

        Assert.True(controller.SetPaused(false));
        Assert.False(controller.Paused);
        Assert.True(_recognizer.Listening);
    }

    [Fact]
    public void Pause_WinsOverAWindowOpenedLater()
    {
        _options.RecognitionMode = "continuous";
        using var controller = Controller();
        controller.SetPaused(true);

        // A dialogue opening its window (or a grammar swap) must not un-mute the pilot.
        controller.OpenListeningWindow(["call up the checklist"]);
        Assert.False(_recognizer.Listening);
        Assert.Equal(0, _recognizer.StartAttempts);
    }

    [Fact]
    public void SetPaused_SameState_ReportsNoChange()
    {
        using var controller = Controller();

        Assert.False(controller.SetPaused(false)); // already listening-allowed
        Assert.True(controller.SetPaused(true));
        Assert.False(controller.SetPaused(true)); // already paused
    }

    [Fact]
    public void Pause_PublishesTheLatchToTheStore()
    {
        var store = new SpeechStatusStore();
        var monitor = new Mock<IOptionsMonitor<SpeechOptions>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(() => _options);
        monitor.Setup(m => m.OnChange(It.IsAny<Action<SpeechOptions, string?>>())).Returns(Mock.Of<IDisposable>());
        var ptt = new PushToTalkService(monitor.Object, NullLogger<PushToTalkService>.Instance);
        using var controller = new RecognitionController(
            monitor.Object, ptt, store, NullLoggerFactory.Instance, () => _recognizer, TestBackoff);

        controller.SetPaused(true);
        Assert.True(store.Snapshot().ListeningPaused);
        Assert.False(store.Snapshot().Listening);

        controller.SetPaused(false);
        Assert.False(store.Snapshot().ListeningPaused);
    }

    [Fact]
    public void Pause_EndsARunningStartRetryEpisode()
    {
        _options.RecognitionMode = "continuous";
        _recognizer.FailNextStarts(int.MaxValue);
        using var controller = Controller();
        controller.OpenListeningWindow(["call up the checklist"]);
        Assert.False(_recognizer.Listening);

        // Pausing flips desire off — the retry loop must stop, not resurrect listening
        // behind the pilot's back once the mic frees up.
        controller.SetPaused(true);
        _recognizer.FailNextStarts(0);

        Thread.Sleep(100); // several test-backoff periods
        Assert.False(_recognizer.Listening);
    }

    // ---- Issue #61: failed starts must retry until they stick ----

    [Fact]
    public void FailedStart_RetriesWithBackoff_UntilItSticks()
    {
        _options.RecognitionMode = "continuous";
        _recognizer.FailNextStarts(2);
        using var controller = Controller();

        controller.OpenListeningWindow(["call up the checklist"]);
        Assert.False(_recognizer.Listening); // the immediate start failed (mic busy)

        // No PTT toggle, no window churn — the retry loop alone must bring it up.
        WaitUntil(() => _recognizer.Listening, "failed start never recovered via retry");
        Assert.True(_recognizer.StartAttempts >= 3);
    }

    [Fact]
    public void EngineDesync_IsRepairedByAnyReEvaluation_WithoutPttToggle()
    {
        _options.RecognitionMode = "continuous";
        using var controller = Controller();
        controller.OpenListeningWindow(["call up the checklist"]);
        Assert.True(_recognizer.Listening);

        // The engine drops capture behind the controller's back. The old latch compared
        // desired against its own intent and short-circuited forever.
        _recognizer.DropListening();
        _reload!.Invoke(_options, null); // an unchanged-settings reload is enough

        Assert.True(_recognizer.Listening);
    }

    [Fact]
    public void RetryEpisode_StopsWhenDesiredStateChanges()
    {
        _options.RecognitionMode = "continuous";
        _recognizer.FailNextStarts(int.MaxValue);
        using var controller = Controller();
        controller.OpenListeningWindow(["call up the checklist"]);
        Assert.False(_recognizer.Listening);

        // Desire flips off (the same path a PTT release or mode flip takes) — the episode
        // must end; later successes must NOT resurrect listening on their own.
        _options.RecognitionMode = "pushToTalk";
        _reload!.Invoke(_options, null);
        _recognizer.FailNextStarts(0);

        Thread.Sleep(100); // several test-backoff periods
        Assert.False(_recognizer.Listening);
    }

    [Fact]
    public void PushToTalkMode_FailedStartWhileNotDesired_NeverRetries()
    {
        // Window open in PTT mode: not listening is the DESIRED state — no retry episode,
        // so the backoff machinery can't fight the pilot's released PTT.
        using var controller = Controller();
        _recognizer.FailNextStarts(int.MaxValue);
        controller.OpenListeningWindow(["call up the checklist"]);

        Thread.Sleep(100);
        Assert.Equal(0, _recognizer.StartAttempts);
    }

    // ---- LAN engine fallback and recovery (2026-09-20: the voice box finished a macOS
    // update minutes after the app gave up, and the whole flight ran offline) ----

    private sealed class LanChain
    {
        public volatile bool Healthy;
        public FakeRecognizer? Lan;
        public FakeRecognizer? Offline;
        public int LanBuilds;
        public ConnectionStatusStore Connections { get; } = new();
        public SpeechStatusStore Speech { get; } = new();
    }

    private RecognitionController LanController(LanChain chain, TimeSpan? reprobeInterval = null)
    {
        _options.AsrBaseUrl = "http://voice.test:8000";
        _options.RecognitionMode = "continuous";
        var monitor = new Mock<IOptionsMonitor<SpeechOptions>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(() => _options);
        monitor.Setup(m => m.OnChange(It.IsAny<Action<SpeechOptions, string?>>()))
            .Returns(Mock.Of<IDisposable>());

        var ptt = new PushToTalkService(monitor.Object, NullLogger<PushToTalkService>.Instance);
        var seams = new RecognitionEngineSeams(
            HealthProbe: _ => Task.FromResult(chain.Healthy),
            LanFactory: () => { chain.LanBuilds++; return chain.Lan = new FakeRecognizer(); },
            OfflineFactory: () => chain.Offline = new FakeRecognizer(),
            ReadinessBudget: TimeSpan.FromMilliseconds(60),
            ReadinessCadence: TimeSpan.FromMilliseconds(10),
            ReprobeInterval: reprobeInterval ?? TimeSpan.FromMilliseconds(20));
        return new RecognitionController(
            monitor.Object, ptt, chain.Speech, NullLoggerFactory.Instance,
            retryBackoff: TestBackoff, connections: chain.Connections, seams: seams);
    }

    private static ConnectionState AsrState(LanChain chain)
        => chain.Connections.Snapshot().FirstOrDefault(p => p.Key == Subsystems.Asr).Value;

    [Fact]
    public void LanEngine_NotReadyWithinBudget_FallsBack_ThenSwapsBackWhenHealthReturns()
    {
        var chain = new LanChain();
        using var controller = LanController(chain);
        Assert.True(controller.OnLanEngine);
        Assert.Equal(ConnectionState.Connecting, AsrState(chain));
        controller.OpenListeningWindow(["call up the checklist"]);

        WaitUntil(() => !controller.OnLanEngine, "the LAN engine never fell back to offline");
        WaitUntil(() => chain.Offline is { Listening: true }, "the offline engine did not take over listening");
        Assert.Equal("systemSpeech", controller.EngineName);
        Assert.Equal(ConnectionState.Disconnected, AsrState(chain));
        Assert.Contains("offline engine covering", chain.Speech.Snapshot().RecognizerDetail);

        chain.Healthy = true;
        WaitUntil(() => controller.OnLanEngine, "the controller never swapped back to the LAN engine");
        WaitUntil(() => chain.Lan is { Listening: true }, "the recovered LAN engine is not listening");
        Assert.Equal(2, chain.LanBuilds);
        Assert.Equal("whisper", controller.EngineName);
        Assert.Equal(ConnectionState.Connected, AsrState(chain));
    }

    [Fact]
    public async Task ReprobeNow_AfterFallback_SwapsBackAtOnce_WhenTheBoxAnswers()
    {
        var chain = new LanChain();
        using var controller = LanController(chain, reprobeInterval: TimeSpan.FromHours(1));
        controller.OpenListeningWindow(["call up the checklist"]);
        WaitUntil(() => !controller.OnLanEngine, "the LAN engine never fell back to offline");

        var stillDown = await controller.ReprobeNowAsync(CancellationToken.None);
        Assert.Contains("still not reachable", stillDown);
        Assert.False(controller.OnLanEngine);

        chain.Healthy = true;
        var back = await controller.ReprobeNowAsync(CancellationToken.None);
        Assert.Contains("swapped from the offline engine", back);
        Assert.True(controller.OnLanEngine);
        Assert.True(chain.Lan is { Listening: true });
    }

    [Fact]
    public void LanEngine_ReadyInTime_StaysOnLan_AndPublishesConnected()
    {
        var chain = new LanChain { Healthy = true };
        using var controller = LanController(chain);

        WaitUntil(() => AsrState(chain) == ConnectionState.Connected, "the LAN engine was never published as connected");
        Assert.True(controller.OnLanEngine);
        Assert.Equal(1, chain.LanBuilds);
    }

    [Fact]
    public async Task ReprobeNow_WithoutLanConfigured_SaysSo()
    {
        using var controller = Controller();

        Assert.Contains("no LAN engine configured", await controller.ReprobeNowAsync(CancellationToken.None));
    }
}
