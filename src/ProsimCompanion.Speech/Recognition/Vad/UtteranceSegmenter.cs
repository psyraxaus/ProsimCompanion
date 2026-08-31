namespace ProsimCompanion.Speech.Recognition.Vad;

/// <summary>Segmentation tuning. <see cref="NegativeThreshold"/> implements Silero's default
/// hysteresis: speech starts at p ≥ <see cref="Threshold"/>, but trailing silence is only
/// counted below Threshold − 0.15 — probabilities in between hold the utterance open without
/// advancing the silence clock, so a soft word tail can't end a sentence early.</summary>
public sealed record SegmenterSettings(
    float Threshold,
    int EndSilenceMs,
    int MinSpeechMs,
    int PreRollMs,
    int MaxUtteranceMs)
{
    public float NegativeThreshold => Math.Max(0.01f, Threshold - 0.15f);
}

/// <summary>Why an utterance was closed. The names are logged verbatim (camelCase) into the
/// session JSONL and read by the tuning workflow — keep them stable.</summary>
public enum UtteranceEndReason
{
    Silence,
    HardCap,
    PttRelease,
}

/// <summary>Per-utterance diagnostics for the session event log (threshold tuning data).</summary>
public sealed record UtteranceStats(int DurationMs, float PeakProbability, UtteranceEndReason EndReason);

public sealed class UtteranceReadyEventArgs : EventArgs
{
    public UtteranceReadyEventArgs(byte[] pcm, UtteranceStats stats)
    {
        Pcm = pcm;
        Stats = stats;
    }

    public byte[] Pcm { get; }

    public UtteranceStats Stats { get; }
}

public sealed class UtteranceDroppedEventArgs : EventArgs
{
    public UtteranceDroppedEventArgs(int durationMs) => DurationMs = durationMs;

    public int DurationMs { get; }
}

/// <summary>
/// The utterance segmentation state machine, extracted from <c>LanAsrRecognizer.OnData</c> so
/// it is testable without a capture device. Consumes 16 kHz mono PCM16 buffers of any size
/// (WaveInEvent delivers 800 samples per 50 ms callback), accumulates them into exact
/// 512-sample classifier frames carrying the remainder across calls, and raises
/// <see cref="UtteranceReady"/> with the buffered audio when trailing silence, the hard cap,
/// or a PTT-release <see cref="Flush"/> closes the utterance. Pre-speech audio is trimmed to
/// the pre-roll so leading silence never reaches the ASR server. Not thread-safe — the owner
/// serialises calls (the recognizer holds its gate).
/// </summary>
public sealed class UtteranceSegmenter
{
    private const int SampleRate = 16_000;
    private const int BytesPerMs = SampleRate * 2 / 1000;
    private const int FrameMs = ISpeechFrameClassifier.FrameSamples * 1000 / SampleRate;
    private const int FrameBytes = ISpeechFrameClassifier.FrameSamples * 2;

    private readonly ISpeechFrameClassifier _classifier;
    private readonly SegmenterSettings _settings;
    private readonly short[] _frame = new short[ISpeechFrameClassifier.FrameSamples];
    private readonly byte[] _frameBytes = new byte[FrameBytes];

    private MemoryStream _buffer = new();
    private int _frameFill;
    private bool _speechDetected;
    private int _silenceMs;
    private float _peakProbability;

    public UtteranceSegmenter(ISpeechFrameClassifier classifier, SegmenterSettings settings)
    {
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(settings);

        _classifier = classifier;
        _settings = settings;
    }

    /// <summary>Raised with the complete utterance PCM (engine callback thread, under the
    /// owner's gate — handlers must not block).</summary>
    public event EventHandler<UtteranceReadyEventArgs>? UtteranceReady;

    /// <summary>Raised instead of <see cref="UtteranceReady"/> when a closed segment was
    /// shorter than the minimum speech length (a blip, door slam, breath).</summary>
    public event EventHandler<UtteranceDroppedEventArgs>? UtteranceDropped;

    /// <summary>Clears classifier state and all buffered audio; call at capture start.</summary>
    public void Reset()
    {
        _classifier.Reset();
        _buffer = new MemoryStream();
        _frameFill = 0;
        _speechDetected = false;
        _silenceMs = 0;
        _peakProbability = 0f;
    }

    /// <summary>Consumes one capture buffer. Audio only enters the utterance buffer as whole
    /// classified frames, so the byte stream and the classifier's decision stream stay
    /// aligned; the sub-frame remainder is carried to the next call.</summary>
    public void Push(byte[] buffer, int bytes)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        var samples = Math.Min(bytes, buffer.Length) / 2;
        for (var i = 0; i < samples; i++)
        {
            _frame[_frameFill++] = BitConverter.ToInt16(buffer, i * 2);
            if (_frameFill == ISpeechFrameClassifier.FrameSamples)
            {
                _frameFill = 0;
                ProcessFrame();
            }
        }
    }

    /// <summary>PTT release: closes the in-progress utterance (partial frame included) if it
    /// contained speech, otherwise discards the buffered ambience silently.</summary>
    public void Flush()
    {
        if (_frameFill > 0)
        {
            Buffer.BlockCopy(_frame, 0, _frameBytes, 0, _frameFill * 2);
            _buffer.Write(_frameBytes, 0, _frameFill * 2);
            _frameFill = 0;
        }

        if (_speechDetected)
        {
            CloseUtterance(UtteranceEndReason.PttRelease);
        }
        else
        {
            _buffer = new MemoryStream();
            _silenceMs = 0;
            _peakProbability = 0f;
        }
    }

    private void ProcessFrame()
    {
        Buffer.BlockCopy(_frame, 0, _frameBytes, 0, FrameBytes);
        _buffer.Write(_frameBytes, 0, FrameBytes);

        var p = _classifier.SpeechProbability(_frame);
        if (p > _peakProbability)
        {
            _peakProbability = p;
        }

        if (p >= _settings.Threshold)
        {
            _speechDetected = true;
            _silenceMs = 0;
        }
        else if (p < _settings.NegativeThreshold)
        {
            _silenceMs += FrameMs;
            if (!_speechDetected)
            {
                TrimToPreRoll();
            }
        }

        // Probabilities in the hysteresis band neither count as speech nor advance the
        // silence clock — the utterance is simply held open.

        if (_speechDetected
            && (_silenceMs >= _settings.EndSilenceMs
                || _buffer.Length >= _settings.MaxUtteranceMs * (long)BytesPerMs))
        {
            CloseUtterance(_silenceMs >= _settings.EndSilenceMs
                ? UtteranceEndReason.Silence
                : UtteranceEndReason.HardCap);
        }
    }

    /// <summary>Emits (or drops, when under min-speech) the buffered utterance and rearms for
    /// the next one. Emission happens synchronously, so a hard-capped utterance is followed
    /// immediately by a clean restart within the same capture stream.</summary>
    private void CloseUtterance(UtteranceEndReason reason)
    {
        var durationMs = (int)(_buffer.Length / BytesPerMs);
        var pcm = _buffer.Length >= _settings.MinSpeechMs * (long)BytesPerMs ? _buffer.ToArray() : null;
        var stats = new UtteranceStats(durationMs, _peakProbability, reason);

        _buffer = new MemoryStream();
        _speechDetected = false;
        _silenceMs = 0;
        _peakProbability = 0f;

        if (pcm is not null)
        {
            UtteranceReady?.Invoke(this, new UtteranceReadyEventArgs(pcm, stats));
        }
        else
        {
            UtteranceDropped?.Invoke(this, new UtteranceDroppedEventArgs(durationMs));
        }
    }

    /// <summary>Keeps only the last pre-roll of pre-speech audio so leading silence never
    /// reaches the server (frame-aligned version of the legacy trim).</summary>
    private void TrimToPreRoll()
    {
        var keep = _settings.PreRollMs * BytesPerMs;
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
}
