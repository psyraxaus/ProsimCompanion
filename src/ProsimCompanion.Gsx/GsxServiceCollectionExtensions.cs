using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
        services.AddSingleton<Automation.GsxAutomationService>();
        services.AddSingleton<Core.State.IGsxDepartureControl>(provider => provider.GetRequiredService<Automation.GsxAutomationService>());
        services.AddSingleton<Sync.GsxProsimWriter>();
        services.AddSingleton<Sync.GsxRefuelSync>();
        services.AddSingleton<Sync.GsxBoardingSync>();
        services.AddSingleton<Sync.GsxGroundEquipmentService>();
        services.AddSingleton<Sync.GsxJetwayStairsService>();
        services.AddSingleton<Sync.GsxRepositionService>();
        services.AddSingleton<Sync.GsxGroundPrepCoordinator>();
        services.AddSingleton<Sync.ProsimNativeGsxGuard>();
        services.AddSingleton<Sync.GsxDoorService>();
        services.AddSingleton<Sync.GsxPushbackSequenceService>();
        services.AddSingleton<Sync.GsxArrivalService>();
        services.AddHostedService<GsxBootstrapService>();

        return services;
    }
}
