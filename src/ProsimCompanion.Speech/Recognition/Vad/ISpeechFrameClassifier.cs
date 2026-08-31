namespace ProsimCompanion.Speech.Recognition.Vad;

/// <summary>
/// Per-frame speech/no-speech decision for the <see cref="UtteranceSegmenter"/>. The frame
/// size is fixed by Silero VAD's contract (512 samples = 32 ms at 16 kHz); the RMS
/// implementation simply adopts it so the two engines are interchangeable behind the same
/// segmentation state machine.
/// </summary>
public interface ISpeechFrameClassifier
{
    /// <summary>Samples per classification frame (32 ms at 16 kHz — Silero v5+/v6 contract).</summary>
    const int FrameSamples = 512;

    /// <summary>Speech probability in [0, 1] for one <see cref="FrameSamples"/>-sample PCM16
    /// frame. Stateful implementations carry recurrent state between consecutive calls, so
    /// frames must be fed in capture order.</summary>
    float SpeechProbability(ReadOnlySpan<short> frame);

    /// <summary>Clears any recurrent state; call at the start of every capture episode.</summary>
    void Reset();
}
