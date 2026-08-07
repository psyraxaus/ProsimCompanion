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

            var app = new App(web.Services, url);
            app.InitializeComponent();
            var exitCode = app.Run();

            web.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();

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
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
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
