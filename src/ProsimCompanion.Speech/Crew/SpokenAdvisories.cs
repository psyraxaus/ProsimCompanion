namespace ProsimCompanion.Speech.Crew;

/// <summary>
/// The one advisory the pilot can wave off (issue #168): an advisory service registers the
/// cancellation of the line it is about to speak, and "disregard" cancels whichever one is
/// current — mid-utterance playback stops, a still-queued line is withdrawn. Only the most
/// recent registration is current; an advisory that finished (or was superseded) is
/// forgotten, so a later "disregard" with nothing to cancel is just acknowledged.
/// Thread-safe; the arbiter's token semantics do the actual stopping.
/// </summary>
public sealed class SpokenAdvisories
{
    private readonly object _gate = new();
    private CancellationTokenSource? _current;
    private string? _tag;

    /// <summary>The tag of the advisory a "disregard" would cancel, or null.</summary>
    public string? CurrentTag
    {
        get
        {
            lock (_gate)
            {
                return _tag;
            }
        }
    }

    /// <summary>Registers a new current advisory and returns the token to speak it with.</summary>
    public CancellationToken Begin(string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _current = cts;
            _tag = tag;
        }

        return cts.Token;
    }

    /// <summary>The advisory spoken with <paramref name="token"/> finished (any outcome): it
    /// is no longer current. A newer registration is left alone.</summary>
    public void End(CancellationToken token)
    {
        lock (_gate)
        {
            if (_current is null || _current.Token != token)
            {
                return;
            }

            _current = null;
            _tag = null;
        }
    }

    /// <summary>Cancels the current advisory. True (with its tag) when there was one.</summary>
    public bool CancelCurrent(out string? tag)
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _current;
            tag = _tag;
            _current = null;
            _tag = null;
        }

        if (cts is null)
        {
            return false;
        }

        // Not disposed: the arbiter may still register on the token while the request drains.
        cts.Cancel();
        return true;
    }
}
