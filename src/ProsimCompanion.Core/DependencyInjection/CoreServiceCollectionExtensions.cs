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
        services.Configure<ChecklistOptions>(configuration.GetSection(ChecklistOptions.SectionName));
        services.Configure<BriefingOptions>(configuration.GetSection(BriefingOptions.SectionName));
        services.Configure<SayIntentionsOptions>(configuration.GetSection(SayIntentionsOptions.SectionName));
        services.Configure<CabinOptions>(configuration.GetSection(CabinOptions.SectionName));
        services.Configure<CompanyOptions>(configuration.GetSection(CompanyOptions.SectionName));
        services.Configure<VoicesOptions>(configuration.GetSection(VoicesOptions.SectionName));
        services.Configure<DayOptions>(configuration.GetSection(DayOptions.SectionName));
        services.Configure<WeatherOptions>(configuration.GetSection(WeatherOptions.SectionName));
        services.Configure<TechLogOptions>(configuration.GetSection(TechLogOptions.SectionName));
        services.Configure<LogbookOptions>(configuration.GetSection(LogbookOptions.SectionName));
        services.Configure<DebriefOptions>(configuration.GetSection(DebriefOptions.SectionName));
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
        services.AddSingleton<WeatherStore>();
        // Weather-provider chain — registration order of the array IS the tier order:
        // ActiveSky (file → API, the injected sim weather wins) → ProSim gateway METAR →
        // the SayIntentions store cache (never a network call).
        services.AddSingleton<Weather.ActiveSkyWxProvider>();
        services.AddSingleton<Weather.GatewayWxProvider>();
        services.AddSingleton<Weather.SayIntentionsStoreWxProvider>();
        services.AddSingleton<Weather.IWxProvider>(p => new Weather.CompositeWxProvider(
            [
                p.GetRequiredService<Weather.ActiveSkyWxProvider>(),
                p.GetRequiredService<Weather.GatewayWxProvider>(),
                p.GetRequiredService<Weather.SayIntentionsStoreWxProvider>(),
            ],
            p.GetRequiredService<WeatherStore>(),
            p.GetRequiredService<ILogger<Weather.CompositeWxProvider>>()));
        services.AddSingleton<ConnectionStatusStore>();
        services.AddSingleton<GsxDiagnosticsStore>();
        services.AddSingleton<AudioStatusStore>();
        services.AddSingleton<SpeechStatusStore>();
        services.AddSingleton<ArrivalMinimaStore>();
        services.AddSingleton<Aircraft.Ofp.OfpStore>();
        services.AddSingleton<LoadsheetStore>();
        services.AddSingleton<GroundOpsSignals>();
        services.AddSingleton<DisplayUnitService>();
        // Per-profile GSX settings (Prosim2GSX model): the active profile's stored block is
        // written over the live gsx section on activation.
        services.AddHostedService<Profiles.ProfileGsxApplier>();
        services.AddSingleton<Checklists.ChecklistService>();
        services.AddSingleton<Deice.DeiceHoldoverService>();
        services.AddSingleton<Aircraft.PassengerManifestService>();

        services.AddSingleton(provider => new JsonlEventLog(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ProsimCompanion",
                "sessions"),
            provider.GetRequiredService<ILogger<JsonlEventLog>>()));

        // Company day mode: persisted state, live view store and the summary composer (the
        // day service itself lives in the Speech project — it speaks).
        services.AddSingleton<Day.DayStateFile>();
        services.AddSingleton<Day.DayStatusStore>();
        services.AddSingleton<Day.IDaySummaryComposer, Day.DaySummaryComposer>();

        // Post-flight bookkeeping pillar: tech log & MEL, pilot logbook, session finalizer.
        // Finalization steps run in explicit Order (debrief 10 → logbook 20 → techlog 30),
        // so registration order here does not matter.
        services.AddSingleton<TechLog.TechLogService>();
        services.AddSingleton<TechLog.ITechLogService>(p => p.GetRequiredService<TechLog.TechLogService>());
        services.AddSingleton<Debrief.IDebriefFactExtractor, Debrief.DebriefFactExtractor>();
        services.AddSingleton<Logbook.LogbookService>();
        services.AddSingleton<Logbook.ILogbookService>(p => p.GetRequiredService<Logbook.LogbookService>());
        services.AddSingleton<Sessions.ISessionFinalizationStep>(p => p.GetRequiredService<Logbook.LogbookService>());
        services.AddSingleton<Sessions.ISessionFinalizationStep>(p => p.GetRequiredService<TechLog.TechLogService>());
        services.AddSingleton<Sessions.SessionFinalizer>();
        services.AddHostedService<Hosting.PostFlightBootstrapService>();

        // IFlightDataSource and IProsimDataRefs come from the Prosim project's registrations.
        services.AddSingleton<FlightStateEngine>();
        services.AddSingleton<IFlightPhaseSource>(p => p.GetRequiredService<FlightStateEngine>());
        // Arrival-gate workflow: Confirm queues, cruise auto-fires to GSX + SayIntentions
        // ATC, Send Now fires immediately. Both targets come from other pillars (Gsx and
        // Speech) and resolve as optional so a composition without them still starts.
        services.AddSingleton(p => new Gate.ArrivalGateCoordinator(
            p.GetRequiredService<IFlightPhaseSource>(),
            p.GetRequiredService<Aircraft.Ofp.OfpStore>(),
            p.GetService<IGsxGateControl>(),
            p.GetService<Gate.ISayIntentionsGateAssign>(),
            p.GetRequiredService<ILogger<Gate.ArrivalGateCoordinator>>()));
        services.AddSingleton<AircraftProfileService>();
        services.AddHostedService<CoreBootstrapService>();

        return services;
    }
}
