using ProsimCompanion.Core.Hosting;

namespace ProsimCompanion.Speech.Day;

/// <summary>
/// Raises the duty-day-ended signal of a day that was abandoned at app start (issue #239).
/// <see cref="CompanyDayService"/> starts as a hosted service (<see cref="DayBootstrapService"/>),
/// before the startup modules run; the subscribers of
/// <see cref="Core.Day.DutyDaySignals.DayEnded"/> in the Core pillar (the leg boundary, the
/// flight-state snapshot) are startup modules registered before the Speech pillar's. This
/// module is registered in the Speech pillar, so by the time it starts they are listening.
/// With no day abandoned at start it does nothing.
/// </summary>
public sealed class DayAbandonSignalModule : IStartupModule
{
    private readonly CompanyDayService _day;

    public DayAbandonSignalModule(CompanyDayService day)
    {
        ArgumentNullException.ThrowIfNull(day);
        _day = day;
    }

    public void Start() => _day.RaisePendingDayEnded();
}
