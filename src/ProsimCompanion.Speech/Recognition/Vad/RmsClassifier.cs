namespace ProsimCompanion.Speech.Recognition.Vad;

/// <summary>
/// The legacy energy gate as a frame classifier: RMS above 500/32767 is "speech" (1.0),
/// anything else silence (0.0) — the constants proven in Prosim2FO. Kept as the fallback
/// engine when Silero VAD cannot initialise, and selectable via <c>speech.vadEngine</c>.
/// Binary output means the segmenter's hysteresis band never engages, which reproduces the
/// original behaviour exactly.
/// </summary>
public sealed class RmsClassifier : ISpeechFrameClassifier
{
    private const double SpeechRmsThreshold = 500.0;

    public float SpeechProbability(ReadOnlySpan<short> frame)
    {
        if (frame.IsEmpty)
        {
            return 0f;
        }

        double sum = 0;
        for (var i = 0; i < frame.Length; i++)
        {
            double sample = frame[i];
            sum += sample * sample;
        }

        return Math.Sqrt(sum / frame.Length) > SpeechRmsThreshold ? 1f : 0f;
    }

    public void Reset()
    {
        // Stateless.
    }
}
