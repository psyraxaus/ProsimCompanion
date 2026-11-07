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
    /// recovery automatic. Only cancellation propagates.
    /// <paramref name="overrideIntercomFilter"/> is the per-call (speaker-role) intercom
    /// decision: null keeps the options-driven default, true forces the band-pass (purser on
    /// the interphone), false bypasses it (company readout) even when the FO filter is on.</summary>
    Task PlayAsync(byte[] wavBytes, CancellationToken cancellationToken, bool? overrideIntercomFilter = null);

    /// <summary>Plays a short programmatic cue chime by id ("cabin" interphone ding-dong,
    /// "company"/"acars" data beep). Unknown ids and any failure are a quiet no-op — a chime
    /// never breaks speech. The intercom filter is never applied (a chime is not voice).
    /// <paramref name="reason"/> names the caller for the trail: every chime is an Information
    /// line and an <c>audio.chime</c> session event (owner 2026-11-07: a stray ding-dong in the
    /// headset with nothing in the log to match it to).</summary>
    Task PlayChimeAsync(string chimeId, CancellationToken cancellationToken, string? reason = null);
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
    private readonly Core.EventLog.JsonlEventLog? _eventLog;

    public SpeechPlayback(IOptionsMonitor<SpeechOptions> options, ILogger<SpeechPlayback> logger, Core.EventLog.JsonlEventLog? eventLog = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _logger = logger;
        _eventLog = eventLog;
    }

    public Task PlayAsync(byte[] wavBytes, CancellationToken cancellationToken, bool? overrideIntercomFilter = null)
        => PlayCoreAsync(
            wavBytes,
            // null = the FO's own setting; a role decision (purser/company) wins outright.
            overrideIntercomFilter ?? _options.CurrentValue.IntercomFilter,
            cancellationToken);

    public Task PlayChimeAsync(string chimeId, CancellationToken cancellationToken, string? reason = null)
    {
        var wav = ChimeSynth.Build(chimeId);
        if (wav is null)
        {
            return Task.CompletedTask;
        }

        // The one place every programmatic tone goes through: name it, so a chime heard in the
        // headset can always be matched to its cause — or ruled out as not ours.
        var why = string.IsNullOrWhiteSpace(reason) ? "unnamed" : reason.Trim();
        var device = _options.CurrentValue.OutputDevice;
        _logger.LogInformation("Chime {Chime} played ({Reason}) on output {Device}",
            chimeId, why, string.IsNullOrWhiteSpace(device) ? "default" : device);
        _eventLog?.Record("audio.chime", new { chime = chimeId, reason = why, device });
        return PlayCoreAsync(wav, applyIntercomFilter: false, cancellationToken);
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
        // The decision is fully resolved by the callers (role override, FO setting, or the
        // chime's hard false) — no second look at options here.
        if (applyIntercomFilter)
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
