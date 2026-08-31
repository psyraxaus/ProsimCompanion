using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NAudio.Wave;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Speech.Recognition.Vad;

namespace ProsimCompanion.Speech.Recognition;

/// <summary>
/// LAN whisper recognizer (faster-whisper wrapper or whisper.cpp, per <c>speech.asrApi</c> —
/// the wire differences live in <see cref="AsrServerApi"/>): WaveInEvent capture at 16 kHz
/// mono PCM16, segmented into utterances by <see cref="UtteranceSegmenter"/> over a pluggable
/// frame classifier (RMS energy gate, constants proven in Prosim2FO), each segmented
/// utterance POSTed as multipart "file" to the transcribe endpoint. Transcribe-only: snapping
/// and gating live in the interpreter. Improvements over the predecessor: the configured input
/// device is actually honoured (matched on WaveIn product-name prefix), and grammar phrases
/// are sent for decoder biasing (hotwords / initial prompt).
/// </summary>
public sealed class LanAsrRecognizer : IVoiceRecognizer
{
    /// <summary>Legacy RMS-path segmentation (700 ms end-silence etc.) — deliberately NOT
    /// driven by the Vad* options, so the fallback behaves exactly like the proven
    /// predecessor gate regardless of Silero tuning.</summary>
    private static readonly SegmenterSettings RmsSegmenterSettings =
        new(Threshold: 0.5f, EndSilenceMs: 700, MinSpeechMs: 300, PreRollMs: 300, MaxUtteranceMs: 15_000);

    private const int SampleRate = 16_000;

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly IOptionsMonitor<SpeechOptions> _options;
    private readonly ILogger<LanAsrRecognizer> _logger;
    private readonly object _gate = new();

    private WaveInEvent? _waveIn;
    private UtteranceSegmenter? _segmenter;
    private IReadOnlyList<string> _grammar = [];
    private int _serverFailureLogged;

    public LanAsrRecognizer(IOptionsMonitor<SpeechOptions> options, ILogger<LanAsrRecognizer> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _logger = logger;
    }

    public event EventHandler<RecognizedEventArgs>? Accepted;

    public event EventHandler<RecognizedEventArgs>? Rejected;

    public void SetGrammar(IReadOnlyList<string> phrases)
    {
        ArgumentNullException.ThrowIfNull(phrases);
        _grammar = [.. phrases.Where(p => p != NumberGrammar.Sentinel)];
    }

    /// <summary>Reality, not intent: true only while a capture device is actually open —
    /// the controller's reconcile reads this (issue #61).</summary>
    public bool IsListening
    {
        get
        {
            lock (_gate)
            {
                return _waveIn is not null;
            }
        }
    }

    public bool StartListening()
    {
        lock (_gate)
        {
            if (_waveIn is not null)
            {
                return true;
            }

            try
            {
                _waveIn = new WaveInEvent
                {
                    DeviceNumber = ResolveDevice(_options.CurrentValue.InputDevice),
                    WaveFormat = new WaveFormat(SampleRate, 16, 1),
                    BufferMilliseconds = 50,
                };
                _waveIn.DataAvailable += OnData;
                _segmenter = CreateSegmenter();
                _waveIn.StartRecording();
            }
            catch (Exception ex)
            {
                // Mic busy at app start is the COMMON case (issue #61) — Debug here; the
                // controller logs the once-per-episode Warning and retries with backoff
                // until the device frees up.
                _logger.LogDebug(ex, "LAN ASR capture failed to start — input device busy or absent");
                if (_waveIn is not null)
                {
                    // StartRecording threw after construction: unhook and drop the half-open
                    // device so the next retry starts clean.
                    _waveIn.DataAvailable -= OnData;
                    _waveIn.Dispose();
                }

                _waveIn = null;
            }

            return _waveIn is not null;
        }
    }

    public void StopListening()
    {
        lock (_gate)
        {
            if (_waveIn is null)
            {
                return;
            }

            try
            {
                _waveIn.StopRecording();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Stopping LAN ASR capture failed");
            }

            _waveIn.DataAvailable -= OnData;
            _waveIn.Dispose();
            _waveIn = null;

            // PTT release: the flush emits the buffered utterance (fire-and-forget POST)
            // if it contained speech, and discards buffered ambience otherwise.
            _segmenter?.Flush();
            _segmenter = null;
        }
    }

    public void Dispose() => StopListening();

    /// <summary>Builds a fresh segmenter for one capture episode; settings are re-read here so
    /// options changes apply on the next StartListening without a restart.</summary>
    private UtteranceSegmenter CreateSegmenter()
    {
        var segmenter = new UtteranceSegmenter(new RmsClassifier(), RmsSegmenterSettings);
        segmenter.Reset();
        segmenter.UtteranceReady += (_, e) => _ = TranscribeAsync(e.Pcm);
        segmenter.UtteranceDropped += (_, e) =>
            _logger.LogDebug("ASR segment dropped under min-speech ({DurationMs} ms)", e.DurationMs);
        return segmenter;
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        lock (_gate)
        {
            _segmenter?.Push(e.Buffer, e.BytesRecorded);
        }
    }

    private async Task TranscribeAsync(byte[] pcm)
    {
        try
        {
            var options = _options.CurrentValue;
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(1000, options.AsrTimeoutMs)));

            var api = options.AsrApi;
            using var form = new MultipartFormDataContent();
            var wav = new ByteArrayContent(ToWav(pcm));
            wav.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
            form.Add(wav, "file", "utterance.wav");

            var grammar = _grammar;
            if (grammar.Count is > 0 and <= 50)
            {
                form.Add(new StringContent(string.Join(", ", grammar)), AsrServerApi.BiasField(api));
            }

            foreach (var (name, value) in AsrServerApi.ExtraFields(api))
            {
                form.Add(new StringContent(value), name);
            }

            var url = options.AsrBaseUrl.TrimEnd('/') + AsrServerApi.TranscribePath(api);
            using var response = await Http.PostAsync(url, form, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                NoteServerFailure(url, (int)response.StatusCode, api);
                Rejected?.Invoke(this, new RecognizedEventArgs("", 0, null, null));
                return;
            }

            var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            var transcript = AsrServerApi.Parse(api, body);
            if (transcript is null)
            {
                NoteServerFailure(url, (int)response.StatusCode, api);
                Rejected?.Invoke(this, new RecognizedEventArgs("", 0, null, null));
                return;
            }

            NoteServerRecovered(url);
            if (transcript.Text.Length == 0)
            {
                Rejected?.Invoke(this, new RecognizedEventArgs("", 0, null, transcript.NoSpeechProb));
                return;
            }

            Accepted?.Invoke(this, new RecognizedEventArgs(
                transcript.Text, transcript.Confidence ?? 0, transcript.Confidence, transcript.NoSpeechProb));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LAN ASR transcription failed");
            Rejected?.Invoke(this, new RecognizedEventArgs("", 0, null, null));
        }
    }

    /// <summary>One Warning per failure episode, Debug thereafter. A server that answers but
    /// not in the configured shape (wrong <c>speech.asrApi</c>, wrong path) used to drop every
    /// utterance with no log line at all — 2026-08-29, a whole morning of "voice is broken"
    /// with <c>/health</c> green.</summary>
    private void NoteServerFailure(string url, int statusCode, AsrApiKind api)
    {
        if (Interlocked.Exchange(ref _serverFailureLogged, 1) == 0)
        {
            _logger.LogWarning(
                "LAN ASR server {Url} answered {StatusCode} for flavour {Api} — utterances are being dropped; check speech.asrApi matches the server",
                url, statusCode, api);
        }
        else
        {
            _logger.LogDebug("LAN ASR server {Url} answered {StatusCode}", url, statusCode);
        }
    }

    private void NoteServerRecovered(string url)
    {
        if (Interlocked.Exchange(ref _serverFailureLogged, 0) == 1)
        {
            _logger.LogInformation("LAN ASR server {Url} answering again", url);
        }
    }

    /// <summary>Canonical 44-byte WAV header + PCM.</summary>
    private static byte[] ToWav(byte[] pcm)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + pcm.Length);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(SampleRate);
        w.Write(SampleRate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(pcm.Length);
        w.Write(pcm);
        return ms.ToArray();
    }

    // Internal: SpeechDiagnosticsService's mic test must open the same device the recognizer
    // will, so they share one resolution rule.
    internal static int ResolveDevice(string configuredName)
    {
        if (string.IsNullOrWhiteSpace(configuredName))
        {
            return 0;
        }

        // WaveIn product names are truncated to 31 chars — prefix-match both ways.
        for (var i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            var product = WaveInEvent.GetCapabilities(i).ProductName;
            if (product.StartsWith(configuredName, StringComparison.OrdinalIgnoreCase)
                || configuredName.StartsWith(product, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return 0;
    }

}
