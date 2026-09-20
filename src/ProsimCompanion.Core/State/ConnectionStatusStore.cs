using System.Collections.Concurrent;

namespace ProsimCompanion.Core.State;

/// <summary>
/// Observable store of subsystem connection states, consumed identically by the WPF shell and
/// Blazor components. Writers are the subsystem services; <see cref="Changed"/> fires on the
/// writer's thread — consumers marshal to their own context (dispatcher / <c>InvokeAsync</c>).
/// </summary>
public sealed class ConnectionStatusStore
{
    private readonly ConcurrentDictionary<string, ConnectionState> _states = new();

    /// <summary>Raised after any state change, on the caller's thread.</summary>
    public event EventHandler? Changed;

    /// <summary>Records the state of a subsystem (see <see cref="Subsystems"/> for keys).
    /// Raises <see cref="Changed"/> only when the value actually changed: on the 2026-09-20
    /// flight the TTS router re-published "Connected" after every utterance and each call
    /// re-rendered the whole web layout and the WPF window (112 events for 105 sentences).</summary>
    public void Set(string subsystem, ConnectionState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subsystem);
        var changed = true;
        _states.AddOrUpdate(
            subsystem,
            state,
            (_, previous) =>
            {
                changed = previous != state;
                return state;
            });

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Point-in-time copy of all known subsystem states, ordered by name.</summary>
    public IReadOnlyList<KeyValuePair<string, ConnectionState>> Snapshot()
        => [.. _states.OrderBy(pair => pair.Key)];
}
