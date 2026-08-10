namespace ProsimCompanion.Sim;

/// <summary>
/// Thread-safe hand-off of the raw SimConnect system-event state from
/// <see cref="SimConnectService"/> (which receives it on the message-pump thread) to
/// <see cref="SimSessionService"/> (which samples it on its own tick). Defaults are the
/// conservative direction — not running, paused — so nothing session-gated can fire before
/// the first real events arrive; both the "Sim" and "Pause_EX1" subscriptions transmit their
/// current state immediately on subscribe.
/// </summary>
public sealed class SimSessionSignals
{
    private readonly object _gate = new();
    private bool _connected;
    private bool _simRunning;
    private bool _paused = true;
    private string? _simVersion;
    private bool _isMsfs2024;

    public void SetConnected(string simVersion, bool isMsfs2024)
    {
        lock (_gate)
        {
            _connected = true;
            _simVersion = simVersion;
            _isMsfs2024 = isMsfs2024;
        }
    }

    public void SetDisconnected()
    {
        lock (_gate)
        {
            _connected = false;
            _simRunning = false;
            _paused = true;
        }
    }

    public void SetSimRunning(bool running)
    {
        lock (_gate)
        {
            _simRunning = running;
        }
    }

    public void SetPaused(bool paused)
    {
        lock (_gate)
        {
            _paused = paused;
        }
    }

    public (bool Connected, bool SimRunning, bool Paused, string? SimVersion, bool IsMsfs2024) Read()
    {
        lock (_gate)
        {
            return (_connected, _simRunning, _paused, _simVersion, _isMsfs2024);
        }
    }
}
