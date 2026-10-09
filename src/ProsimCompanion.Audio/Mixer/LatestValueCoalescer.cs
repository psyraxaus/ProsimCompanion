namespace ProsimCompanion.Audio.Mixer;

/// <summary>
/// The knob debounce, as a pure core: callers <see cref="Offer"/> values as fast as ProSim
/// pushes them (SDK thread, never blocks), a 50 ms tick <see cref="Drain"/>s the latest value
/// per parameter — at most 20 writes per second per parameter, always the newest value, and
/// a value equal to the last one drained is skipped so a knob at rest costs nothing.
/// <see cref="Reset"/> forgets the sent values so a reconnect replays everything once.
/// </summary>
public sealed class LatestValueCoalescer
{
    private readonly object _gate = new();
    private readonly Dictionary<string, double> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _lastDrained = new(StringComparer.Ordinal);

    /// <summary>The tick period that gives ~20 writes per second.</summary>
    public static TimeSpan DefaultPeriod { get; } = TimeSpan.FromMilliseconds(50);

    public void Offer(string parameter, double value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parameter);
        lock (_gate)
        {
            if (_lastDrained.TryGetValue(parameter, out var last) && last == value)
            {
                _pending.Remove(parameter);
                return;
            }

            _pending[parameter] = value;
        }
    }

    /// <summary>Takes every pending (parameter, latest value) pair and remembers them as sent.</summary>
    public IReadOnlyList<(string Parameter, double Value)> Drain()
    {
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return [];
            }

            var batch = new List<(string, double)>(_pending.Count);
            foreach (var (parameter, value) in _pending)
            {
                batch.Add((parameter, value));
                _lastDrained[parameter] = value;
            }

            _pending.Clear();
            return batch;
        }
    }

    /// <summary>Forgets what was sent (the agent or Voicemeeter restarted) so the next
    /// <see cref="Offer"/> of every parameter goes out again.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _lastDrained.Clear();
        }
    }

    /// <summary>Forgets one parameter's sent value (its write failed) so the next offer of
    /// the same value goes out again.</summary>
    public void Forget(string parameter)
    {
        lock (_gate)
        {
            _lastDrained.Remove(parameter);
        }
    }

    /// <summary>Last value drained for a parameter, if any — what the mixer should be holding.</summary>
    public bool TryGetLastDrained(string parameter, out double value)
    {
        lock (_gate)
        {
            return _lastDrained.TryGetValue(parameter, out value);
        }
    }
}
