using System.Buffers.Binary;

namespace ProsimCompanion.Speech.Tts;

/// <summary>
/// Repairs RIFF/data chunk sizes in a WAV byte buffer. Streaming TTS responses (kokoro-fastapi)
/// carry placeholder sizes (0 or 0xFFFFFFFF) that NAudio's WaveFileReader rejects ("Stream
/// length must be non-negative…") even though media players tolerate them — so the real
/// lengths are written in before caching or playback. Also applied to cache HITS, healing any
/// entry cached before this fix existed (idempotent on good WAVs). Quirk carried from
/// Prosim2FO.
/// </summary>
public static class WavRepair
{
    public static void NormalizeSizes(byte[] wav)
    {
        ArgumentNullException.ThrowIfNull(wav);

        if (wav.Length < 44
            || wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F'
            || wav[8] != 'W' || wav[9] != 'A' || wav[10] != 'V' || wav[11] != 'E')
        {
            return;
        }

        var riffSize = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(4));
        var actualRiffSize = (uint)(wav.Length - 8);
        if (riffSize is 0 or 0xFFFFFFFF || riffSize > actualRiffSize)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(4), actualRiffSize);
        }

        var pos = 12;
        while (pos + 8 <= wav.Length)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(pos + 4));
            var isData = wav[pos] == 'd' && wav[pos + 1] == 'a' && wav[pos + 2] == 't' && wav[pos + 3] == 'a';
            if (isData)
            {
                var remaining = (uint)(wav.Length - pos - 8);
                if (size is 0 or 0xFFFFFFFF || size > remaining)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(pos + 4), remaining);
                }

                return;
            }

            if (size is 0 or 0xFFFFFFFF || pos + 8 + (long)size > wav.Length)
            {
                return; // Untrustworthy non-data chunk — bail rather than walk off the end.
            }

            pos += 8 + (int)size + (int)(size & 1); // Chunks are word-aligned.
        }
    }
}
