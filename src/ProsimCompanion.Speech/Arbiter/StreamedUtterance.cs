using System.Threading.Channels;

namespace ProsimCompanion.Speech.Arbiter;

/// <summary>One piece of a streamed utterance.</summary>
/// <param name="Text">What to say.</param>
/// <param name="Cacheable">False for text that will never be asked for again (an LLM
/// sentence): the TTS disk cache is neither read nor written for it.</param>
public sealed record SpeechSegment(string Text, bool Cacheable = true);

/// <summary>
/// An utterance that arrives in pieces (issue #147): the producer writes sentences as they
/// become ready, the arbiter speaks them one after another inside ONE queue item. Because it
/// is one item, nothing of the same or a lower priority can be spoken between two of its
/// sentences; a Critical still cuts it, and a cut stream ends — it is never resumed or
/// restarted (the content may be stale, and the pilot must not hear it from the top).
/// <para>
/// Producer side: <see cref="TryWrite"/>, <see cref="Complete"/>, and watch
/// <see cref="Aborted"/> (stop producing) and <see cref="StarvedFor"/> (the speech has caught
/// up and is waiting). Arbiter side: the internal members. Thread-safe.
/// </para>
/// </summary>
public sealed class StreamedUtterance : IDisposable
{
    private readonly Channel<SpeechSegment> _channel = Channel.CreateUnbounded<SpeechSegment>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _aborted = new();
    private readonly TaskCompletionSource<DateTimeOffset> _firstAudio =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private readonly List<string> _spoken = [];
    private DateTimeOffset? _starvedSince;

    /// <summary>Queues a segment. False when the stream was aborted or already completed —
    /// the segment will not be spoken.</summary>
    public bool TryWrite(SpeechSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        return !_aborted.IsCancellationRequested && _channel.Writer.TryWrite(segment);
    }

    /// <summary>No more segments will come: the item ends when the queued ones are spoken.</summary>
    public void Complete() => _channel.Writer.TryComplete();

    /// <summary>Cancelled when the arbiter ended the item before its last segment
    /// (pre-empted by a Critical, expired, suppressed, no longer valid, caller cancel,
    /// shutdown). The producer should stop: nothing further will be spoken.</summary>
    public CancellationToken Aborted => _aborted.Token;

    /// <summary>Completes when the first segment starts PLAYING, with that instant. Cancelled
    /// if the stream ends without any audio.</summary>
    public Task<DateTimeOffset> FirstAudio => _firstAudio.Task;

    /// <summary>The segments spoken to their end, in order.</summary>
    public IReadOnlyList<string> Spoken
    {
        get
        {
            lock (_gate)
            {
                return [.. _spoken];
            }
        }
    }

    /// <summary>How long the arbiter has been waiting with nothing to say — zero while it is
    /// speaking or still has a segment queued. The producer's stall rule reads this.</summary>
    public TimeSpan StarvedFor(DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            return _starvedSince is { } since && nowUtc > since ? nowUtc - since : TimeSpan.Zero;
        }
    }

    internal ChannelReader<SpeechSegment> Reader => _channel.Reader;

    internal void MarkStarved(DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            _starvedSince ??= nowUtc;
        }
    }

    internal void MarkFed()
    {
        lock (_gate)
        {
            _starvedSince = null;
        }
    }

    internal void MarkFirstAudio(DateTimeOffset nowUtc) => _firstAudio.TrySetResult(nowUtc);

    internal void MarkSpoken(string text)
    {
        lock (_gate)
        {
            _spoken.Add(text);
        }
    }

    /// <summary>The arbiter is done with the item. <paramref name="aborted"/> = it ended
    /// early; a normal end leaves <see cref="Aborted"/> untouched.</summary>
    internal void Close(bool aborted)
    {
        _channel.Writer.TryComplete();
        _firstAudio.TrySetCanceled();
        MarkFed();
        if (aborted)
        {
            try
            {
                _aborted.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The producer already disposed the stream: nobody is listening.
            }
        }
    }

    public void Dispose() => _aborted.Dispose();
}
