using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace ProsimCompanion.Core.Commands;

/// <summary>
/// Centralised dispatcher for named, typed command handlers — the single command seam shared by
/// the web UI, the HTTP command API and (later) the Stream Deck plugin, so validation and
/// outcome reporting live in one place instead of each surface poking services directly.
/// <para>
/// Naming convention (carried over from the predecessor): dotted lowercase area + camelCase
/// verb, e.g. <c>"gsx.forceNextService"</c>, <c>"checklists.advanceNext"</c>, <c>"minima.set"</c>.
/// </para>
/// <para>
/// Unlike the predecessor there is no WPF dispatcher marshalling: this app's stores and control
/// seams are UI-agnostic (plain thread-safe singletons), so handlers run wherever the caller is.
/// Registration uses a <see cref="ConcurrentDictionary{TKey,TValue}"/> so bundles can be wired
/// from any thread during startup; execution is lock-free.
/// </para>
/// </summary>
public sealed class CommandRegistry
{
    /// <summary>The typed delegate plus the metadata the generic HTTP endpoint needs: the
    /// request CLR type to deserialise into, and a boxed invoker so the endpoint does not have
    /// to close the generic <see cref="ExecuteAsync{TReq,TRes}"/> over per-command types.</summary>
    private sealed record Registration(
        Type RequestType,
        Delegate Handler,
        Func<object?, CancellationToken, Task<object?>> Invoke);

    private readonly ConcurrentDictionary<string, Registration> _handlers = new(StringComparer.Ordinal);

    /// <summary>Registered command names, sorted ordinally — for the diagnostics listing
    /// (<c>GET /api/commands</c>, later a /commands web page).</summary>
    public IReadOnlyList<string> Names
    {
        get
        {
            var names = _handlers.Keys.ToArray();
            Array.Sort(names, StringComparer.Ordinal);
            return names;
        }
    }

    /// <summary>
    /// Registers a handler with a typed request and response. Throws when the same name is
    /// registered twice — handler bundles are wired exactly once at startup, so a duplicate is
    /// a bug worth failing loudly on, not a case to silently last-writer-wins.
    /// </summary>
    /// <exception cref="InvalidOperationException">The name is already registered.</exception>
    public void Register<TReq, TRes>(string name, Func<TReq, CancellationToken, Task<TRes>> handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(handler);

        var registration = new Registration(
            typeof(TReq),
            handler,
            async (request, cancellationToken) =>
                await handler((TReq)request!, cancellationToken).ConfigureAwait(false));

        if (!_handlers.TryAdd(name, registration))
        {
            throw new InvalidOperationException(
                $"Command '{name}' is already registered — double-registration is a bug.");
        }
    }

    /// <summary>
    /// Executes a previously registered command with compile-time request/response types.
    /// The token lets HTTP callers pipe <c>HttpContext.RequestAborted</c> through.
    /// </summary>
    /// <exception cref="CommandNotFoundException">No handler carries this name.</exception>
    /// <exception cref="InvalidOperationException">The handler was registered with different
    /// request/response types — a caller/bundle mismatch, i.e. a bug.</exception>
    public async Task<TRes> ExecuteAsync<TReq, TRes>(
        string name,
        TReq request,
        CancellationToken cancellationToken = default)
    {
        if (!_handlers.TryGetValue(name, out var registration))
        {
            throw new CommandNotFoundException(name);
        }

        if (registration.Handler is not Func<TReq, CancellationToken, Task<TRes>> handler)
        {
            throw new InvalidOperationException(
                $"Command '{name}' is registered with a different signature than requested.");
        }

        return await handler(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes a command when the request/response types are only known at runtime — the seam
    /// the generic <c>POST /api/command/{name}</c> endpoint uses after deserialising the body
    /// into the type reported by <see cref="TryGetRequestType"/>. The request must be an
    /// instance of that type (or null for a fresh empty request).
    /// </summary>
    /// <exception cref="CommandNotFoundException">No handler carries this name.</exception>
    public Task<object?> ExecuteUntypedAsync(
        string name,
        object? request,
        CancellationToken cancellationToken = default)
        => _handlers.TryGetValue(name, out var registration)
            ? registration.Invoke(request, cancellationToken)
            : throw new CommandNotFoundException(name);

    /// <summary>True when a handler with the given name is registered — for diagnostics and
    /// conditional UI rendering.</summary>
    public bool IsRegistered(string name) => _handlers.ContainsKey(name);

    /// <summary>Reports the CLR request type a command was registered with, so a generic caller
    /// can deserialise an inbound JSON body before <see cref="ExecuteUntypedAsync"/>.</summary>
    public bool TryGetRequestType(string name, [NotNullWhen(true)] out Type? requestType)
    {
        if (_handlers.TryGetValue(name, out var registration))
        {
            requestType = registration.RequestType;
            return true;
        }

        requestType = null;
        return false;
    }
}
