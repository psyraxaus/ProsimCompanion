using NAudio.Dsp;
using NAudio.Wave;

namespace ProsimCompanion.Speech.Playback;

/// <summary>
/// Colours a voice like a flight-deck interphone: a gentle 300–3000 Hz band-pass (two cascaded
/// 2nd-order biquads per channel, 12 dB/oct — not a brick wall) plus tanh soft-clip grit.
/// Constants carried from Prosim2FO (drive 1.6, makeup 1.1); deliberately not configurable.
/// </summary>
public sealed class IntercomFilterProvider : ISampleProvider
{
    private const float HighPassHz = 300f;
    private const float LowPassHz = 3000f;
    private const float Drive = 1.6f;
    private const float Makeup = 1.1f;

    private readonly ISampleProvider _source;
    private readonly BiQuadFilter[] _highPass;
    private readonly BiQuadFilter[] _lowPass;

    public IntercomFilterProvider(ISampleProvider source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
        var channels = source.WaveFormat.Channels;
        _highPass = new BiQuadFilter[channels];
        _lowPass = new BiQuadFilter[channels];
        for (var c = 0; c < channels; c++)
        {
            _highPass[c] = BiQuadFilter.HighPassFilter(source.WaveFormat.SampleRate, HighPassHz, 0.707f);
            _lowPass[c] = BiQuadFilter.LowPassFilter(source.WaveFormat.SampleRate, LowPassHz, 0.707f);
        }
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        var channels = WaveFormat.Channels;
        for (var i = 0; i < read; i++)
        {
            // i % channels is correct only because reads start on a frame boundary.
            var c = i % channels;
            var sample = _lowPass[c].Transform(_highPass[c].Transform(buffer[offset + i]));
            sample = MathF.Tanh(sample * Drive) * Makeup;
            buffer[offset + i] = Math.Clamp(sample, -1f, 1f);
        }

        return read;
    }
}
