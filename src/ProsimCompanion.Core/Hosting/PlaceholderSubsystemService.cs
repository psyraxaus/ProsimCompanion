using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Hosting;

/// <summary>
/// Stand-in hosted service for a subsystem that has not been implemented yet. Marks the subsystem
/// as disconnected in the status store and logs which roadmap phase will deliver it. Each feature
/// project replaces its placeholder registration with the real service as its phase lands.
/// </summary>
public sealed class PlaceholderSubsystemService : BackgroundService
{
    private readonly ConnectionStatusStore _status;
    private readonly ILogger<PlaceholderSubsystemService> _logger;
    private readonly string _subsystem;
    private readonly string _roadmapPhase;

    public PlaceholderSubsystemService(
        ConnectionStatusStore status,
        ILogger<PlaceholderSubsystemService> logger,
        string subsystem,
        string roadmapPhase)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(subsystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(roadmapPhase);

        _status = status;
        _logger = logger;
        _subsystem = subsystem;
        _roadmapPhase = roadmapPhase;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _status.Set(_subsystem, ConnectionState.Disconnected);
        _logger.LogInformation(
            "{Subsystem} integration is not implemented yet — planned for {Phase} (docs/ROADMAP.md)",
            _subsystem,
            _roadmapPhase);
        return Task.CompletedTask;
    }
}
