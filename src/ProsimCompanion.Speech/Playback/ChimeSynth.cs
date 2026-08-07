namespace ProsimCompanion.Speech.Playback;

/// <summary>
/// Programmatic cue chimes — no shipped audio assets (predecessor parity, exact tone specs).
/// Pure: builds a complete 16-bit PCM mono 44.1 kHz WAV in memory. Unknown ids return null so
/// a chime can never break speech.
/// </summary>
public static class ChimeSynth
{
    private const int SampleRate = 44_100;
    private const double Gain = 0.22;

    /// <summary>"cabin" = interphone ding-dong (E5 → C5); "company"/"acars" = ACARS data
    /// double-beep (C6). Anything else → null (quiet no-op).</summary>
    public static byte[]? Build(string? chimeId) => chimeId?.Trim().ToLowerInvariant() switch
    {
        "cabin" => TwoTone(660, 190, gapMs: 45, 523, 240),
        "company" or "acars" => TwoTone(1046, 85, gapMs: 70, 1046, 85),
        _ => null,
    };

    private static byte[] TwoTone(double f1, int ms1, int gapMs, double f2, int ms2)
    {
        var samples = new List<short>(SampleRate);
        AppendSine(samples, f1, ms1);
        AppendSilence(samples, gapMs);
        AppendSine(samples, f2, ms2);
        return Wrap(samples);
    }

    private static void AppendSine(List<short> samples, double freqHz, int ms)
    {
        var count = SampleRate * ms / 1000;
        for (var i = 0; i < count; i++)
        {
            var value = Gain * Math.Sin(2 * Math.PI * freqHz * i / SampleRate);
            samples.Add((short)(value * short.MaxValue));
        }
    }

    private static void AppendSilence(List<short> samples, int ms)
        => samples.AddRange(new short[SampleRate * ms / 1000]);

    private static byte[] Wrap(List<short> samples)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        var dataBytes = samples.Count * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);            // PCM
        writer.Write((short)1);            // mono
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2);      // byte rate
        writer.Write((short)2);            // block align
        writer.Write((short)16);           // bits per sample
        writer.Write("data"u8);
        writer.Write(dataBytes);
        foreach (var sample in samples)
        {
            writer.Write(sample);
        }

        writer.Flush();
        return ms.ToArray();
    }
}
