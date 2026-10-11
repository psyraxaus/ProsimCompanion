using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Services;

namespace ProsimCompanion.Gsx.Sync;

/// <summary>
/// Mirrors the GSX de-icing crew's lifecycle edges (Requested / Active / Completed, as the
/// Remote API reports them) onto the Core <see cref="DeiceActivityStore"/> (issue #166), so
/// the Speech pillar can hold the pack-fault abnormals while the trucks spray and run the FO's
/// packs/ditching procedure on the edges without a reference to this pillar. The Remote API
/// edges are used, not <c>L:FSDT_GSX_DEICING_STATE</c>: on 2026-10-10 the LVAR went Active
/// 25 s after the API and Completed 43 s before it. Startup module: construction subscribes;
/// the host calls <see cref="Start"/>.
/// </summary>
public sealed class GsxDeiceActivityRelay : Core.Hosting.IStartupModule, IDisposable
{
    private readonly GsxServiceLifecycleTracker _lifecycle;
    private readonly DeiceActivityStore _activity;
    private readonly ILogger<GsxDeiceActivityRelay> _logger;

    public GsxDeiceActivityRelay(
        GsxServiceLifecycleTracker lifecycle,
        DeiceActivityStore activity,
        ILogger<GsxDeiceActivityRelay> logger)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(logger);
        _lifecycle = lifecycle;
        _activity = activity;
        _logger = logger;
    }

    public void Start() => _lifecycle.ServiceEvent += OnServiceEvent;

    public void Dispose() => _lifecycle.ServiceEvent -= OnServiceEvent;

    /// <summary>Service id tolerance as the ground-ops relay: GSX has shipped "Deice",
    /// "De-Ice" and "Deicing" spellings.</summary>
    public static bool IsDeiceService(string serviceId)
    {
        ArgumentNullException.ThrowIfNull(serviceId);
        return serviceId.Replace("-", "").Contains("deic", StringComparison.OrdinalIgnoreCase);
    }

    public static DeiceStage? StageFor(GsxServiceLifecycleEvent lifecycleEvent) => lifecycleEvent switch
    {
        GsxServiceLifecycleEvent.Requested => DeiceStage.Requested,
        GsxServiceLifecycleEvent.Active => DeiceStage.Active,
        GsxServiceLifecycleEvent.Completed => DeiceStage.Completed,
        _ => null,
    };

    private void OnServiceEvent(string serviceId, GsxServiceLifecycleEvent lifecycleEvent)
    {
        try
        {
            if (!IsDeiceService(serviceId) || StageFor(lifecycleEvent) is not { } stage)
            {
                return;
            }

            _activity.SetStage(stage);
            _logger.LogInformation("GSX de-icing activity: {Stage} ({Service} {Event})", stage, serviceId, lifecycleEvent);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "De-icing activity relay failed");
        }
    }
}
