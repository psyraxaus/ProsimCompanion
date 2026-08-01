using ProsimCompanion.Core.Aircraft;

namespace ProsimCompanion.Prosim.DataRefs;

/// <summary>
/// Pure bookkeeping for dataref subscriptions — no SDK types, fully unit-testable. Tracks which
/// names are subscribed at which cadence (multiple subscribers share one registration at the
/// fastest requested tier), caches pushed values, and flags them stale on disconnect instead of
/// clearing ("valid or hold previous decision").
/// </summary>
public sealed class DataRefSubscriptionTable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Action<string, Exception>? _subscriberErrorSink;

    /// <param name="subscriberErrorSink">
    /// Called when a subscriber's ValueChanged handler throws (name, exception). Subscriber
    /// exceptions are contained so one bad handler can never break the push pump.
    /// </param>
    public DataRefSubscriptionTable(Action<string, Exception>? subscriberErrorSink = null)
    {
        _subscriberErrorSink = subscriberErrorSink;
    }

    /// <summary>Raised when a name needs a (new or faster) server registration: (name, intervalMs).</summary>
    public event Action<string, int>? RegistrationNeeded;

    /// <summary>Raised when the last subscriber for a name is disposed.</summary>
    public event Action<string>? RegistrationReleased;

    /// <summary>Creates a subscription handle for a dataref at the given cadence.</summary>
    public IDataRefSubscription Subscribe(string name, DataRefTier tier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var interval = (int)tier;
        Subscription subscription;
        var needsRegistration = false;

        lock (_gate)
        {
            if (!_entries.TryGetValue(name, out var entry))
            {
                entry = new Entry(name, interval);
                _entries[name] = entry;
                needsRegistration = true;
            }
            else if (interval < entry.IntervalMs)
            {
                // A faster tier was requested: the shared registration speeds up. It never slows
                // back down when fast subscribers leave — a faster cadence is always safe.
                entry.IntervalMs = interval;
                needsRegistration = true;
            }

            subscription = new Subscription(this, entry);
            entry.Subscriptions.Add(subscription);

            if (needsRegistration)
            {
                interval = entry.IntervalMs;
            }
        }

        if (needsRegistration)
        {
            RegistrationNeeded?.Invoke(name, interval);
        }

        return subscription;
    }

    /// <summary>Records a pushed value and notifies subscribers (on the caller's thread).</summary>
    public void UpdateValue(string name, object? value, DateTimeOffset timestampUtc)
    {
        Subscription[] toNotify;

        lock (_gate)
        {
            if (!_entries.TryGetValue(name, out var entry))
            {
                return;
            }

            entry.RawValue = value;
            entry.LastUpdatedUtc = timestampUtc;
            entry.IsStale = false;
            toNotify = [.. entry.Subscriptions];
        }

        Notify(name, toNotify);
    }

    /// <summary>Flags every cached value stale (connection lost) and notifies subscribers.</summary>
    public void MarkAllStale()
    {
        List<(string Name, Subscription[] Subscriptions)> toNotify;

        lock (_gate)
        {
            toNotify = new(_entries.Count);
            foreach (var entry in _entries.Values)
            {
                if (!entry.IsStale)
                {
                    entry.IsStale = true;
                    toNotify.Add((entry.Name, [.. entry.Subscriptions]));
                }
            }
        }

        foreach (var (name, subscriptions) in toNotify)
        {
            Notify(name, subscriptions);
        }
    }

    /// <summary>Names and cadences that currently need a server registration (used to replay
    /// registrations after a reconnect).</summary>
    public IReadOnlyList<(string Name, int IntervalMs)> ActiveRegistrations()
    {
        lock (_gate)
        {
            return [.. _entries.Values.Select(entry => (entry.Name, entry.IntervalMs))];
        }
    }

    private void Notify(string name, Subscription[] subscriptions)
    {
        foreach (var subscription in subscriptions)
        {
            try
            {
                subscription.RaiseValueChanged();
            }
            catch (Exception ex)
            {
                _subscriberErrorSink?.Invoke(name, ex);
            }
        }
    }

    private void Remove(Subscription subscription, Entry entry)
    {
        var released = false;

        lock (_gate)
        {
            entry.Subscriptions.Remove(subscription);
            if (entry.Subscriptions.Count == 0)
            {
                _entries.Remove(entry.Name);
                released = true;
            }
        }

        if (released)
        {
            RegistrationReleased?.Invoke(entry.Name);
        }
    }

    private sealed class Entry
    {
        public Entry(string name, int intervalMs)
        {
            Name = name;
            IntervalMs = intervalMs;
        }

        public string Name { get; }
        public int IntervalMs { get; set; }
        public object? RawValue { get; set; }
        public DateTimeOffset? LastUpdatedUtc { get; set; }
        public bool IsStale { get; set; }
        public List<Subscription> Subscriptions { get; } = [];
    }

    private sealed class Subscription : IDataRefSubscription
    {
        private readonly DataRefSubscriptionTable _table;
        private readonly Entry _entry;
        private int _disposed;

        public Subscription(DataRefSubscriptionTable table, Entry entry)
        {
            _table = table;
            _entry = entry;
        }

        public string Name => _entry.Name;

        public object? RawValue
        {
            get
            {
                lock (_table._gate)
                {
                    return _entry.RawValue;
                }
            }
        }

        public bool IsStale
        {
            get
            {
                lock (_table._gate)
                {
                    return _entry.IsStale;
                }
            }
        }

        public DateTimeOffset? LastUpdatedUtc
        {
            get
            {
                lock (_table._gate)
                {
                    return _entry.LastUpdatedUtc;
                }
            }
        }

        public event EventHandler? ValueChanged;

        public T GetValue<T>(T fallback) => DataRefCoercion.Coerce(RawValue, fallback);

        public void RaiseValueChanged() => ValueChanged?.Invoke(this, EventArgs.Empty);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _table.Remove(this, _entry);
            }
        }
    }
}
