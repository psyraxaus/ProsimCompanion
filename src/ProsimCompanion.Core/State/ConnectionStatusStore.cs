using System.Collections.Concurrent;

namespace ProsimCompanion.Core.State;

/// <summary>
/// Observable store of subsystem connection states, consumed identically by the WPF shell and
/// Blazor components. Writers are the subsystem services; <see cref="Changed"/> fires on the
/// writer's thread — consumers marshal to their own context (dispatcher / <c>InvokeAsync</c>).
/// </summary>
public sealed class ConnectionStatusStore
{
    private readonly ConcurrentDictionary<string, Entry> _states = new();

    /// <summary>Raised after any state change, on the caller's thread.</summary>
    public event EventHandler? Changed;

    /// <summary>Records the state of a subsystem (see <see cref="Subsystems"/> for keys).
    /// Raises <see cref="Changed"/> only when the value actually changed: on the 2026-09-20
    /// flight the TTS router re-published "Connected" after every utterance and each call
    /// re-rendered the whole web layout and the WPF window (112 events for 105 sentences).</summary>
    /// <param name="subsystem">Subsystem key.</param>
    /// <param name="state">The new state.</param>
    /// <param name="reason">Why the subsystem is in this state, in words a pilot can act on
    /// (issue #158: a ProSim SDK that did not match the app left a red dot and nothing else on
    /// the page). Every call replaces it, so a later state without a reason clears it.</param>
    public void Set(string subsystem, ConnectionState state, string? reason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subsystem);
        var entry = new Entry(state, string.IsNullOrWhiteSpace(reason) ? null : reason.Trim());
        var changed = true;
        _states.AddOrUpdate(
            subsystem,
            entry,
            (_, previous) =>
            {
                changed = previous != entry;
                return entry;
            });

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The reason recorded with the subsystem's current state, or null when the
    /// writer gave none (the usual case) or the subsystem is unknown.</summary>
    public string? ReasonOf(string subsystem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subsystem);
        return _states.TryGetValue(subsystem, out var entry) ? entry.Reason : null;
    }

    /// <summary>Point-in-time copy of all known subsystem states, ordered by name.</summary>
    public IReadOnlyList<KeyValuePair<string, ConnectionState>> Snapshot()
        => [.. _states.OrderBy(pair => pair.Key).Select(pair => KeyValuePair.Create(pair.Key, pair.Value.State))];

    private readonly record struct Entry(ConnectionState State, string? Reason);
}
