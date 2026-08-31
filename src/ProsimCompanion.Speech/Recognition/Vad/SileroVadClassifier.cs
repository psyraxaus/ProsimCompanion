using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ProsimCompanion.Speech.Recognition.Vad;

/// <summary>
/// Silero VAD v6 over ONNX Runtime (CPU), mirroring the upstream C# example
/// (<c>examples/csharp/SileroVadOnnxModel.cs</c> at the vendored tag — THIRD_PARTY.md):
/// inputs <c>input</c> [1, 64 + 512], <c>sr</c> [1], <c>state</c> [2, 1, 128]; outputs
/// <c>output</c> (speech probability) and <c>stateN</c> (recurrent state fed back on the next
/// frame). The last 64 input samples are carried over as context for the next frame — the
/// model expects that overlap, so frames must arrive in capture order. One inference per
/// 32 ms frame with a single intra-op thread costs well under a millisecond on CPU and must
/// stay negligible next to the sim. Construction throws when the model file or the native
/// runtime is unavailable; the recognizer catches that and degrades to <see cref="RmsClassifier"/>.
/// </summary>
public sealed class SileroVadClassifier : ISpeechFrameClassifier, IDisposable
{
    private const int ContextSamples = 64;
    private const int StateLength = 2 * 1 * 128;

    private static readonly int[] InputDims = [1, ContextSamples + ISpeechFrameClassifier.FrameSamples];
    private static readonly int[] StateDims = [2, 1, 128];

    private readonly InferenceSession _session;
    private readonly float[] _input = new float[ContextSamples + ISpeechFrameClassifier.FrameSamples];
    private readonly float[] _state = new float[StateLength];
    private readonly float[] _context = new float[ContextSamples];
    private readonly DenseTensor<long> _srTensor = new(new long[] { 16_000 }, [1]);

    /// <summary>The vendored model beside the executing assembly (a build content file —
    /// never downloaded).</summary>
    public static string DefaultModelPath =>
        Path.Combine(AppContext.BaseDirectory, "Recognition", "Vad", "silero_vad.onnx");

    public SileroVadClassifier(string modelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);

        using var options = new SessionOptions
        {
            // One inference thread: a 32 ms frame is tiny and the sim owns the CPU.
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
        };
        _session = new InferenceSession(modelPath, options);
    }

    public float SpeechProbability(ReadOnlySpan<short> frame)
    {
        Array.Copy(_context, _input, ContextSamples);
        var count = Math.Min(frame.Length, ISpeechFrameClassifier.FrameSamples);
        for (var i = 0; i < count; i++)
        {
            _input[ContextSamples + i] = frame[i] / 32768f;
        }

        for (var i = count; i < ISpeechFrameClassifier.FrameSamples; i++)
        {
            _input[ContextSamples + i] = 0f;
        }

        using var results = _session.Run(
        [
            NamedOnnxValue.CreateFromTensor("input", new DenseTensor<float>(_input, InputDims)),
            NamedOnnxValue.CreateFromTensor("state", new DenseTensor<float>(_state, StateDims)),
            NamedOnnxValue.CreateFromTensor("sr", _srTensor),
        ]);

        float probability = 0f;
        foreach (var value in results)
        {
            if (value.Name == "output")
            {
                probability = value.AsTensor<float>().GetValue(0);
            }
            else if (value.Name == "stateN")
            {
                var stateN = value.AsTensor<float>();
                var flat = 0;
                foreach (var f in stateN)
                {
                    _state[flat++] = f;
                }
            }
        }

        Array.Copy(_input, _input.Length - ContextSamples, _context, 0, ContextSamples);
        return probability;
    }

    /// <summary>Zeroes the recurrent state and the context carry-over — upstream's
    /// <c>ResetStates()</c>; call at the start of every capture episode.</summary>
    public void Reset()
    {
        Array.Clear(_state);
        Array.Clear(_context);
    }

    public void Dispose() => _session.Dispose();
}
