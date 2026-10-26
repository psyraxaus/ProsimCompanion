namespace ProsimCompanion.Core.Diagnostics.Metrics;

/// <summary>
/// End-to-end latency of one voice turn: the pilot stops talking, the First Officer's first
/// audio comes out. No utterance id crosses the pipeline (the recognizer raises a text, the
/// router picks a feature, the arbiter plays whatever that feature enqueued), so the two
/// instants are correlated by time: the recognizer stamps the utterance end, the arbiter's
/// first audio start within <see cref="Window"/> closes the turn and records the difference.
/// A rejected utterance clears the stamp so an unrelated callout is not measured against it;
/// a callout with no stamp open is simply not a turn. One instance, three writers on three
/// threads, hence the lock.
/// </summary>
public sealed class VoiceTurnTracker
{
    /// <summary>How long a stamped utterance end waits for audio before it expires.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(20);

    private readonly object _gate = new();
    private readonly LatencyTracker _target;
    private DateTimeOffset? _utteranceEndUtc;

    public VoiceTurnTracker(LatencyRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _target = registry[LatencyNames.EndToEnd];
    }

    /// <summary>The pilot stopped talking (the segmenter closed the utterance).</summary>
    public void UtteranceEnded(DateTimeOffset now)
    {
        lock (_gate)
        {
            _utteranceEndUtc = now;
        }
    }

    /// <summary>The utterance was rejected (noise, empty, self-echo): no turn to measure.</summary>
    public void UtteranceRejected()
    {
        lock (_gate)
        {
            _utteranceEndUtc = null;
        }
    }

    /// <summary>The First Officer's audio is about to start. Records the turn when an
    /// utterance end is open and inside the window; returns the measured milliseconds, else null.</summary>
    public double? AudioStarted(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_utteranceEndUtc is not { } ended)
            {
                return null;
            }

            _utteranceEndUtc = null;
            var elapsed = now - ended;
            if (elapsed < TimeSpan.Zero || elapsed > Window)
            {
                return null;
            }

            _target.Record(elapsed.TotalMilliseconds);
            return elapsed.TotalMilliseconds;
        }
    }

    /// <summary>True while an utterance end is waiting for audio (the FO is "thinking").</summary>
    public bool TurnOpen
    {
        get
        {
            lock (_gate)
            {
                return _utteranceEndUtc is not null;
            }
        }
    }
}
