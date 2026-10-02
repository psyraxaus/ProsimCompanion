using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Persona;
using ProsimCompanion.Speech.Playback;
using ProsimCompanion.Speech.Tts;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>
/// Issue #147: a streamed item through the real arbiter core and shell — one slot for all
/// its sentences, a High callout between them (D3), a Critical ending it for good (D2), and
/// the TTS cache bypass for the model's sentences.
/// </summary>
public sealed class SpeechArbiterStreamTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    /// <summary>Hands the text back as the "audio" and notes whether the cache was bypassed.</summary>
    private sealed class EchoProvider : ITtsProvider
    {
        public string Name => "echo";
        public bool IsConfigured => true;
        public bool IsNetworkProvider => false;
        public List<(string Text, bool CacheBypassed)> Calls { get; } = [];

        public Task<TtsAudio> SynthesizeAsync(string text, CancellationToken cancellationToken, string? voiceOverride = null)
        {
            lock (Calls)
            {
                Calls.Add((text, TtsDiskCache.IsBypassed));
            }

            return Task.FromResult(new TtsAudio(Encoding.UTF8.GetBytes(text), Name));
        }
    }

    /// <summary>Plays each clip for a fixed time and records what was played, in order.</summary>
    private sealed class RecordingPlayback : ISpeechPlayback
    {
        public TimeSpan ClipDuration { get; set; } = TimeSpan.FromMilliseconds(80);
        public List<string> Played { get; } = [];
        public List<string> Cut { get; } = [];

        public async Task PlayAsync(byte[] wavBytes, CancellationToken cancellationToken, bool? overrideIntercomFilter = null)
        {
            var text = Encoding.UTF8.GetString(wavBytes);
            try
            {
                await Task.Delay(ClipDuration, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                lock (Played)
                {
                    Cut.Add(text);
                }

                throw;
            }

            lock (Played)
            {
                Played.Add(text);
            }
        }

        public Task PlayChimeAsync(string chimeId, CancellationToken cancellationToken) => Task.CompletedTask;

        public string[] Snapshot()
        {
            lock (Played)
            {
                return [.. Played];
            }
        }
    }

    private readonly EchoProvider _provider = new();
    private readonly RecordingPlayback _playback = new();
    private readonly FakePhaseSource _phases = new();
    private readonly List<SpeechArbiterEvent> _events = [];
    private readonly SpeechArbiterService _arbiter;

    public SpeechArbiterStreamTests()
    {
        var speech = SpeechTestSupport.SpeechMonitor(new SpeechOptions());
        var voices = new Moq.Mock<Microsoft.Extensions.Options.IOptionsMonitor<VoicesOptions>>();
        voices.SetupGet(m => m.CurrentValue).Returns(new VoicesOptions());
        var store = new SpeechStatusStore();
        _arbiter = new SpeechArbiterService(
            speech, voices.Object,
            new TtsRouter([_provider], speech, store, NullLogger<TtsRouter>.Instance),
            _playback, _phases, store, SpeechTestSupport.TempEventLog(),
            NullLogger<SpeechArbiterService>.Instance, new QuietState());
        _arbiter.Observed += e =>
        {
            lock (_events)
            {
                _events.Add(e);
            }
        };
    }

    public void Dispose() => _arbiter.Dispose();

    private static SpeechRequest StreamRequest(StreamedUtterance stream, SpeechPriority priority = SpeechPriority.Normal, Func<bool>? valid = null)
        => new("Streamed briefing", priority, IsStillValid: valid, Tag: "briefing.departure", Stream: stream);

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTimeOffset.UtcNow + Wait;
        while (!condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "timed out waiting for " + what);
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Segments_AreSpokenInOrder_InOneSlot_AndANormalRequestWaitsForTheWholeStream()
    {
        using var stream = new StreamedUtterance();
        stream.TryWrite(new SpeechSegment("Sentence one.", Cacheable: false));
        var speaking = _arbiter.EnqueueAsync(StreamRequest(stream));

        await WaitUntil(() => _playback.Snapshot().Length >= 1, "the first segment");
        // Enqueued mid-stream at the same priority: must not get between the sentences.
        var other = _arbiter.EnqueueAsync(new SpeechRequest("Another Normal item.", SpeechPriority.Normal));
        stream.TryWrite(new SpeechSegment("Sentence two.", Cacheable: false));
        stream.TryWrite(new SpeechSegment("Template line.", Cacheable: true));
        stream.Complete();

        Assert.Equal(SpeechOutcome.Spoken, await speaking.WaitAsync(Wait));
        Assert.Equal(SpeechOutcome.Spoken, await other.WaitAsync(Wait));
        Assert.Equal(["Sentence one.", "Sentence two.", "Template line.", "Another Normal item."], _playback.Snapshot());
        Assert.Equal(["Sentence one.", "Sentence two.", "Template line."], stream.Spoken);
        Assert.True(stream.FirstAudio.IsCompletedSuccessfully);
        Assert.False(stream.Aborted.IsCancellationRequested);

        // The model's sentences never touch the TTS disk cache; the template line does.
        Assert.Equal(
            [("Sentence one.", true), ("Sentence two.", true), ("Template line.", false), ("Another Normal item.", false)],
            _provider.Calls);

        lock (_events)
        {
            Assert.Equal(3, _events.Count(e => e.Kind == SpeechEventKind.Segment));
            var spoken = _events.Single(e => e.Kind == SpeechEventKind.Spoken && e.Tag == "briefing.departure");
            Assert.Equal("Streamed briefing", spoken.Text);
            Assert.Contains("3 segment(s)", spoken.Reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AHighCallout_IsSpokenBetweenTwoSegments_AndTheStreamGoesOn()
    {
        using var stream = new StreamedUtterance();
        stream.TryWrite(new SpeechSegment("Sentence one.", Cacheable: false));
        var speaking = _arbiter.EnqueueAsync(StreamRequest(stream));

        await WaitUntil(() => _playback.Snapshot().Length >= 1, "the first segment");
        var callout = _arbiter.EnqueueAsync(new SpeechRequest("Positive climb.", SpeechPriority.High, Tag: "callout"));
        await WaitUntil(() => _playback.Snapshot().Length >= 2, "the callout");
        stream.TryWrite(new SpeechSegment("Sentence two.", Cacheable: false));
        stream.Complete();

        Assert.Equal(SpeechOutcome.Spoken, await callout.WaitAsync(Wait));
        Assert.Equal(SpeechOutcome.Spoken, await speaking.WaitAsync(Wait));
        Assert.Equal(["Sentence one.", "Positive climb.", "Sentence two."], _playback.Snapshot());
        Assert.False(stream.Aborted.IsCancellationRequested);
    }

    [Fact]
    public async Task ACritical_CutsTheStream_WhichEndsForGood_AndIsNeverRestarted()
    {
        _playback.ClipDuration = TimeSpan.FromMilliseconds(400);
        using var stream = new StreamedUtterance();
        stream.TryWrite(new SpeechSegment("A long first sentence.", Cacheable: false));
        stream.TryWrite(new SpeechSegment("Sentence two.", Cacheable: false));
        var speaking = _arbiter.EnqueueAsync(StreamRequest(stream));

        await WaitUntil(() => _provider.Calls.Count >= 1, "synthesis of the first segment");
        await Task.Delay(50);
        var critical = _arbiter.EnqueueAsync(new SpeechRequest("Minimums.", SpeechPriority.Critical, Tag: "minimums"));

        Assert.Equal(SpeechOutcome.Superseded, await speaking.WaitAsync(Wait));
        Assert.Equal(SpeechOutcome.Spoken, await critical.WaitAsync(Wait));
        Assert.True(stream.Aborted.IsCancellationRequested);
        Assert.Contains("A long first sentence.", _playback.Cut);
        Assert.Equal(["Minimums."], _playback.Snapshot());
        // Still queued when the cut came, and never spoken: a cut stream does not resume.
        stream.TryWrite(new SpeechSegment("Sentence three.", Cacheable: false));
        stream.Complete();
        await Task.Delay(200);
        Assert.Equal(["Minimums."], _playback.Snapshot());
        lock (_events)
        {
            Assert.DoesNotContain(_events, e => e.Kind == SpeechEventKind.Requeued);
        }
    }

    [Fact]
    public async Task ValidityGoingFalseBetweenSegments_DropsTheRest_AndTellsTheProducer()
    {
        var valid = true;
        using var stream = new StreamedUtterance();
        stream.TryWrite(new SpeechSegment("Sentence one.", Cacheable: false));
        var speaking = _arbiter.EnqueueAsync(StreamRequest(stream, valid: () => valid));

        await WaitUntil(() => _playback.Snapshot().Length >= 1, "the first segment");
        valid = false;
        stream.TryWrite(new SpeechSegment("Sentence two.", Cacheable: false));

        Assert.Equal(SpeechOutcome.Dropped, await speaking.WaitAsync(Wait));
        Assert.True(stream.Aborted.IsCancellationRequested);
        Assert.Equal(["Sentence one."], _playback.Snapshot());
    }

    [Fact]
    public async Task AStreamDroppedAtDequeue_TellsItsProducer()
    {
        using var cts = new CancellationTokenSource();
        using var stream = new StreamedUtterance();
        stream.TryWrite(new SpeechSegment("Never spoken.", Cacheable: false));
        cts.Cancel();

        var outcome = await _arbiter.EnqueueAsync(StreamRequest(stream), cts.Token).WaitAsync(Wait);

        Assert.Equal(SpeechOutcome.Dropped, outcome);
        Assert.True(stream.Aborted.IsCancellationRequested);
        Assert.Empty(_playback.Snapshot());
    }

    // ---- core rules ----

    [Fact]
    public void Core_APreemptedStreamedNormal_IsSuperseded_NotRestarted()
    {
        var core = new SpeechArbiterCore([]);
        var now = DateTimeOffset.UtcNow;
        using var stream = new StreamedUtterance();
        var streamed = core.Enqueue(StreamRequest(stream), now, CancellationToken.None, out _);
        var plain = core.Enqueue(new SpeechRequest("Plain.", SpeechPriority.Normal), now, CancellationToken.None, out _);

        Assert.Same(streamed, core.TakeNext(SpeechContext.Unknown, now).Next);
        core.BeginRender(streamed);
        core.Enqueue(new SpeechRequest("Go around.", SpeechPriority.Critical), now, CancellationToken.None, out var preempt);
        Assert.True(preempt);
        Assert.Equal(CancelDisposition.Superseded, core.HandleCancelled(streamed));

        // The same situation with an ordinary Normal item restarts it (unchanged behaviour).
        Assert.Equal(SpeechPriority.Critical, core.TakeNext(SpeechContext.Unknown, now).Next!.Request.Priority);
        Assert.Same(plain, core.TakeNext(SpeechContext.Unknown, now).Next);
        core.BeginRender(plain);
        core.Enqueue(new SpeechRequest("Minimums.", SpeechPriority.Critical), now, CancellationToken.None, out _);
        Assert.Equal(CancelDisposition.Restarted, core.HandleCancelled(plain));
    }

    [Fact]
    public void Core_TakeNextWithAFloor_LeavesTheLowerBandsQueued()
    {
        var core = new SpeechArbiterCore([]);
        var now = DateTimeOffset.UtcNow;
        core.Enqueue(new SpeechRequest("low", SpeechPriority.Low), now, CancellationToken.None, out _);
        core.Enqueue(new SpeechRequest("normal", SpeechPriority.Normal), now, CancellationToken.None, out _);
        core.Enqueue(new SpeechRequest("high", SpeechPriority.High), now, CancellationToken.None, out _);

        Assert.Equal("high", core.TakeNext(SpeechContext.Unknown, now, SpeechPriority.High).Next?.Request.Text);
        Assert.Null(core.TakeNext(SpeechContext.Unknown, now, SpeechPriority.High).Next);
        Assert.Equal(2, core.QueueDepth);
        Assert.Equal("normal", core.TakeNext(SpeechContext.Unknown, now).Next?.Request.Text);
    }

    [Fact]
    public async Task TtsDiskCache_Bypass_IsScopedToTheCallingFlow()
    {
        var cache = new TtsDiskCache(NullLogger<TtsDiskCache>.Instance);
        var root = Directory.CreateTempSubdirectory("pc-tts-bypass-").FullName;
        try
        {
            Assert.False(TtsDiskCache.IsBypassed);
            using (TtsDiskCache.Bypass())
            {
                Assert.True(TtsDiskCache.IsBypassed);
                await cache.PutAsync(root, "p", "v", "one-off text", [1, 2, 3], maxMbPerVoice: 0);
                Assert.Null(await cache.GetAsync(root, "p", "v", "one-off text"));
            }

            Assert.False(TtsDiskCache.IsBypassed);
            Assert.Null(await cache.GetAsync(root, "p", "v", "one-off text"));     // was never written
            await cache.PutAsync(root, "p", "v", "kept text", [4, 5], maxMbPerVoice: 0);
            Assert.Equal([4, 5], await cache.GetAsync(root, "p", "v", "kept text"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
