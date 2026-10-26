namespace ProsimCompanion.Core.Diagnostics.Metrics;

/// <summary>
/// Events per second over a sliding five-second window, cheap enough to sit on the dataref
/// dispatch path (tens of hits a second): ten buckets of 500 ms, one increment per hit, a
/// bucket cleared the first time its slot is reused. <see cref="PerSecond"/> is the sum of the
/// buckets still inside the window divided by the window length.
/// </summary>
public sealed class RateCounter
{
    /// <summary>The window the rate is averaged over.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    private const int BucketCount = 10;
    private static readonly long BucketTicks = Window.Ticks / BucketCount;

    private readonly object _gate = new();
    private readonly long[] _counts = new long[BucketCount];
    private readonly long[] _slots = new long[BucketCount];
    private readonly Func<DateTimeOffset> _clock;

    public RateCounter(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
    }

    /// <summary>Counts one event.</summary>
    public void Hit()
    {
        var slot = _clock().UtcTicks / BucketTicks;
        var index = (int)(slot % BucketCount);
        lock (_gate)
        {
            if (_slots[index] != slot)
            {
                _slots[index] = slot;
                _counts[index] = 0;
            }

            _counts[index]++;
        }
    }

    /// <summary>Events per second over the window as of now.</summary>
    public double PerSecond
    {
        get
        {
            var currentSlot = _clock().UtcTicks / BucketTicks;
            long total = 0;
            lock (_gate)
            {
                for (var i = 0; i < BucketCount; i++)
                {
                    if (currentSlot - _slots[i] < BucketCount)
                    {
                        total += _counts[i];
                    }
                }
            }

            return total / Window.TotalSeconds;
        }
    }
}
