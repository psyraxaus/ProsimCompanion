using System.Globalization;
using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.DependencyInjection;
using ProsimCompanion.Gsx;
using ProsimCompanion.Prosim;
using ProsimCompanion.Sim;
using Serilog;

namespace ProsimCompanion.App;

/// <summary>
/// Composition root. One process, one DI container: an ASP.NET Core WebApplication owns the
/// container and hosted services; WPF runs on the STA main thread as a thin shell over it
/// (see docs/ARCHITECTURE.md).
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProsimCompanion",
            "logs");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
            .WriteTo.File(
                Path.Combine(logDirectory, "ProsimCompanion-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                formatProvider: CultureInfo.InvariantCulture)
            .CreateLogger();

        try
        {
            var web = BuildWebHost(args);
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

    private static WebApplication BuildWebHost(string[] args)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        });

        var settingsPath = Path.Combine(AppContext.BaseDirectory, "config", "settings.json");
        var settingsFile = new JsonSettingsFile(settingsPath);
        var previousVersion = SettingsMigrator.Migrate(settingsFile);
        if (previousVersion < SettingsMigrator.CurrentVersion)
        {
            Log.Information(
                "Settings migrated from version {From} to {To}",
                previousVersion,
                SettingsMigrator.CurrentVersion);
        }

        builder.Configuration.AddJsonFile(settingsPath, optional: true, reloadOnChange: true);

        builder.Services.AddSerilog();
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddCoreServices(builder.Configuration, settingsPath);
        builder.Services.AddProsimServices();
        builder.Services.AddSimServices();
        builder.Services.AddGsxServices();

        var webUi = builder.Configuration.GetSection(WebUiOptions.SectionName).Get<WebUiOptions>()
            ?? new WebUiOptions();
        var host = webUi.BindToAllInterfaces ? "0.0.0.0" : "localhost";
        builder.WebHost.UseUrls($"http://{host}:{webUi.Port}");

        var web = builder.Build();

        // Serves wwwroot, including the blazor.web.js copied there at build (see csproj) — a
        // WinExe host has no static-web-assets pipeline to provide it.
        web.UseStaticFiles();

        web.UseAntiforgery();
        web.MapRazorComponents<Web.App>().AddInteractiveServerRenderMode();
        return web;
    }

    private static string DisplayUrl(IServiceProvider services)
    {
        var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<WebUiOptions>>();
        return $"http://localhost:{options.Value.Port}";
    }
}
