using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProsimCompanion.App.Hosting;
using ProsimCompanion.App.Logging;
using ProsimCompanion.Audio;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.DependencyInjection;
using ProsimCompanion.Core.Logging;
using ProsimCompanion.Gsx;
using ProsimCompanion.Prosim;
using ProsimCompanion.Sim;
using ProsimCompanion.Speech;
using ProsimCompanion.Web;
using Serilog;

namespace ProsimCompanion.App;

/// <summary>
/// The app's service registrations, separated from the Kestrel/WPF/Serilog plumbing in
/// <see cref="Program"/> so a test can build the same container with
/// <c>ValidateOnBuild</c> (tests/App/ServiceCompositionTests). Every pillar's
/// <c>Add*Services()</c> runs here and nowhere else: a collaborator that one pillar forgot to
/// register used to surface only at host start, where the startup-module host logs the
/// failure and disables the module ("degrade, not fail" hid a missing registration on
/// 2026-10-19). Nothing here touches ProSim, SimConnect, GSX or the network — registration
/// only; construction happens when the host resolves.
/// </summary>
public static class ServiceComposition
{
    /// <param name="services">The host's service collection.</param>
    /// <param name="configuration">The bound settings (config/settings.json in the app; an
    /// in-memory collection in tests — every option class has safe defaults).</param>
    /// <param name="settingsPath">Path of the settings file the write path saves to.</param>
    /// <param name="levels">The live log-level switches, created before the logger.</param>
    /// <param name="logBuffer">The in-memory buffer the Logs page reads.</param>
    /// <param name="wireTrace">The protocol frame trace sink.</param>
    public static IServiceCollection AddProsimCompanionServices(
        this IServiceCollection services,
        IConfiguration configuration,
        string settingsPath,
        LoggingLevels levels,
        LogBufferStore logBuffer,
        IWireTrace wireTrace)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        ArgumentNullException.ThrowIfNull(levels);
        ArgumentNullException.ThrowIfNull(logBuffer);
        ArgumentNullException.ThrowIfNull(wireTrace);

        services.AddSerilog();
        services.AddRazorComponents().AddInteractiveServerComponents(circuit =>
        {
            // Remote tablets (iPad Safari especially) freeze the page and drop the circuit's
            // WebSocket whenever the tab backgrounds or the screen locks — routine in cockpit
            // use. Retain disconnected circuits well past the 3-minute default so a woken
            // client reconnects to its live session (unsaved drafts intact) instead of being
            // rejected and forced through a reload (#27).
            circuit.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(15);
        });
        services.AddCoreServices(configuration, settingsPath);
        services.AddProsimServices();
        services.AddSimServices();
        services.AddGsxServices();
        services.AddAudioServices();
        services.AddSpeechServices();
        // User drop-in themes come from the user config tree (ADR-0007); the built-ins are
        // embedded in the Web assembly.
        services.AddWebServices(
            UserConfigPaths.File("themes"),
            // Seeded user packs first (edits, added languages), the shipped defaults behind.
            [UserConfigPaths.Languages, Path.Combine(AppContext.BaseDirectory, "config", "lang")]);
        // The desktop shell's Open/Save/Folder dialogs for the settings pages' path fields,
        // offered only to a browser on this PC (option 3, 2026-10-13).
        services.AddSingleton<ProsimCompanion.Core.Hosting.INativeFilePicker, WpfFilePicker>();
        // Feature startup modules (campaign #87) — registered AFTER every pillar so hosted
        // transports (the GSX client, ProSim connection) start before the modules activate.
        services.AddHostedService<ProsimCompanion.Core.Hosting.StartupModuleHost>();
        // Verbose logging turns itself off after 24 h unless logging.keepVerbose (2026-10-17);
        // the Logs page reads the clock through the Core interface.
        services.AddSingleton<VerboseLoggingWatchdog>();
        services.AddSingleton<ProsimCompanion.Core.Logging.IVerboseLoggingStatus>(
            provider => provider.GetRequiredService<VerboseLoggingWatchdog>());
        services.AddHostedService(provider => provider.GetRequiredService<VerboseLoggingWatchdog>());

        services.AddSingleton(levels);
        services.AddSingleton(logBuffer);
        services.AddSingleton<IWireTrace>(wireTrace);

        // HTTP command API: the command registry itself (populated in Main after the host is
        // built — CommandsBootstrap needs the built provider to resolve seams). Its gate
        // options bind with every other section in AddCoreServices (campaign #84).
        services.AddSingleton<ProsimCompanion.Core.Commands.CommandRegistry>();

        // Airframe-damage debug probe (/api/debug/damage) — subscribes its SimVars lazily on
        // first request, so registering it costs nothing until the surface is actually used.
        services.AddSingleton<SimDamageProbe>();
        return services;
    }
}
