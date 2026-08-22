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

        // Every option section registers through AddOptionSection (campaign #84): one call
        // binds the section, applies the binder list-append fix, and records the section in
        // the registry the settings-defaults writer enumerates. The binder-appends-to-defaults
        // archaeology lives on OptionsListBinding.
        services.AddOptionSection<WebUiOptions>(configuration);
        services.AddOptionSection<ProsimOptions>(configuration);
        services.AddOptionSection<GsxOptions>(configuration);
        services.AddOptionSection<AudioOptions>(configuration);
        services.AddOptionSection<SpeechOptions>(configuration);
        services.AddOptionSection<ChecklistOptions>(configuration);
        services.AddOptionSection<SopOptions>(configuration);
        services.AddOptionSection<BriefingOptions>(configuration);
        services.AddOptionSection<McduOptions>(configuration);
        services.AddOptionSection<SayIntentionsOptions>(configuration);
        services.AddOptionSection<CabinOptions>(configuration);
        services.AddOptionSection<GroundCrewOptions>(configuration);
        services.AddOptionSection<AccentOptions>(configuration);
        services.AddOptionSection<CompanyOptions>(configuration);
        services.AddOptionSection<VoicesOptions>(configuration);
        services.AddOptionSection<DayOptions>(configuration);
        services.AddOptionSection<WeatherOptions>(configuration);
        // HTTP command API gate — bound here with every other section (it used to live in
        // App/Program.cs, which is how it escaped the defaults writer's notice pre-#84).
        services.AddOptionSection<CommandApiOptions>(configuration);
        // Read-only telemetry API (issue #94) — serves session/log files to the
        // flight-verification workflow.
        services.AddOptionSection<TelemetryApiOptions>(configuration);
        services.AddOptionSection<TechLogOptions>(configuration);
        services.AddOptionSection<LogbookOptions>(configuration);
        services.AddOptionSection<DebriefOptions>(configuration);
        services.AddOptionSection<FlightDataOptions>(configuration);
        services.AddOptionSection<LoggingOptions>(configuration);
        services.AddOptionSection<AircraftProfilesOptions>(configuration);
        services.AddOptionSection<UpdateCheckOptions>(configuration);
        services.AddOptionSection<PersonaOptions>(configuration);

        services.AddSingleton(new JsonSettingsFile(settingsFilePath));
        // The typed settings write path — pages and services write through this, never through
        // hand-written section/key strings.
        services.AddSingleton<SettingsWriter>();
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
        // Written by the Sim pillar's session monitor; read by session-gated automation and
        // the web UI. Stays at Empty (phase Unknown = hold) when the Sim pillar is absent.
        services.AddSingleton<SimSessionStore>();
        services.AddSingleton<GsxDiagnosticsStore>();
        services.AddSingleton<AudioStatusStore>();
        services.AddSingleton<SpeechStatusStore>();
        // LLM endpoint health (issue #66) — written by the speech pillar's LLM client and
        // re-probe; read by the web banner and the FO's one-shot offline advisory.
        services.AddSingleton<LlmHealthStore>();
        // User-content parse failures (issue #74) — written by the checklist/abnormal/command/
        // phrase/ATC-request loaders; read by the web warning banner.
        services.AddSingleton<ConfigProblemStore>();
        services.AddSingleton<ArrivalMinimaStore>();
        services.AddSingleton<Aircraft.Ofp.OfpStore>();
        services.AddSingleton<LoadsheetStore>();
        services.AddSingleton<GroundOpsSignals>();
        services.AddSingleton<GsxResyncState>();
        // The departure cycle (CONTEXT.md): shared owner of started/complete/turnaround/prep
        // flags so the GSX automation and the prep coordinator never reference each other
        // (campaign #78).
        services.AddSingleton<DepartureCycleState>();
        services.AddSingleton<DisplayUnitService>();
        // Spoken text (CONTEXT.md, campaign #81): pronunciation decided once — friendly
        // airport names come from the optional DFD-backed resolver when the Speech pillar
        // registered one, NATO-spelled codes otherwise.
        services.AddSingleton<Speech.ISpokenText>(provider =>
            new Speech.SpokenText(provider.GetService<Airports.IAirportNames>()));
        // Per-profile GSX settings (Prosim2GSX model): the active profile's stored block is
        // written over the live gsx section on activation.
        services.AddHostedService<Profiles.ProfileGsxApplier>();
        // The write half: the GSX Settings page mirrors the saved gsx section back into the
        // active profile's stored block after every save.
        services.AddSingleton<Profiles.ProfileGsxMirror>();
        services.AddSingleton<Checklists.ChecklistService>();
        services.AddSingleton<Deice.DeiceHoldoverService>();
        services.AddSingleton<Aircraft.PassengerManifestService>();

        services.AddSingleton(provider => new JsonlEventLog(
            UserDataPaths.Sessions,
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
        // The simulated clock as a value (issue #95): loadsheet timestamps and STD triggers
        // follow the sim's day, falling back to real UTC when the sim clock is not live.
        services.AddSingleton<Aircraft.SimClock>();
        services.AddSingleton<Aircraft.ISimClock>(p => p.GetRequiredService<Aircraft.SimClock>());
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

        // Update-available banner: GitHub releases check, silent offline, never blocks startup.
        services.AddSingleton<UpdateStore>();
        services.AddHostedService<Updates.UpdateCheckService>();

        return services;
    }
}
