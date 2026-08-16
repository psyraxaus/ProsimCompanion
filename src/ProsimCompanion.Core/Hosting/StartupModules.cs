using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ProsimCompanion.Core.Hosting;

/// <summary>
/// A feature module that starts with the host (campaign #87). Modules whose activation is
/// their construction (event wiring in the ctor) register via
/// <see cref="StartupModuleServiceCollectionExtensions.AddStartupModule{T}"/> without
/// implementing this; modules with explicit start-up work implement it and their
/// <see cref="Start"/> runs after construction. Replaces the bootstrap god-objects whose
/// constructor parameter lists existed only to force DI instantiation — the two highest-churn
/// files in the repo.
/// </summary>
public interface IStartupModule
{
    /// <summary>Explicit start-up work; called once, in registration order.</summary>
    void Start();
}

/// <summary>One registered module: resolved (and thereby activated) at host start.</summary>
public sealed record StartupModuleRegistration(string Name, Func<IServiceProvider, object> Activate);

public static class StartupModuleServiceCollectionExtensions
{
    /// <summary>Registers <typeparamref name="T"/> as a singleton AND as a startup module:
    /// the host resolves it at start (construction is activation) and calls
    /// <see cref="IStartupModule.Start"/> when implemented. Adding a feature module is one
    /// line in its own pillar's registration — no central bootstrap edits.</summary>
    public static IServiceCollection AddStartupModule<T>(this IServiceCollection services)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<T>();
        services.AddSingleton(new StartupModuleRegistration(
            typeof(T).Name,
            provider => provider.GetRequiredService<T>()));
        return services;
    }
}

/// <summary>
/// Starts every registered module in registration order, each behind its own guard: a module
/// that throws logs and stays disabled — it never blocks startup or the other modules
/// (degrade, not fail). At shutdown the activated modules are disposed in reverse order,
/// before the container teardown, so audio and timers go quiet early (the old speech
/// bootstrap's rule); a second container-side Dispose must stay harmless, as before.
/// Register AFTER every pillar so hosted transports (e.g. the GSX client) start first.
/// </summary>
public sealed class StartupModuleHost : IHostedService
{
    private readonly IReadOnlyList<StartupModuleRegistration> _modules;
    private readonly IServiceProvider _provider;
    private readonly ILogger<StartupModuleHost> _logger;
    private readonly List<object> _activated = [];

    public StartupModuleHost(
        IEnumerable<StartupModuleRegistration> modules,
        IServiceProvider provider,
        ILogger<StartupModuleHost> logger)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(logger);
        _modules = [.. modules];
        _provider = provider;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var module in _modules)
        {
            try
            {
                var instance = module.Activate(_provider);
                _activated.Add(instance);
                (instance as IStartupModule)?.Start();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Startup module {Module} failed to start — it stays disabled (degrade, not fail)",
                    module.Name);
            }
        }

        _logger.LogInformation("Started {Count} feature modules", _activated.Count);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        for (var i = _activated.Count - 1; i >= 0; i--)
        {
            try
            {
                (_activated[i] as IDisposable)?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Startup module {Module} dispose failed at shutdown",
                    _activated[i].GetType().Name);
            }
        }

        _activated.Clear();
        return Task.CompletedTask;
    }
}
