using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Speech.Playback;

/// <summary>Plays synthesized WAV clips. Implemented by <see cref="SpeechPlayback"/>; a fake
/// stands in for tests of the arbiter shell.</summary>
public interface ISpeechPlayback
{
    /// <summary>Plays the clip to completion (or cancellation). Playback problems are logged
    /// and swallowed — a missing/vanished output device yields silence, not an error; nothing
    /// retries, and the next utterance re-enumerates from scratch, which is what makes
    /// recovery automatic. Only cancellation propagates.</summary>
    Task PlayAsync(byte[] wavBytes, CancellationToken cancellationToken);

    /// <summary>Plays a short programmatic cue chime by id ("cabin" interphone ding-dong,
    /// "company"/"acars" data beep). Unknown ids and any failure are a quiet no-op — a chime
    /// never breaks speech. The intercom filter is never applied (a chime is not voice).</summary>
    Task PlayChimeAsync(string chimeId, CancellationToken cancellationToken);
}

/// <summary>
/// WASAPI shared-mode playback with a per-clip pipeline: WAV → intercom filter (optional) →
/// client-side volume → resample to the device mix rate → mono/stereo adaptation. A fresh
/// WasapiOut and device are created and disposed per clip (utterances are seconds apart —
/// pooling is not worth device-invalidation handling).
/// </summary>
public sealed class SpeechPlayback : ISpeechPlayback
{
    private readonly IOptionsMonitor<SpeechOptions> _options;
    private readonly ILogger<SpeechPlayback> _logger;

    public SpeechPlayback(IOptionsMonitor<SpeechOptions> options, ILogger<SpeechPlayback> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _logger = logger;
    }

    public Task PlayAsync(byte[] wavBytes, CancellationToken cancellationToken)
        => PlayCoreAsync(wavBytes, applyIntercomFilter: true, cancellationToken);

    public Task PlayChimeAsync(string chimeId, CancellationToken cancellationToken)
    {
        var wav = ChimeSynth.Build(chimeId);
        return wav is null
            ? Task.CompletedTask
            : PlayCoreAsync(wav, applyIntercomFilter: false, cancellationToken);
    }

    private async Task PlayCoreAsync(byte[] wavBytes, bool applyIntercomFilter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wavBytes);
        cancellationToken.ThrowIfCancellationRequested();

        var options = _options.CurrentValue;
        MMDevice? device = null;
        WasapiOut? output = null;
        try
        {
            device = ResolveDevice(options.OutputDevice);
            if (device is null)
            {
                _logger.LogWarning("No usable output device; speech playback skipped");
                return;
            }

            using var reader = new WaveFileReader(new MemoryStream(wavBytes, writable: false));
            var chain = BuildChain(reader.ToSampleProvider(), device, options, applyIntercomFilter);

            output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 100);
            output.Init(chain.ToWaveProvider());

            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            output.PlaybackStopped += (_, e) =>
            {
                if (e.Exception is not null)
                {
                    completed.TrySetException(e.Exception);
                }
                else
                {
                    completed.TrySetResult();
                }
            };

            output.Play();

            // WaitAsync makes cancellation deterministic even if the device wedges and Stop()
            // never raises PlaybackStopped — the await can then never outlive the token.
            var local = output;
            using (cancellationToken.Register(() => { try { local.Stop(); } catch { } }))
            {
                await completed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw; // Pre-emption/shutdown — the arbiter handles cancelled renders.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Speech playback failed");
        }
        finally
        {
            output?.Dispose();
            device?.Dispose();
        }
    }

    private static ISampleProvider BuildChain(
        ISampleProvider sample, MMDevice device, SpeechOptions options, bool applyIntercomFilter)
    {
        if (applyIntercomFilter && options.IntercomFilter)
        {
            sample = new IntercomFilterProvider(sample);
        }

        var volume = Math.Clamp(options.Volume, 0, 100) / 100f;
        if (volume < 1f)
        {
            sample = new VolumeSampleProvider(sample) { Volume = volume };
        }

        var deviceRate = device.AudioClient.MixFormat.SampleRate;
        if (sample.WaveFormat.SampleRate != deviceRate)
        {
            sample = new WdlResamplingSampleProvider(sample, deviceRate);
        }

        var deviceChannels = device.AudioClient.MixFormat.Channels;
        if (sample.WaveFormat.Channels == 1 && deviceChannels >= 2)
        {
            sample = new MonoToStereoSampleProvider(sample);
        }
        else if (sample.WaveFormat.Channels == 2 && deviceChannels == 1)
        {
            sample = new StereoToMonoSampleProvider(sample);
        }

        return sample;
    }

    /// <summary>Finds the configured render device by exact friendly name, falling back to the
    /// system default. Every enumerated device except the returned one is disposed (the
    /// predecessor leaked the post-match remainder).</summary>
    private MMDevice? ResolveDevice(string configuredName)
    {
        using var enumerator = new MMDeviceEnumerator();

        MMDevice? match = null;
        if (!string.IsNullOrWhiteSpace(configuredName))
        {
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                if (match is null && string.Equals(device.FriendlyName, configuredName, StringComparison.Ordinal))
                {
                    match = device;
                }
                else
                {
                    device.Dispose();
                }
            }

            if (match is not null)
            {
                return match;
            }

            _logger.LogWarning("Output device '{Name}' not found; using system default", configuredName);
        }

        return enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            : null;
    }
}
