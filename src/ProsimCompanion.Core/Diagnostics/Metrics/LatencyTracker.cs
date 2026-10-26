namespace ProsimCompanion.Core.Diagnostics.Metrics;

/// <summary>
/// A ring of the last <see cref="Capacity"/> latency samples in milliseconds with the three
/// figures the VOICE page prints: the last one, the median and the 95th percentile. Writers
/// are the ASR, LLM and TTS clients on their own threads; the reader is the diagnostics tick,
/// so every member locks. Percentiles are nearest-rank over a sorted copy: 64 doubles once a
/// second is nothing, and it keeps the arithmetic obvious.
/// </summary>
public sealed class LatencyTracker
{
    /// <summary>Samples kept.</summary>
    public const int Capacity = 64;

    private readonly object _gate = new();
    private readonly double[] _ring = new double[Capacity];
    private int _next;
    private int _count;
    private double _last;

    /// <summary>Records one sample; negative or NaN samples are ignored.</summary>
    public void Record(double milliseconds)
    {
        if (double.IsNaN(milliseconds) || milliseconds < 0)
        {
            return;
        }

        lock (_gate)
        {
            _ring[_next] = milliseconds;
            _next = (_next + 1) % Capacity;
            if (_count < Capacity)
            {
                _count++;
            }

            _last = milliseconds;
        }
    }

    /// <summary>The most recent sample, or null before the first.</summary>
    public double? Last
    {
        get
        {
            lock (_gate)
            {
                return _count == 0 ? null : _last;
            }
        }
    }

    /// <summary>Samples held (at most <see cref="Capacity"/>).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>The median of the held samples, or null when empty.</summary>
    public double? P50 => Percentile(50);

    /// <summary>The 95th percentile of the held samples, or null when empty.</summary>
    public double? P95 => Percentile(95);

    /// <summary>Nearest-rank percentile (1..100) over the held samples.</summary>
    public double? Percentile(int percent)
    {
        percent = Math.Clamp(percent, 1, 100);
        double[] sorted;
        lock (_gate)
        {
            if (_count == 0)
            {
                return null;
            }

            sorted = new double[_count];
            Array.Copy(_ring, sorted, _count);
        }

        Array.Sort(sorted);
        var rank = (int)Math.Ceiling(percent / 100.0 * sorted.Length);
        return sorted[Math.Clamp(rank, 1, sorted.Length) - 1];
    }
}
