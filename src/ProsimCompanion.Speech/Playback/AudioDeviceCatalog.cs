using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Speech.Playback;

/// <summary>
/// <see cref="IAudioDeviceCatalog"/> over NAudio. Capture names come from the legacy WaveIn
/// API rather than MMDevice because that is what <c>LanAsrRecognizer</c> opens — the picker
/// must offer the exact (31-char-truncated) strings the prefix match will later see. Render
/// names come from the same MMDevice enumeration <c>SpeechPlayback</c> resolves against.
/// Enumerated fresh per call: device hot-plug is common on sim rigs and the settings pages
/// call this only on load.
/// </summary>
public sealed class AudioDeviceCatalog : IAudioDeviceCatalog
{
    private readonly ILogger<AudioDeviceCatalog> _logger;

    public AudioDeviceCatalog(ILogger<AudioDeviceCatalog> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public IReadOnlyList<string> GetCaptureDeviceNames()
    {
        try
        {
            var names = new List<string>(WaveInEvent.DeviceCount);
            for (var i = 0; i < WaveInEvent.DeviceCount; i++)
            {
                var product = WaveInEvent.GetCapabilities(i).ProductName;
                if (!string.IsNullOrWhiteSpace(product) && !names.Contains(product, StringComparer.OrdinalIgnoreCase))
                {
                    names.Add(product);
                }
            }

            return names;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Capture device enumeration failed");
            return [];
        }
    }

    public IReadOnlyList<string> GetRenderDeviceNames()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var names = new List<string>();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    if (!string.IsNullOrWhiteSpace(device.FriendlyName))
                    {
                        names.Add(device.FriendlyName);
                    }
                }
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Render device enumeration failed");
            return [];
        }
    }
}
