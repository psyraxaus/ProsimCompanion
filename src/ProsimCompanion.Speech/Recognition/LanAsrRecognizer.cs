using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NAudio.Wave;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Speech.Recognition;

/// <summary>
/// LAN faster-whisper recognizer: WaveInEvent capture at 16 kHz mono PCM16 with a simple RMS
/// VAD (constants proven in Prosim2FO — 500/32767 threshold, 700 ms end-silence, 300 ms
/// minimum speech, 300 ms pre-roll, 15 s hard cap), each segmented utterance POSTed as
/// multipart "file" to the transcribe endpoint. Transcribe-only: snapping and gating live in
/// the interpreter. Improvements over the predecessor: the configured input device is actually
/// honoured (matched on WaveIn product-name prefix), and grammar phrases are sent as hotwords
/// (the server has always accepted them).
/// </summary>
public sealed class LanAsrRecognizer : IVoiceRecognizer
{
    private const double SpeechRmsThreshold = 500.0;
    private const int EndSilenceMs = 700;
    private const int MinSpeechMs = 300;
    private const int PreRollMs = 300;
    private const int MaxUtteranceMs = 15_000;
    private const int SampleRate = 16_000;
    private const int BytesPerMs = SampleRate * 2 / 1000;

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly IOptionsMonitor<SpeechOptions> _options;
    private readonly ILogger<LanAsrRecognizer> _logger;
    private readonly object _gate = new();

    private WaveInEvent? _waveIn;
    private MemoryStream _buffer = new();
    private IReadOnlyList<string> _grammar = [];
    private bool _speechDetected;
    private int _silenceMs;

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

    public void StartListening()
    {
        lock (_gate)
        {
            if (_waveIn is not null)
            {
                return;
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
                _buffer = new MemoryStream();
                _speechDetected = false;
                _silenceMs = 0;
                _waveIn.StartRecording();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LAN ASR capture failed to start — no input device?");
                _waveIn = null;
            }
        }
    }

    public void StopListening()
    {
        byte[]? utterance = null;
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

            // PTT release: flush what was buffered if it contained speech.
            if (_speechDetected && _buffer.Length >= MinSpeechMs * BytesPerMs)
            {
                utterance = _buffer.ToArray();
            }

            _buffer = new MemoryStream();
            _speechDetected = false;
        }

        if (utterance is not null)
        {
            _ = TranscribeAsync(utterance);
        }
    }

    public void Dispose() => StopListening();

    private void OnData(object? sender, WaveInEventArgs e)
    {
        byte[]? utterance = null;
        lock (_gate)
        {
            var frameMs = e.BytesRecorded / BytesPerMs;
            var speaking = Rms(e.Buffer, e.BytesRecorded) > SpeechRmsThreshold;

            _buffer.Write(e.Buffer, 0, e.BytesRecorded);

            if (speaking)
            {
                _speechDetected = true;
                _silenceMs = 0;
            }
            else
            {
                _silenceMs += frameMs;
                if (!_speechDetected)
                {
                    TrimToPreRoll();
                }
            }

            // Segment on trailing silence or the hard cap.
            if (_speechDetected
                && (_silenceMs >= EndSilenceMs || _buffer.Length >= MaxUtteranceMs * (long)BytesPerMs))
            {
                if (_buffer.Length >= MinSpeechMs * BytesPerMs)
                {
                    utterance = _buffer.ToArray();
                }

                _buffer = new MemoryStream();
                _speechDetected = false;
                _silenceMs = 0;
            }
        }

        if (utterance is not null)
        {
            _ = TranscribeAsync(utterance);
        }
    }

    /// <summary>Keeps only the last 300 ms of pre-speech audio so leading silence never
    /// reaches the server.</summary>
    private void TrimToPreRoll()
    {
        var keep = PreRollMs * BytesPerMs;
        if (_buffer.Length <= keep)
        {
            return;
        }

        var tail = new byte[keep];
        _buffer.Position = _buffer.Length - keep;
        _ = _buffer.Read(tail, 0, keep);
        _buffer = new MemoryStream();
        _buffer.Write(tail, 0, keep);
    }

    private async Task TranscribeAsync(byte[] pcm)
    {
        try
        {
            var options = _options.CurrentValue;
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(1000, options.AsrTimeoutMs)));

            using var form = new MultipartFormDataContent();
            var wav = new ByteArrayContent(ToWav(pcm));
            wav.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
            form.Add(wav, "file", "utterance.wav");

            var grammar = _grammar;
            if (grammar.Count is > 0 and <= 50)
            {
                form.Add(new StringContent(string.Join(", ", grammar)), "hotwords");
            }

            var url = options.AsrBaseUrl.TrimEnd('/') + "/transcribe";
            using var response = await Http.PostAsync(url, form, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Rejected?.Invoke(this, new RecognizedEventArgs("", 0, null, null));
                return;
            }

            var payload = await System.Net.Http.Json.HttpContentJsonExtensions
                .ReadFromJsonAsync<TranscribeResponse>(response.Content, cancellationToken: cts.Token)
                .ConfigureAwait(false);
            var text = payload?.Text?.Trim() ?? "";
            if (text.Length == 0)
            {
                Rejected?.Invoke(this, new RecognizedEventArgs("", 0, null, payload?.NoSpeechProb));
                return;
            }

            Accepted?.Invoke(this, new RecognizedEventArgs(
                text, payload!.Confidence ?? 0, payload.Confidence, payload.NoSpeechProb));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LAN ASR transcription failed");
            Rejected?.Invoke(this, new RecognizedEventArgs("", 0, null, null));
        }
    }

    private static double Rms(byte[] buffer, int bytes)
    {
        if (bytes < 2)
        {
            return 0;
        }

        double sum = 0;
        var samples = bytes / 2;
        for (var i = 0; i < samples; i++)
        {
            double sample = BitConverter.ToInt16(buffer, i * 2);
            sum += sample * sample;
        }

        return Math.Sqrt(sum / samples);
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

    private static int ResolveDevice(string configuredName)
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

    private sealed record TranscribeResponse(
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("confidence")] double? Confidence,
        [property: JsonPropertyName("no_speech_prob")] double? NoSpeechProb);
}
