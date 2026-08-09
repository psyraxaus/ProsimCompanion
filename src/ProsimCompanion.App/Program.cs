using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
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

        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProsimCompanion",
            "logs");

        var settingsPath = Path.Combine(AppContext.BaseDirectory, "config", "settings.json");
        var settingsFile = new JsonSettingsFile(settingsPath);
        EnsureAccessToken(settingsFile);
        var previousVersion = SettingsMigrator.Migrate(settingsFile);
        var defaultsAdded = SettingsDefaultsWriter.EnsureDefaults(settingsFile);

        // The level switches and buffer exist before the logger so every line — including
        // startup — flows through them; settings changes retune the switches live.
        var levels = new LoggingLevels();
        levels.Apply(ReadLoggingOptions(settingsFile));
        var logBuffer = new LogBufferStore();
        Log.Logger = BuildLogger(levels, logBuffer, logDirectory);

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

            if (defaultsAdded)
            {
                Log.Information("Settings file updated with newly available option defaults");
            }

            // One-shot predecessor config import (Prosim2GSX AppConfig.json, Prosim2FO
            // settings.json) — marker-guarded, before the host binds the settings file.
            using (var importLoggerFactory = new Serilog.Extensions.Logging.SerilogLoggerFactory(Log.Logger))
            {
                PredecessorConfigImporter.TryImportOnFirstRun(
                    settingsFile,
                    importLoggerFactory.CreateLogger("PredecessorImport"));
            }

            var web = BuildWebHost(args, settingsPath, settingsFile, levels, logBuffer, wireTrace);

            // Populate the named-command registry (web/API/StreamDeck seam). RegisterAll
            // resolves seams with GetService so an absent pillar's commands still exist and
            // answer "unavailable" — startup can never fail here.
            ProsimCompanion.Core.Commands.CommandsBootstrap.RegisterAll(
                web.Services.GetRequiredService<ProsimCompanion.Core.Commands.CommandRegistry>(),
                web.Services);

            // Retune log levels / wire trace whenever settings change (web UI or file edit).
            var loggingMonitor = web.Services.GetRequiredService<IOptionsMonitor<LoggingOptions>>();
            using var levelSubscription = loggingMonitor.OnChange(options =>
            {
                levels.Apply(options);
                Log.Information(
                    "Logging levels reloaded (default {Default}, wire trace {WireTrace})",
                    options.DefaultLevel,
                    options.WireTrace);
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

        builder.Configuration.AddJsonFile(settingsPath, optional: true, reloadOnChange: true);

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
        builder.Services.AddWebServices(
            Path.Combine(AppContext.BaseDirectory, "config", "themes"));

        builder.Services.AddSingleton(levels);
        builder.Services.AddSingleton(logBuffer);
        builder.Services.AddSingleton<IWireTrace>(wireTrace);

        // HTTP command API: gate options + the registry itself (populated in Main after the
        // host is built — CommandsBootstrap needs the built provider to resolve seams).
        builder.Services.Configure<CommandApiOptions>(
            builder.Configuration.GetSection(CommandApiOptions.SectionName));
        builder.Services.AddSingleton<ProsimCompanion.Core.Commands.CommandRegistry>();

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
    /// finds an empty token.</summary>
    private static void EnsureAccessToken(JsonSettingsFile settingsFile)
    {
        var webUi = settingsFile.Read()[WebUiOptions.SectionName];
        if (!string.IsNullOrEmpty((string?)webUi?["accessToken"]))
        {
            return;
        }

        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        settingsFile.Update(root =>
            JsonSettingsFile.GetOrCreateSection(root, WebUiOptions.SectionName)["accessToken"] = token);
    }
}
