using Microsoft.Extensions.Options;

namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// An <see cref="IOptionsMonitor{TOptions}"/> over one fixed instance — for code paths with
/// no configuration system behind them (the replay harness, tests, the engine's
/// defaults-only constructor). Never changes, so <see cref="OnChange"/> is a no-op.
/// </summary>
public sealed class FixedOptionsMonitor<TOptions>(TOptions value) : IOptionsMonitor<TOptions>
    where TOptions : class
{
    /// <inheritdoc />
    public TOptions CurrentValue { get; } = value ?? throw new ArgumentNullException(nameof(value));

    /// <inheritdoc />
    public TOptions Get(string? name) => CurrentValue;

    /// <inheritdoc />
    public IDisposable? OnChange(Action<TOptions, string?> listener) => null;
}
