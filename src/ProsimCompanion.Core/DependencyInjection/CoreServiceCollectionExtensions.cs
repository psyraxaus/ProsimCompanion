using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Hosting;
using ProsimCompanion.Core.Profiles;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.DependencyInjection;

public static class CoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers the domain services shared by every surface: option bindings from
    /// config/settings.json, the settings write path, the state stores, the flight state engine
    /// and the session event log.
    /// </summary>
    public static IServiceCollection AddCoreServices(
        this IServiceCollection services,
        IConfiguration configuration,
        string settingsFilePath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsFilePath);

        services.Configure<WebUiOptions>(configuration.GetSection(WebUiOptions.SectionName));
        services.Configure<ProsimOptions>(configuration.GetSection(ProsimOptions.SectionName));
        // The config binder APPENDS array items to a list the options class already initialized
        // (round-4 smoke test: the departure order arrived doubled and every service triggered
        // twice). Clear list defaults before the file binds; restore them after when the file
        // omitted the key entirely.
        services.Configure<GsxOptions>(o => o.DepartureServices.Clear());
        services.Configure<GsxOptions>(configuration.GetSection(GsxOptions.SectionName));
        services.PostConfigure<GsxOptions>(o =>
        {
            if (o.DepartureServices.Count == 0)
            {
                o.DepartureServices.AddRange(GsxOptions.DefaultDepartureServices);
            }
        });
        services.Configure<AircraftProfilesOptions>(configuration.GetSection(AircraftProfilesOptions.SectionName));
        services.Configure<LoggingOptions>(configuration.GetSection(LoggingOptions.SectionName));
        services.Configure<FlightDataOptions>(configuration.GetSection(FlightDataOptions.SectionName));

        services.AddSingleton(new JsonSettingsFile(settingsFilePath));
        services.AddSingleton<ConnectionStatusStore>();
        services.AddSingleton<GsxDiagnosticsStore>();
        services.AddSingleton<Aircraft.Ofp.OfpStore>();
        services.AddSingleton<LoadsheetStore>();
        services.AddSingleton<GroundOpsSignals>();

        services.AddSingleton(provider => new JsonlEventLog(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ProsimCompanion",
                "sessions"),
            provider.GetRequiredService<ILogger<JsonlEventLog>>()));

        // IFlightDataSource and IProsimDataRefs come from the Prosim project's registrations.
        services.AddSingleton<FlightStateEngine>();
        services.AddSingleton<AircraftProfileService>();
        services.AddHostedService<CoreBootstrapService>();

        return services;
    }
}
