using Microsoft.Extensions.Logging;

namespace ProsimCompanion.Gsx.Menu;

/// <summary>
/// Dispatches GSX-raised question menus on the <b>rising edge of the menu title</b>: when a menu
/// is shown with a title different from the last-seen one, the first matching handler (StartsWith,
/// case-insensitive) runs exactly once; the edge clears when the menu hides or the title empties,
/// so a genuine re-raise is treated fresh. Handlers are serialized — overlapping mirror updates
/// off the receive thread can never run two handlers concurrently.
/// </summary>
public sealed class GsxQuestionDispatcher : IDisposable
{
    private readonly ILogger<GsxQuestionDispatcher> _logger;
    private readonly List<(string TitlePrefix, Func<CancellationToken, Task> Handler)> _handlers = [];
    private readonly SemaphoreSlim _dispatchLock = new(1, 1);
    private readonly object _gate = new();
    private string? _lastSeenTitle;

    /// <summary>Unmatched titles already logged this session. A single last-title latch was
    /// not enough (issue #76): two known-ignorable menus alternating ("Interrupt pushback?" /
    /// operator popup) re-armed each other and re-logged every swap.</summary>
    private readonly HashSet<string> _unhandledTitlesLogged = new(StringComparer.Ordinal);

    public GsxQuestionDispatcher(ILogger<GsxQuestionDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public void Dispose() => _dispatchLock.Dispose();

    /// <summary>Registers a handler for menus whose title starts with the prefix. First
    /// registered match wins.</summary>
    public void Register(string titlePrefix, Func<CancellationToken, Task> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(titlePrefix);
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            _handlers.Add((titlePrefix, handler));
        }
    }

    /// <summary>Feed every mirror menu/menuShown update here (receive thread).</summary>
    public async Task OnMenuUpdatedAsync(bool menuShown, string? title, CancellationToken cancellationToken = default)
    {
        if (!menuShown || string.IsNullOrEmpty(title))
        {
            // Menu hidden or title empty: clear the edge so a re-raise dispatches fresh.
            lock (_gate)
            {
                _lastSeenTitle = null;
            }
            return;
        }

        Func<CancellationToken, Task>? handler = null;
        lock (_gate)
        {
            if (string.Equals(title, _lastSeenTitle, StringComparison.Ordinal))
            {
                return; // same menu still up — already dispatched
            }

            _lastSeenTitle = title;
            foreach (var (prefix, candidate) in _handlers)
            {
                if (title.StartsWith(prefix, StringComparison.InvariantCultureIgnoreCase))
                {
                    handler = candidate;
                    break;
                }
            }
        }

        if (handler is null)
        {
            // GSX re-raises an open menu about once a second (hide/show cycles that re-arm the
            // dispatch edge) — a stuck unhandled menu logged every second for minutes before a
            // crash (issue #46). Each unmatched title is logged ONCE PER SESSION, at Info so a
            // pilot reading the log sees which menus (e.g. "Interrupt pushback?") were
            // deliberately left alone (issue #76).
            lock (_gate)
            {
                if (!_unhandledTitlesLogged.Add(title))
                {
                    return;
                }
            }

            _logger.LogInformation(
                "GSX menu '{Title}' has no question handler — leaving it for the user (logged once per session)",
                title);
            return;
        }

        await _dispatchLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _logger.LogInformation("Dispatching question handler for menu '{Title}'", title);
            await handler(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Safe-fail: a failing handler leaves the menu for the user.
            _logger.LogError(ex, "Question handler for menu '{Title}' failed; menu left for the user", title);
        }
        finally
        {
            _dispatchLock.Release();
        }
    }
}
