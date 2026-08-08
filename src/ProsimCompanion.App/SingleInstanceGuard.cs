namespace ProsimCompanion.App;

/// <summary>
/// Single-instance enforcement (roadmap Phase 7): a named mutex claims the instance, and a named
/// event lets a second launch tell the running instance to bring its window to the front instead
/// of silently dying. Session-local names — two Windows users can each run their own copy.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\ProsimCompanion.SingleInstance";
    private const string ShowEventName = @"Local\ProsimCompanion.ShowWindow";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showEvent;
    private readonly RegisteredWaitHandle _showWait;
    private volatile Action? _onShowRequested;

    private SingleInstanceGuard(Mutex mutex, EventWaitHandle showEvent)
    {
        _mutex = mutex;
        _showEvent = showEvent;
        // executeOnlyOnce: false — every later second-launch signal re-fires the callback.
        _showWait = ThreadPool.RegisterWaitForSingleObject(
            _showEvent,
            (_, _) => _onShowRequested?.Invoke(),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    /// <summary>Claims the single-instance mutex. Null when another instance already owns it —
    /// the caller should <see cref="SignalExistingInstance"/> and exit.</summary>
    public static SingleInstanceGuard? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }

        return new SingleInstanceGuard(
            mutex,
            new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName));
    }

    /// <summary>Second-launch path: pokes the running instance's show-window event.</summary>
    public static void SignalExistingInstance()
    {
        try
        {
            using var showEvent = EventWaitHandle.OpenExisting(ShowEventName);
            showEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The first instance died between the mutex check and here — nothing to poke.
        }
    }

    /// <summary>Called (on a thread-pool thread) whenever a second launch asks for the window;
    /// the handler must marshal to the dispatcher itself.</summary>
    public void OnShowRequested(Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _onShowRequested = handler;
    }

    public void Dispose()
    {
        _showWait.Unregister(null);
        _showEvent.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
