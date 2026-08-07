using Microsoft.Extensions.Hosting;

namespace ProsimCompanion.Speech.Day;

/// <summary>
/// Activates company day mode at startup. Separate from the other speech bootstraps so the
/// whole feature is one additive registration block (see WIRING-DAY.md) and nothing else
/// changes when it is absent — degrade, not fail.
/// </summary>
public sealed class DayBootstrapService : IHostedService
{
    private readonly CompanyDayService _day;

    public DayBootstrapService(CompanyDayService day)
    {
        ArgumentNullException.ThrowIfNull(day);
        _day = day;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _day.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _day.Dispose();
        return Task.CompletedTask;
    }
}
