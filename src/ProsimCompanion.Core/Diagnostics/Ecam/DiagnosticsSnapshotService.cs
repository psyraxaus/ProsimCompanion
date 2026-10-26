using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Core.Diagnostics.Ecam;

/// <summary>
/// The timer around <see cref="DiagnosticsSnapshotCore"/>: once per <see cref="DiagnosticsOptions.EcamTickMs"/>
/// (hot-reloaded, floored at <see cref="DiagnosticsOptions.MinEcamTickMs"/>) it runs the
/// collectors and publishes to the <see cref="DiagnosticsStore"/>. A tick that throws outside
/// a collector (it should not) is logged and the loop carries on; the pages keep the last
/// snapshot rather than going blank.
/// </summary>
public sealed class DiagnosticsSnapshotService : BackgroundService
{
    private readonly IReadOnlyList<IDiagnosticsCollector> _collectors;
    private readonly IDiagMemoSource _memo;
    private readonly DiagEventLog _events;
    private readonly DiagnosticsStore _store;
    private readonly IOptionsMonitor<DiagnosticsOptions> _options;
    private readonly ILogger<DiagnosticsSnapshotService> _logger;
    private readonly DiagnosticsSnapshotCore _core;

    public DiagnosticsSnapshotService(
        IEnumerable<IDiagnosticsCollector> collectors,
        IDiagMemoSource memo,
        DiagEventLog events,
        DiagnosticsStore store,
        IOptionsMonitor<DiagnosticsOptions> options,
        ILogger<DiagnosticsSnapshotService> logger)
    {
        ArgumentNullException.ThrowIfNull(collectors);
        ArgumentNullException.ThrowIfNull(memo);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _collectors = [.. collectors];
        _memo = memo;
        _events = events;
        _store = store;
        _options = options;
        _logger = logger;
        _core = new DiagnosticsSnapshotCore(logger);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Diagnostics pages: {Count} collector(s) on {Pages}",
            _collectors.Count,
            string.Join(", ", _collectors.Select(c => $"{c.GetType().Name}→{c.Page}")));
        _events.Push(new DiagEvent(DateTimeOffset.UtcNow, "SESSION START", DiagState.Normal));

        while (!stoppingToken.IsCancellationRequested)
        {
            Tick();
            var period = Math.Max(DiagnosticsOptions.MinEcamTickMs, _options.CurrentValue.EcamTickMs);
            try
            {
                await Task.Delay(period, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>One tick, exposed so a test (or the API) can force a refresh.</summary>
    public void Tick()
    {
        try
        {
            var next = _core.ProcessTick(DateTimeOffset.UtcNow, _collectors, _memo, _events, _store.Snapshot());
            _store.Publish(next);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Diagnostics tick failed; the pages keep the previous snapshot");
        }
    }
}
