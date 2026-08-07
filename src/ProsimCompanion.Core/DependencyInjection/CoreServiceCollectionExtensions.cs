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
        // Same binder-appends-to-defaults trap for the audio lists.
        services.Configure<AudioOptions>(o =>
        {
            o.AppMappings.Clear();
            o.ActiveAcps.Clear();
        });
        services.Configure<AudioOptions>(configuration.GetSection(AudioOptions.SectionName));
        services.PostConfigure<AudioOptions>(o =>
        {
            if (o.AppMappings.Count == 0)
            {
                o.AppMappings.AddRange(AudioOptions.DefaultAppMappings);
            }

            if (o.ActiveAcps.Count == 0)
            {
                o.ActiveAcps.Add(AcpSide.Captain);
            }
        });
        services.Configure<SpeechOptions>(configuration.GetSection(SpeechOptions.SectionName));
        services.Configure<BriefingOptions>(configuration.GetSection(BriefingOptions.SectionName));
        services.Configure<SayIntentionsOptions>(configuration.GetSection(SayIntentionsOptions.SectionName));
        services.Configure<CabinOptions>(configuration.GetSection(CabinOptions.SectionName));
        services.Configure<CompanyOptions>(configuration.GetSection(CompanyOptions.SectionName));
        // Same binder-appends-to-defaults trap for the SOP lists.
        services.Configure<SopOptions>(o =>
        {
            o.AltitudeCallouts.Clear();
            o.FlapPlacards.Clear();
            o.ApproachGates.Clear();
        });
        services.Configure<SopOptions>(configuration.GetSection(SopOptions.SectionName));
        services.PostConfigure<SopOptions>(o =>
        {
            if (o.AltitudeCallouts.Count == 0)
            {
                o.AltitudeCallouts.AddRange(SopOptions.DefaultAltitudeCallouts);
            }

            if (o.FlapPlacards.Count == 0)
            {
                o.FlapPlacards.AddRange(SopOptions.DefaultFlapPlacards);
            }

            if (o.ApproachGates.Count == 0)
            {
                o.ApproachGates.AddRange(SopOptions.DefaultApproachGates);
            }
        });
        services.Configure<AircraftProfilesOptions>(configuration.GetSection(AircraftProfilesOptions.SectionName));
        services.Configure<LoggingOptions>(configuration.GetSection(LoggingOptions.SectionName));
        services.Configure<FlightDataOptions>(configuration.GetSection(FlightDataOptions.SectionName));

        services.AddSingleton(new JsonSettingsFile(settingsFilePath));
        services.AddSingleton<ConnectionStatusStore>();
        services.AddSingleton<GsxDiagnosticsStore>();
        services.AddSingleton<AudioStatusStore>();
        services.AddSingleton<SpeechStatusStore>();
        services.AddSingleton<ArrivalMinimaStore>();
        services.AddSingleton<Aircraft.Ofp.OfpStore>();
        services.AddSingleton<LoadsheetStore>();
        services.AddSingleton<GroundOpsSignals>();
        services.AddSingleton<Checklists.ChecklistService>();
        services.AddSingleton<Deice.DeiceHoldoverService>();
        services.AddSingleton<Aircraft.PassengerManifestService>();

        services.AddSingleton(provider => new JsonlEventLog(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ProsimCompanion",
                "sessions"),
            provider.GetRequiredService<ILogger<JsonlEventLog>>()));

        // IFlightDataSource and IProsimDataRefs come from the Prosim project's registrations.
        services.AddSingleton<FlightStateEngine>();
        services.AddSingleton<IFlightPhaseSource>(p => p.GetRequiredService<FlightStateEngine>());
        services.AddSingleton<AircraftProfileService>();
        services.AddHostedService<CoreBootstrapService>();

        return services;
    }
}
