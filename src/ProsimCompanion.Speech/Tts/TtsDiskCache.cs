using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ProsimCompanion.Speech.Tts;

/// <summary>
/// Per-provider, per-voice WAV disk cache: <c>{root}/{provider}/{voice}/{SHA256(text)}.wav</c>.
/// The provider/voice namespacing matters — it means a large local-voice cache can never purge
/// another provider's or voice's audio, and text alone is a sufficient key within a namespace
/// (output is always WAV). Every failure is swallowed: the cache accelerates, it never breaks
/// synthesis.
/// </summary>
public sealed class TtsDiskCache
{
    private readonly ILogger<TtsDiskCache> _logger;

    public TtsDiskCache(ILogger<TtsDiskCache> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>Returns the cached WAV (sizes healed) or null on a miss.</summary>
    public async Task<byte[]?> GetAsync(string root, string provider, string voice, string text)
    {
        try
        {
            var path = PathFor(root, provider, voice, text);
            if (!File.Exists(path))
            {
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            WavRepair.NormalizeSizes(bytes); // Heal entries cached before the header fix.
            return bytes;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TTS cache read failed ({Provider}/{Voice})", provider, voice);
            return null;
        }
    }

    /// <summary>Stores a WAV, then trims the voice folder oldest-first when it exceeds
    /// <paramref name="maxMbPerVoice"/> (0 = uncapped).</summary>
    public async Task PutAsync(string root, string provider, string voice, string text, byte[] wav, int maxMbPerVoice)
    {
        ArgumentNullException.ThrowIfNull(wav);

        try
        {
            var path = PathFor(root, provider, voice, text);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, wav).ConfigureAwait(false);

            if (maxMbPerVoice > 0)
            {
                TrimVoiceFolder(Path.GetDirectoryName(path)!, maxMbPerVoice);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TTS cache write failed ({Provider}/{Voice})", provider, voice);
        }
    }

    private void TrimVoiceFolder(string folder, int maxMb)
    {
        var files = new DirectoryInfo(folder).GetFiles("*.wav");
        var total = files.Sum(f => f.Length);
        var cap = (long)maxMb * 1024 * 1024;
        if (total <= cap)
        {
            return;
        }

        foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
        {
            try
            {
                total -= file.Length;
                file.Delete();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "TTS cache trim failed for {File}", file.Name);
            }

            if (total <= cap)
            {
                return;
            }
        }
    }

    private static string PathFor(string root, string provider, string voice, string text)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        var safeVoice = string.Concat(voice.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(root, provider, safeVoice, hash + ".wav");
    }
}
