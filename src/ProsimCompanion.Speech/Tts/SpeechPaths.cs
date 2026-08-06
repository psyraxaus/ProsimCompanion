using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Speech.Tts;

/// <summary>Filesystem locations for the speech pillar.</summary>
public static class SpeechPaths
{
    /// <summary>The TTS cache root: the configured folder, or
    /// %LOCALAPPDATA%\ProsimCompanion\cache\tts when unset.</summary>
    public static string CacheRoot(SpeechOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return string.IsNullOrWhiteSpace(options.CacheFolder)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ProsimCompanion", "cache", "tts")
            : options.CacheFolder;
    }
}
