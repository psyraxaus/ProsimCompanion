namespace ProsimCompanion.Sim;

/// <summary>
/// Hands work from any thread to the one thread that owns the SimConnect session (issue #246).
/// The managed SimConnect wrapper is used from its message-pump thread; a write or a
/// registration issued from a thread-pool thread while the pump sat in <c>ReceiveMessage</c>
/// coincided, to within 12 ms, with both link drops in the owner's logs (EGLL 2026-11-15 at
/// engine shutdown, 2026-11-14 in the descent). Every SimConnect call now goes through here:
/// run at once when the caller already is the pump thread, else queued and run by the pump
/// between two messages.
/// </summary>
/// <remarks>No SimConnect type appears here, so the class loads and is testable without the
/// wrapper assembly.</remarks>
public sealed class SimPumpDispatcher
{
    private readonly object _gate = new();
    private readonly Queue<Item> _items = new();
    private readonly Action<Exception>? _onFault;
    private Action? _wake;
    private int _ownerThreadId;
    private bool _attached;

    /// <param name="onFault">Told about an exception a work item let escape. The pump carries
    /// on: one bad call must not end the session.</param>
    public SimPumpDispatcher(Action<Exception>? onFault = null) => _onFault = onFault;

    /// <summary>Work waiting for the pump.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    /// <summary>Called by the pump thread when its session exists. <paramref name="wake"/>
    /// makes the pump's wait return so queued work does not sit until the next message.</summary>
    public void Attach(Action wake)
    {
        ArgumentNullException.ThrowIfNull(wake);
        lock (_gate)
        {
            _attached = true;
            _ownerThreadId = Environment.CurrentManagedThreadId;
            _wake = wake;
        }
    }

    /// <summary>The session is gone: nothing more is accepted, and every waiting item is told
    /// so with <paramref name="reason"/> — a caller awaiting a write must not wait for ever.</summary>
    public void Detach(Exception reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        List<Item> abandoned;
        lock (_gate)
        {
            _attached = false;
            _ownerThreadId = 0;
            _wake = null;
            abandoned = [.. _items];
            _items.Clear();
        }

        foreach (var item in abandoned)
        {
            Abandon(item, reason);
        }
    }

    /// <summary>Runs <paramref name="work"/> on the pump thread: now when the caller is that
    /// thread, else on its next <see cref="Drain"/>. False when no session is attached — the
    /// work is not kept and <paramref name="abandoned"/> is not called.</summary>
    /// <param name="abandoned">Called instead of <paramref name="work"/> when the session
    /// ends before the pump got to it.</param>
    public bool Post(Action work, Action<Exception>? abandoned = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        Action? wake;
        lock (_gate)
        {
            if (!_attached)
            {
                return false;
            }

            if (_ownerThreadId != Environment.CurrentManagedThreadId)
            {
                _items.Enqueue(new Item(work, abandoned));
                wake = _wake;
            }
            else
            {
                wake = null;
            }
        }

        if (wake is null)
        {
            Run(work);
        }
        else
        {
            wake();
        }

        return true;
    }

    /// <summary>Runs everything queued so far, in order. Pump thread only. Returns how many
    /// items ran.</summary>
    public int Drain()
    {
        var ran = 0;
        while (true)
        {
            Item item;
            lock (_gate)
            {
                if (!_attached || _items.Count == 0)
                {
                    return ran;
                }

                item = _items.Dequeue();
            }

            Run(item.Work);
            ran++;
        }
    }

    private void Run(Action work)
    {
        try
        {
            work();
        }
        catch (Exception ex)
        {
            _onFault?.Invoke(ex);
        }
    }

    private void Abandon(Item item, Exception reason)
    {
        try
        {
            item.Abandoned?.Invoke(reason);
        }
        catch (Exception ex)
        {
            _onFault?.Invoke(ex);
        }
    }

    private readonly record struct Item(Action Work, Action<Exception>? Abandoned);
}
