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

    /// <summary>Records the state of a subsystem (see <see cref="Subsystems"/> for keys).</summary>
    public void Set(string subsystem, ConnectionState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subsystem);
        _states[subsystem] = state;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Point-in-time copy of all known subsystem states, ordered by name.</summary>
    public IReadOnlyList<KeyValuePair<string, ConnectionState>> Snapshot()
        => [.. _states.OrderBy(pair => pair.Key)];
}
