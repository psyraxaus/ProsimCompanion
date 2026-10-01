using NAudio.MediaFoundation;
using NAudio.Wave;

namespace ProsimCompanion.Speech.Tts;

/// <summary>
/// MP3 → WAV for providers that cannot emit WAV on every plan (ElevenLabs: PCM output is a
/// paid-tier feature, MP3 works on Free). Decodes through NAudio's Media Foundation reader —
/// Windows-only, which is fine for a Windows-only app — into a complete RIFF/WAV so the
/// playback layer's "always a WAV" contract holds. Separate from <see cref="WavRepair"/> so
/// the header helpers there stay dependency-free.
/// </summary>
public static class Mp3Wav
{
    /// <summary>Decodes a whole MP3 byte buffer to 16-bit PCM WAV at the MP3's own sample rate
    /// and channel count. Throws on undecodable input — the caller (a TTS provider) lets it
    /// surface so the router falls through.</summary>
    public static byte[] DecodeMp3ToWav(byte[] mp3)
    {
        ArgumentNullException.ThrowIfNull(mp3);
        if (mp3.Length == 0)
        {
            throw new InvalidOperationException("MP3 buffer is empty");
        }

        // Idempotent (flag-guarded inside NAudio); explicit so the decode never depends on
        // which NAudio class happened to initialise Media Foundation first.
        MediaFoundationApi.Startup();

        // MediaFoundationReader needs a SEEKABLE source: a network stream would fail at the
        // header probe, so the caller hands us the fully-buffered body and we wrap it here.
        using var source = new MemoryStream(mp3, writable: false);
        using var reader = new StreamMediaFoundationReader(source);
        using var output = new MemoryStream();
        using (var writer = new WaveFileWriter(new IgnoreDisposeStream(output), reader.WaveFormat))
        {
            reader.CopyTo(writer);
        }

        return output.ToArray();
    }

    /// <summary>WaveFileWriter disposes the stream it writes to; this shim keeps the
    /// MemoryStream alive so its bytes can be pulled out after the header is finalised.</summary>
    private sealed class IgnoreDisposeStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            // Deliberately not disposing the inner stream — flush only.
            inner.Flush();
            base.Dispose(disposing);
        }
    }
}
