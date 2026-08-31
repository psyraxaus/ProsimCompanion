using ProsimCompanion.Speech.Recognition.Vad;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

public sealed class SileroVadSmokeTests
{
    /// <summary>Loads the vendored model and checks a second of digital silence scores under
    /// 0.1 on every frame. A native-runtime load failure on the test host is a skip, not a
    /// failure (xunit 2.x has no runtime skip, so "skip" is an early return); a MISSING MODEL
    /// FILE still fails — that is a packaging bug this test exists to catch.</summary>
    [Fact]
    public void SileroModel_Loads_AndScoresSilenceBelowPointOne()
    {
        SileroVadClassifier classifier;
        try
        {
            classifier = new SileroVadClassifier(SileroVadClassifier.DefaultModelPath);
        }
        catch (Exception ex) when (ex is DllNotFoundException
            or EntryPointNotFoundException
            or TypeInitializationException
            or PlatformNotSupportedException)
        {
            return; // ONNX Runtime native library unavailable on this host — skip.
        }

        using (classifier)
        {
            classifier.Reset();
            var silence = new short[ISpeechFrameClassifier.FrameSamples];
            var peak = 0f;
            for (var i = 0; i < 31; i++) // 31 × 32 ms ≈ 1 s
            {
                peak = Math.Max(peak, classifier.SpeechProbability(silence));
            }

            Assert.InRange(peak, 0f, 0.1f);
        }
    }
}
