using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Moq;
using ProsimCompanion.App;
using ProsimCompanion.App.Logging;
using ProsimCompanion.Core.Hosting;
using ProsimCompanion.Core.Logging;
using Xunit;
using Xunit.Abstractions;

namespace ProsimCompanion.Core.Tests.App;

/// <summary>
/// DI composition guard (2026-10-19). A refactor registered a module's new collaborators for
/// one pillar and forgot four of them for a sibling; nothing caught it, because the
/// startup-module host resolves each module behind its own guard and a module whose
/// constructor dependencies are missing is logged and disabled — "degrade, not fail" hid the
/// bug. This test builds the container exactly as the app does (<see cref="ServiceComposition"/>
/// is the one registration path, called from Program.cs) with <c>ValidateOnBuild</c>, so every
/// constructor-registered type must have resolvable dependencies, then resolves every hosted
/// service and every startup module — the factory-registered ones ValidateOnBuild cannot see —
/// without starting any of them. Constructors must not connect to anything; a constructor
/// that does belongs in StartAsync/Start, not in a mock here.
/// </summary>
public sealed class ServiceCompositionTests
{
    private readonly ITestOutputHelper _output;

    public ServiceCompositionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static ServiceCollection ComposeAsTheAppDoes()
    {
        // Empty settings: every option class must bind to safe defaults (CLAUDE.md), so the
        // composition is validated the way a first start with no settings.json sees it.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var services = new ServiceCollection();
        // What WebApplication.CreateBuilder registers before Program.cs adds anything and the
        // Blazor server registrations depend on: the configuration, the (web) host environment
        // and the application lifetime. Nothing of ours depends on them (no src reference).
        services.AddSingleton<IConfiguration>(configuration);
        var environment = new TestHostEnvironment();
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddSingleton<IWebHostEnvironment>(environment);
        services.AddSingleton(Mock.Of<IHostApplicationLifetime>());
        services.AddProsimCompanionServices(
            configuration,
            Path.Combine(Path.GetTempPath(), "ProsimCompanion.Tests", "composition", "settings.json"),
            new LoggingLevels(),
            new LogBufferStore(),
            Mock.Of<IWireTrace>());
        return services;
    }

    private static ServiceProvider BuildValidated(IServiceCollection services)
        => services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

    /// <summary>Tears the container down the way the host would (DisposeAsync — the ProSim
    /// dataref service is IAsyncDisposable only). A fault here is a disposal bug, not a
    /// composition one, so it is written to the test output instead of failing the test: the
    /// app itself never disposes its container (Program.cs ends with Environment.Exit), and a
    /// forwarded registration (<c>AddSingleton&lt;I&gt;(p => p.GetRequiredService&lt;T&gt;())</c>)
    /// makes the container dispose the same instance once per registration, which a
    /// non-idempotent Dispose rejects (CrewHailService on 2026-10-19).</summary>
    private async Task DisposeToleratingDisposalFaultsAsync(ServiceProvider provider)
    {
        try
        {
            await provider.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            _output.WriteLine($"Container disposal fault (not a composition failure): {ex.Message}");
        }
    }

    [Fact]
    public async Task EveryRegistration_HasResolvableConstructorDependencies()
    {
        var services = ComposeAsTheAppDoes();

        // ValidateOnBuild throws one AggregateException naming every unresolvable dependency
        // ("Unable to resolve service for type 'X' while attempting to activate 'Y'").
        var provider = BuildValidated(services);
        await DisposeToleratingDisposalFaultsAsync(provider).ConfigureAwait(true);

        _output.WriteLine($"{services.Count} registrations validated");
        Assert.True(services.Count > 300, $"only {services.Count} registrations — is the composition still complete?");
    }

    [Fact]
    public async Task EveryHostedService_Resolves_WithoutStarting()
    {
        var provider = BuildValidated(ComposeAsTheAppDoes());
        try
        {
            using var scope = provider.CreateScope();

            var hosted = scope.ServiceProvider.GetServices<IHostedService>().ToList();

            _output.WriteLine($"{hosted.Count} hosted services resolved");
            Assert.NotEmpty(hosted);
            Assert.Contains(hosted, h => h is StartupModuleHost);
            Assert.Contains(hosted, h => h is VerboseLoggingWatchdog);
        }
        finally
        {
            await DisposeToleratingDisposalFaultsAsync(provider).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task EveryStartupModule_Resolves_WithoutStarting()
    {
        var provider = BuildValidated(ComposeAsTheAppDoes());
        try
        {
            using var scope = provider.CreateScope();
            var registrations = scope.ServiceProvider.GetServices<StartupModuleRegistration>().ToList();
            Assert.NotEmpty(registrations);

            // The same activation the StartupModuleHost performs, minus its catch — and minus
            // IStartupModule.Start, which is where connections and timers belong.
            var failures = new List<string>();
            foreach (var registration in registrations)
            {
                try
                {
                    registration.Activate(scope.ServiceProvider);
                }
                catch (Exception ex)
                {
                    failures.Add($"{registration.Name}: {ex.GetBaseException().Message}");
                }
            }

            _output.WriteLine($"{registrations.Count} startup modules resolved");
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }
        finally
        {
            await DisposeToleratingDisposalFaultsAsync(provider).ConfigureAwait(true);
        }
    }

    /// <summary>The web host's environment, as Kestrel would describe this test run: no
    /// wwwroot, the test output directory as content root.</summary>
    private sealed class TestHostEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "ProsimCompanion.Core.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
