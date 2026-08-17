using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProsimCompanion.Core.Hosting;
using ProsimCompanion.Gsx.Services;

namespace ProsimCompanion.Gsx;

public static class GsxServiceCollectionExtensions
{
    /// <summary>
    /// Registers GSX Pro connectivity: the Couatl Remote API v2 client (and, as Phase 2
    /// progresses, the menu intent framework, gate selection and the ground automation state
    /// machine on top of it). Spec: docs/integrations/gsx-remote-api.md.
    /// </summary>
    public static IServiceCollection AddGsxServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<GsxRemoteApiClient>();
        services.AddSingleton<IGsxRemoteApi>(provider => provider.GetRequiredService<GsxRemoteApiClient>());
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<GsxRemoteApiClient>());
        services.AddSingleton<GsxServiceLifecycleTracker>();
        services.AddSingleton<Menu.GsxMenuIntentExecutor>();
        services.AddSingleton<Menu.GsxQuestionDispatcher>();
        services.AddSingleton<Menu.GsxQuestionCatalog>();
        services.AddSingleton<Gate.GsxGateSelectionService>();
        services.AddSingleton<Core.State.IGsxGateControl>(provider => provider.GetRequiredService<Gate.GsxGateSelectionService>());
        // The trigger slot (CONTEXT.md): the single serialized service.trigger path shared by
        // every sender — sequencer, on-demand commands, arrival, pushback, jetway/stairs.
        services.AddSingleton<Automation.GsxTriggerSlot>();
        services.AddSingleton<Automation.IGsxTriggerSlot>(provider => provider.GetRequiredService<Automation.GsxTriggerSlot>());
        services.AddSingleton<Automation.GsxAutomationService>();
        services.AddSingleton<Core.State.IGsxDepartureControl>(provider => provider.GetRequiredService<Automation.GsxAutomationService>());
        // One flight-plan rule for the sequencer gate AND the on-demand path (ADR-0006).
        services.AddSingleton<Automation.GsxFlightPlanMonitor>();
        services.AddSingleton<Automation.IGsxFlightPlanStatus>(provider => provider.GetRequiredService<Automation.GsxFlightPlanMonitor>());
        // On-demand per-service calls (gsx.request*/gsx.retract* commands, voice) — routed
        // through the automation's single serialized trigger slot, never a second writer.
        services.AddSingleton<GsxServiceControl>();
        services.AddSingleton<Core.State.IGsxServiceControl>(provider => provider.GetRequiredService<GsxServiceControl>());
        services.AddSingleton<Sync.GsxProsimWriter>();
        // Sync modules are startup modules (campaign #87): construction is their activation
        // (event wiring in the ctor), driven by the StartupModuleHost — never by a bootstrap
        // constructor parameter list.
        services.AddStartupModule<Sync.GsxRefuelSync>();
        services.AddStartupModule<Sync.GsxBoardingSync>();
        services.AddStartupModule<Sync.GsxGroundEquipmentService>();
        services.AddStartupModule<Sync.GsxJetwayStairsService>();
        services.AddStartupModule<Sync.GsxRepositionService>();
        services.AddSingleton<Sync.GsxGateAnchorService>();
        services.AddStartupModule<Sync.GsxGroundPrepCoordinator>();
        services.AddSingleton<Sync.IGsxGroundPrepStatus>(provider => provider.GetRequiredService<Sync.GsxGroundPrepCoordinator>());
        services.AddStartupModule<Sync.ProsimNativeGsxGuard>();
        services.AddStartupModule<Sync.GsxDoorService>();
        services.AddStartupModule<Sync.GsxPushbackSequenceService>();
        services.AddStartupModule<Sync.GsxArrivalService>();
        services.AddStartupModule<Sync.GsxGroundOpsSignalRelay>();
        // Startup resync (issue #30): tracking LVARs + dataref evidence seed the lifecycle
        // latches after an app restart mid-turnaround; the sequencer holds until assessed.
        services.AddStartupModule<Sync.GsxStartupResyncService>();
        // Cold-and-dark verification (issue #63): once per session, after the resync verdict
        // distinguishes a fresh departure from a turnaround. The Core control seam is how the
        // web button and the voice command trigger an on-demand re-check (issue #92).
        services.AddStartupModule<Sync.AircraftStateCheckService>();
        services.AddSingleton<Core.State.IAircraftStateCheckControl>(
            provider => provider.GetRequiredService<Sync.AircraftStateCheckService>());
        services.AddHostedService<GsxBootstrapService>();

        return services;
    }
}
