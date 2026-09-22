using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ProsimCompanion.App.Configuration;
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
/// Composition root. One process, one DI container: an ASP.NET Core WebApplication owns the
/// container and hosted services; WPF runs on the STA main thread as a thin shell over it
/// (see docs/ARCHITECTURE.md).
/// </summary>
public static class Program
{
    private static readonly JsonSerializerOptions SettingsReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [STAThread]
    public static int Main(string[] args)
    {
        // Single instance (roadmap Phase 7): a second launch activates the running window
        // and exits — two instances would fight over the web port and the SDK connection.
        using var singleInstance = SingleInstanceGuard.TryAcquire();
        if (singleInstance is null)
        {
            SingleInstanceGuard.SignalExistingInstance();
            return 0;
        }

        var logDirectory = UserDataPaths.Logs;

        var settingsPath = Path.Combine(AppContext.BaseDirectory, "config", "settings.json");
        var settingsFile = new JsonSettingsFile(settingsPath);
        EnsureAccessToken(settingsFile);
        var previousVersion = SettingsMigrator.Migrate(settingsFile);

        // The level switches and buffer exist before the logger so every line — including
        // startup — flows through them; settings changes retune the switches live.
        var levels = new LoggingLevels();
        levels.Apply(ReadLoggingOptions(settingsFile));
        var logBuffer = new LogBufferStore();
        Log.Logger = BuildLogger(levels, logBuffer, logDirectory);

        // Last-words logging (issue #46: a crash left zero trace). These cannot stop a native
        // fault, but any managed unhandled exception — including background threads and
        // finalized-task faults — writes a Fatal line (flushed) before the process dies.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Fatal(e.ExceptionObject as Exception,
                "Unhandled exception (terminating: {IsTerminating})", e.IsTerminating);
            Log.CloseAndFlush();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // Observed-and-logged: an unobserved task fault must never escalate to a crash.
            Log.Error(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };

        using var wireTrace = new WireTraceService(levels, logDirectory);

        try
        {
            if (previousVersion < SettingsMigrator.CurrentVersion)
            {
                Log.Information(
                    "Settings migrated from version {From} to {To}",
                    previousVersion,
                    SettingsMigrator.CurrentVersion);
            }

            // One-shot upgrade of a settings file that still holds a plain API key / token
            // (pre-DPAPI build, or a hand-edited key). Before the host binds the file so the
            // plain value is never read by the configuration provider; every later write
            // protects itself inside JsonSettingsFile.Update.
            if (SecretProtector.EnsureProtected(settingsFile))
            {
                Log.Information("Settings secrets are now DPAPI-protected for the current Windows user");
            }

            // One-shot predecessor config import (Prosim2GSX AppConfig.json, Prosim2FO
            // settings.json) — marker-guarded, before the host binds the settings file.
            using (var importLoggerFactory = new Serilog.Extensions.Logging.SerilogLoggerFactory(Log.Logger))
            {
                PredecessorConfigImporter.TryImportOnFirstRun(
                    settingsFile,
                    importLoggerFactory.CreateLogger("PredecessorImport"));

                // Mirror the shipped user-editable config (checklists, abnormals, commands,
                // phrases, ATC requests) into %LOCALAPPDATA%\ProsimCompanion\config with
                // keep-user-edits semantics (ADR-0007, issue #55). Must run before the host
                // builds — ChecklistService and friends read the user tree in their
                // constructors.
                var seedResult = UserConfigSeeder.Seed(
                    Path.Combine(AppContext.BaseDirectory, "config"),
                    UserConfigPaths.Root,
                    importLoggerFactory.CreateLogger("UserConfigSeeder"));
                Log.Information(
                    "User config at {Root}: {Seeded} seeded, {Updated} default(s) refreshed, {Kept} user-edited kept, {Current} current",
                    UserConfigPaths.Root, seedResult.Seeded, seedResult.Updated,
                    seedResult.KeptEdited, seedResult.Current);
            }

            var web = BuildWebHost(args, settingsPath, settingsFile, levels, logBuffer, wireTrace);

            // Self-document every registered option section in settings.json (missing keys
            // only; existing values are never touched). Runs after the host exists because the
            // section list IS the DI registry (campaign #84) — safe after binding, since a key
            // this writes is by definition one whose absence already bound to the same default.
            if (SettingsDefaultsWriter.EnsureDefaults(
                    settingsFile,
                    web.Services.GetRequiredService<ProsimCompanion.Core.Configuration.OptionSectionRegistry>()))
            {
                Log.Information("Settings file updated with newly available option defaults");
            }

            // Populate the named-command registry (web/API/StreamDeck seam). RegisterAll
            // resolves seams with GetService so an absent pillar's commands still exist and
            // answer "unavailable" — startup can never fail here.
            ProsimCompanion.Core.Commands.CommandsBootstrap.RegisterAll(
                web.Services.GetRequiredService<ProsimCompanion.Core.Commands.CommandRegistry>(),
                web.Services);

            // Retune log levels / wire trace whenever settings change (web UI or file edit).
            // Debounced (issue #76 item 3): the file watcher + options binder fire OnChange in
            // bursts (5 reloads in 16 s on the 2026-08-15 flight) — one settings save must
            // apply once. A 2 s quiet period, latest options win.
            var loggingMonitor = web.Services.GetRequiredService<IOptionsMonitor<LoggingOptions>>();
            var reloadGate = new object();
            LoggingOptions? pendingLoggingOptions = null;
            using var reloadDebounce = new System.Threading.Timer(_ =>
            {
                LoggingOptions? toApply;
                lock (reloadGate)
                {
                    toApply = pendingLoggingOptions;
                    pendingLoggingOptions = null;
                }
                if (toApply is null)
                {
                    return;
                }
                levels.Apply(toApply);
                Log.Information(
                    "Logging levels reloaded (default {Default}, wire trace {WireTrace})",
                    toApply.DefaultLevel,
                    toApply.WireTrace);
            });
            using var levelSubscription = loggingMonitor.OnChange(options =>
            {
                lock (reloadGate)
                {
                    pendingLoggingOptions = options;
                }
                reloadDebounce.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
            });

            web.Start();

            var url = DisplayUrl(web.Services);
            Log.Information("Web UI available at {Url}", url);

            // Keep any deployed gsx_handler.py scripts pointed at our actual port (the
            // predecessor's GsxHandlerSync rule — a changed port silently kills the bridge).
            using (var syncLoggerFactory = new Serilog.Extensions.Logging.SerilogLoggerFactory(Log.Logger))
            {
                GsxHandlerEndpoints.SyncHandlerPort(
                    web.Services.GetRequiredService<IOptionsMonitor<WebUiOptions>>().CurrentValue.Port,
                    syncLoggerFactory.CreateLogger("GsxHandlerSync"));
            }

            var app = new App(web.Services, url, singleInstance);
            app.InitializeComponent();
            var exitCode = app.Run();

            // Shutdown must TERMINATE (issue #32): StopAsync's timeout only signals a token —
            // one hosted service blocking in synchronous teardown (SDK disconnect, TTS, a
            // native VBVMR call) hung Main here forever, leaving a headless zombie that kept
            // the web port, the single-instance mutex and the VoiceMeeter client; the next
            // launch then couldn't start (or talked to the half-dead instance's UI). Audio
            // hands its targets back FIRST — hosted services stop in reverse order, so a
            // speech-side hang used to prevent the VoiceMeeter neutral-reset/logout from ever
            // running — then the host gets a bounded stop, and the process exits regardless.
            TryShutdownAudio(web.Services);
            try
            {
                if (!web.StopAsync(TimeSpan.FromSeconds(5)).Wait(TimeSpan.FromSeconds(10)))
                {
                    Log.Warning("Host shutdown exceeded 10 s — forcing process exit");
                }
            }
            catch (AggregateException ex)
            {
                Log.Warning(ex.GetBaseException(), "Host shutdown faulted — forcing process exit");
            }

            // ProSimSDK owns a foreground thread that cannot be joined; without an explicit exit
            // the process would linger after the UI closes (known from the predecessor apps).
            Log.CloseAndFlush();
            Environment.Exit(exitCode);
            return exitCode;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "ProsimCompanion terminated unexpectedly");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    /// <summary>Audio's clean hand-back (CoreAudio volumes restored / VoiceMeeter targets to
    /// 0 dB + VBVMR logout), bounded so a blocked native call cannot hang the exit; a second
    /// call from the hosted service's own StopAsync is a no-op (Shutdown is idempotent).</summary>
    private static void TryShutdownAudio(IServiceProvider services)
    {
        try
        {
            var shutdown = Task.Run(() => services.GetRequiredService<AudioControlService>().Shutdown());
            if (!shutdown.Wait(TimeSpan.FromSeconds(3)))
            {
                Log.Warning("Audio hand-back did not finish within 3 s — continuing shutdown");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Audio hand-back failed — continuing shutdown");
        }
    }

    private static Serilog.Core.Logger BuildLogger(
        LoggingLevels levels,
        LogBufferStore logBuffer,
        string logDirectory)
    {
        var configuration = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(levels.DefaultLevel)
            .Enrich.WithThreadId()
            .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
            .WriteTo.File(
                new CmTraceTextFormatter(),
                Path.Combine(logDirectory, "ProsimCompanion-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14)
            .WriteTo.Sink(new LogBufferSink(logBuffer));

        foreach (var (source, levelSwitch) in levels.SourceSwitches)
        {
            configuration.MinimumLevel.Override(source, levelSwitch);
        }

        return configuration.CreateLogger();
    }

    private static WebApplication BuildWebHost(
        string[] args,
        string settingsPath,
        JsonSettingsFile settingsFile,
        LoggingLevels levels,
        LogBufferStore logBuffer,
        WireTraceService wireTrace)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        });

        // Stock JSON file source plus DPAPI decryption of the registered secret paths
        // (SecretProtector); reloadOnChange keeps the web UI's saves live as before.
        builder.Configuration.Sources.Add(new ProtectedJsonConfigurationSource
        {
            Path = settingsPath,
            Optional = true,
            ReloadOnChange = true,
        });

        builder.Services.AddSerilog();
        builder.Services.AddRazorComponents().AddInteractiveServerComponents(circuit =>
        {
            // Remote tablets (iPad Safari especially) freeze the page and drop the circuit's
            // WebSocket whenever the tab backgrounds or the screen locks — routine in cockpit
            // use. Retain disconnected circuits well past the 3-minute default so a woken
            // client reconnects to its live session (unsaved drafts intact) instead of being
            // rejected and forced through a reload (#27).
            circuit.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(15);
        });
        builder.Services.AddCoreServices(builder.Configuration, settingsPath);
        builder.Services.AddProsimServices();
        builder.Services.AddSimServices();
        builder.Services.AddGsxServices();
        builder.Services.AddAudioServices();
        builder.Services.AddSpeechServices();
        // User drop-in themes come from the user config tree (ADR-0007); the built-ins are
        // embedded in the Web assembly.
        builder.Services.AddWebServices(UserConfigPaths.File("themes"));
        // Feature startup modules (campaign #87) — registered AFTER every pillar so hosted
        // transports (the GSX client, ProSim connection) start before the modules activate.
        builder.Services.AddHostedService<ProsimCompanion.Core.Hosting.StartupModuleHost>();

        builder.Services.AddSingleton(levels);
        builder.Services.AddSingleton(logBuffer);
        builder.Services.AddSingleton<IWireTrace>(wireTrace);

        // HTTP command API: the command registry itself (populated in Main after the host is
        // built — CommandsBootstrap needs the built provider to resolve seams). Its gate
        // options bind with every other section in AddCoreServices (campaign #84).
        builder.Services.AddSingleton<ProsimCompanion.Core.Commands.CommandRegistry>();

        // Airframe-damage debug probe (/api/debug/damage) — subscribes its SimVars lazily on
        // first request, so registering it costs nothing until the surface is actually used.
        builder.Services.AddSingleton<SimDamageProbe>();

        var webUi = builder.Configuration.GetSection(WebUiOptions.SectionName).Get<WebUiOptions>()
            ?? new WebUiOptions();
        var host = webUi.BindToAllInterfaces ? "0.0.0.0" : "localhost";
        builder.WebHost.UseUrls($"http://{host}:{webUi.Port}");

        var web = builder.Build();

        // LAN clients authenticate with the access token (QR onboarding); loopback always passes.
        web.UseMiddleware<LanTokenMiddleware>();

        // Serves wwwroot, including the blazor.web.js copied there at build (see csproj) — a
        // WinExe host has no static-web-assets pipeline to provide it.
        web.UseStaticFiles();

        web.UseAntiforgery();

        // HTTP command API + read-only status feed (both opt-in via the commandApi settings
        // section; 404 while disabled). The status feed is what the Stream Deck plugin polls.
        web.MapCommandApi();
        web.MapStatusApi();

        // Read-only telemetry feed for the flight-verification workflow (issue #94):
        // session event logs + log tails, on by default, 404 while disabled.
        web.MapTelemetryApi();

        // Read-only airframe-damage snapshot (blown-tyre wear state); rides the telemetry gate.
        web.MapSimDamageApi();

        // The pilot's uploaded airline logos for the header and the Appearance page.
        web.MapThemeLogoApi();

        // Boot id for the client-side server-restart watchdog (issue #97).
        web.MapAppBoot();

        // In-sim GSX handler bridge (gsx_handler.py): event push + VDGS flight info. Always
        // on — the script targets loopback, which the token middleware exempts.
        web.MapGsxHandlerApi();

        web.MapRazorComponents<Web.App>().AddInteractiveServerRenderMode();
        return web;
    }

    private static string DisplayUrl(IServiceProvider services)
    {
        var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<WebUiOptions>>();
        return $"http://localhost:{options.Value.Port}";
    }

    private static LoggingOptions ReadLoggingOptions(JsonSettingsFile settingsFile)
    {
        try
        {
            var section = settingsFile.Read()[LoggingOptions.SectionName];
            return section?.Deserialize<LoggingOptions>(SettingsReadOptions) ?? new LoggingOptions();
        }
        catch (JsonException)
        {
            return new LoggingOptions();
        }
    }

    /// <summary>Generates the LAN access token on first start so enabling LAN access later never
    /// finds an empty token. A stored token that no longer decrypts (settings.json copied from
    /// another PC/user) is regenerated too — it was machine-generated, so there is nothing for
    /// the user to re-enter; the new token reaches the tablet via the QR code as usual.</summary>
    private static void EnsureAccessToken(JsonSettingsFile settingsFile)
    {
        var webUi = settingsFile.Read()[WebUiOptions.SectionName];
        var stored = (string?)webUi?["accessToken"];
        if (!string.IsNullOrEmpty(stored)
            && SecretProtector.TryUnprotect(stored, out var existing)
            && !string.IsNullOrEmpty(existing))
        {
            return;
        }

        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        settingsFile.Update(root =>
            JsonSettingsFile.GetOrCreateSection(root, WebUiOptions.SectionName)["accessToken"] = token);
    }
}
