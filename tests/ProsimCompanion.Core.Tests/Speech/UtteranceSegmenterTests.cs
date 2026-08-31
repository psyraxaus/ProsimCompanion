using ProsimCompanion.Speech.Recognition.Vad;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>
/// Drives <see cref="UtteranceSegmenter"/> with a scripted classifier, one probability per
/// 512-sample frame (32 ms at 16 kHz, 1024 bytes). Small ms settings keep the arithmetic
/// readable: every duration below is a multiple of the 32 ms frame.
/// </summary>
public sealed class UtteranceSegmenterTests
{
    private const int FrameBytes = ISpeechFrameClassifier.FrameSamples * 2;

    private sealed class ScriptedClassifier : ISpeechFrameClassifier
    {
        private readonly Queue<float> _script = new();

        public int Frames { get; private set; }

        public int Resets { get; private set; }

        public List<int> FrameLengths { get; } = [];

        public void Enqueue(float probability, int frames = 1)
        {
            for (var i = 0; i < frames; i++)
            {
                _script.Enqueue(probability);
            }
        }

        public float SpeechProbability(ReadOnlySpan<short> frame)
        {
            Frames++;
            FrameLengths.Add(frame.Length);
            return _script.Count > 0 ? _script.Dequeue() : 0f;
        }

        public void Reset() => Resets++;
    }

    private static SegmenterSettings SmallSettings(
        int endSilenceMs = 64, int minSpeechMs = 64, int preRollMs = 64, int maxUtteranceMs = 3200)
        => new(Threshold: 0.5f, endSilenceMs, minSpeechMs, preRollMs, maxUtteranceMs);

    /// <summary>One 512-sample frame of PCM16 filled with a marker value.</summary>
    private static byte[] Frame(short marker)
    {
        var samples = new short[ISpeechFrameClassifier.FrameSamples];
        Array.Fill(samples, marker);
        var bytes = new byte[FrameBytes];
        Buffer.BlockCopy(samples, 0, bytes, 0, FrameBytes);
        return bytes;
    }

    private static (UtteranceSegmenter Segmenter, ScriptedClassifier Classifier,
        List<UtteranceReadyEventArgs> Ready, List<UtteranceDroppedEventArgs> Dropped)
        Build(SegmenterSettings settings)
    {
        var classifier = new ScriptedClassifier();
        var segmenter = new UtteranceSegmenter(classifier, settings);
        segmenter.Reset();
        var ready = new List<UtteranceReadyEventArgs>();
        var dropped = new List<UtteranceDroppedEventArgs>();
        segmenter.UtteranceReady += (_, e) => ready.Add(e);
        segmenter.UtteranceDropped += (_, e) => dropped.Add(e);
        return (segmenter, classifier, ready, dropped);
    }

    [Fact]
    public void EndSilence_ClosesUtterance_AndPreRollIsRetained()
    {
        var (segmenter, classifier, ready, _) = Build(SmallSettings());

        // 5 silent frames (marker 1) — trimmed to the 2-frame (64 ms) pre-roll.
        classifier.Enqueue(0f, frames: 5);
        for (var i = 0; i < 5; i++)
        {
            segmenter.Push(Frame(1), FrameBytes);
        }

        // 3 speech frames (marker 2), then 2 silent frames (marker 3) reach end-silence.
        classifier.Enqueue(0.9f, frames: 3);
        classifier.Enqueue(0f, frames: 2);
        for (var i = 0; i < 3; i++)
        {
            segmenter.Push(Frame(2), FrameBytes);
        }

        for (var i = 0; i < 2; i++)
        {
            segmenter.Push(Frame(3), FrameBytes);
        }

        var e = Assert.Single(ready);
        Assert.Equal(UtteranceEndReason.Silence, e.Stats.EndReason);
        Assert.Equal(7 * FrameBytes, e.Pcm.Length); // 2 pre-roll + 3 speech + 2 trailing
        Assert.Equal(7 * 32, e.Stats.DurationMs);
        Assert.Equal(0.9f, e.Stats.PeakProbability);

        // Pre-roll audio is the tail of the pre-speech ambience, not speech frames.
        Assert.Equal(1, BitConverter.ToInt16(e.Pcm, 0));
        Assert.Equal(2, BitConverter.ToInt16(e.Pcm, 2 * FrameBytes));
    }

    [Fact]
    public void SubMinSpeechBlip_IsDropped_NotEmitted()
    {
        // Min speech 128 ms = 4 frames; the blip closes at 3 frames total.
        var (segmenter, classifier, ready, dropped) = Build(SmallSettings(minSpeechMs: 128));

        classifier.Enqueue(0.9f);
        classifier.Enqueue(0f, frames: 2);
        for (var i = 0; i < 3; i++)
        {
            segmenter.Push(Frame(2), FrameBytes);
        }

        Assert.Empty(ready);
        var drop = Assert.Single(dropped);
        Assert.Equal(3 * 32, drop.DurationMs);

        // The segmenter restarted cleanly: a full utterance afterwards is emitted.
        classifier.Enqueue(0.9f, frames: 4);
        classifier.Enqueue(0f, frames: 2);
        for (var i = 0; i < 6; i++)
        {
            segmenter.Push(Frame(2), FrameBytes);
        }

        Assert.Single(ready);
    }

    [Fact]
    public void HysteresisBand_HoldsThroughADip_WithoutCountingSilence()
    {
        // End silence 64 ms = 2 frames. The dip (0.40 ≥ 0.35 = threshold − 0.15) lasts
        // 4 frames — long enough that counted silence would have closed the utterance twice.
        var (segmenter, classifier, ready, _) = Build(SmallSettings());

        classifier.Enqueue(0.9f, frames: 2);
        classifier.Enqueue(0.40f, frames: 4);
        classifier.Enqueue(0.9f, frames: 2);
        classifier.Enqueue(0f, frames: 2);
        for (var i = 0; i < 10; i++)
        {
            segmenter.Push(Frame(2), FrameBytes);
        }

        var e = Assert.Single(ready);
        Assert.Equal(UtteranceEndReason.Silence, e.Stats.EndReason);
        Assert.Equal(10 * FrameBytes, e.Pcm.Length); // the dip audio is inside the utterance
    }

    [Fact]
    public void HardCap_Terminates_AndTheNextUtteranceStartsCleanly()
    {
        // Cap at 320 ms = 10 frames of continuous speech.
        var (segmenter, classifier, ready, _) = Build(SmallSettings(maxUtteranceMs: 320));

        classifier.Enqueue(0.9f, frames: 12);
        classifier.Enqueue(0f, frames: 2);
        for (var i = 0; i < 14; i++)
        {
            segmenter.Push(Frame(2), FrameBytes);
        }

        Assert.Equal(2, ready.Count);
        Assert.Equal(UtteranceEndReason.HardCap, ready[0].Stats.EndReason);
        Assert.Equal(10 * FrameBytes, ready[0].Pcm.Length);
        Assert.Equal(UtteranceEndReason.Silence, ready[1].Stats.EndReason);
        Assert.Equal(4 * FrameBytes, ready[1].Pcm.Length); // 2 speech + 2 trailing silence
    }

    [Fact]
    public void Flush_EmitsBufferedSpeech_AsPttRelease()
    {
        var (segmenter, classifier, ready, _) = Build(SmallSettings());

        classifier.Enqueue(0.9f, frames: 3);
        for (var i = 0; i < 3; i++)
        {
            segmenter.Push(Frame(2), FrameBytes);
        }

        // Half a frame of remainder is included in the flushed utterance.
        segmenter.Push(Frame(2), FrameBytes / 2);
        segmenter.Flush();

        var e = Assert.Single(ready);
        Assert.Equal(UtteranceEndReason.PttRelease, e.Stats.EndReason);
        Assert.Equal((3 * FrameBytes) + (FrameBytes / 2), e.Pcm.Length);
    }

    [Fact]
    public void Flush_WithoutSpeech_EmitsNothing()
    {
        var (segmenter, classifier, ready, dropped) = Build(SmallSettings());

        classifier.Enqueue(0f, frames: 3);
        for (var i = 0; i < 3; i++)
        {
            segmenter.Push(Frame(1), FrameBytes);
        }

        segmenter.Flush();

        Assert.Empty(ready);
        Assert.Empty(dropped);
    }

    [Fact]
    public void WaveInSizedBuffers_AreReframedTo512Samples_WithRemainderCarry()
    {
        // WaveInEvent delivers 800 samples per 50 ms callback: 10 × 800 = 8000 samples
        // must yield exactly floor(8000 / 512) = 15 classified frames.
        var (segmenter, classifier, _, _) = Build(SmallSettings());

        var buffer = new byte[1600];
        for (var i = 0; i < 10; i++)
        {
            segmenter.Push(buffer, buffer.Length);
        }

        Assert.Equal(15, classifier.Frames);
        Assert.All(classifier.FrameLengths, length => Assert.Equal(ISpeechFrameClassifier.FrameSamples, length));
    }

    [Fact]
    public void Reset_ClearsClassifierState_AndBufferedAudio()
    {
        var (segmenter, classifier, ready, _) = Build(SmallSettings());

        classifier.Enqueue(0.9f, frames: 2);
        segmenter.Push(Frame(2), FrameBytes);
        segmenter.Push(Frame(2), FrameBytes);

        segmenter.Reset();
        Assert.Equal(2, classifier.Resets); // once in Build, once here

        // Post-reset, the previous speech is gone: silence alone emits nothing.
        classifier.Enqueue(0f, frames: 3);
        for (var i = 0; i < 3; i++)
        {
            segmenter.Push(Frame(1), FrameBytes);
        }

        segmenter.Flush();
        Assert.Empty(ready);
    }
}
