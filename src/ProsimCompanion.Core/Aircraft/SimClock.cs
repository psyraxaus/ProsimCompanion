namespace ProsimCompanion.Core.Aircraft;

/// <summary>
/// The simulated clock as a value (issue #95). The EFB header already showed sim time
/// (issue #71); this seam gives TIMESTAMP consumers — the loadsheet footer, its STD-offset
/// gating — the same clock, because a pilot simulating 02Z at a real-world 10Z must see 02Z
/// on paperwork and have schedule triggers fire against the simulated day.
/// </summary>
public interface ISimClock
{
    /// <summary>The current simulated UTC instant, or null while the sim clock is not live
    /// (ProSim absent, datarefs unregistered, or pushes gone stale).</summary>
    DateTimeOffset? SimUtcNow { get; }

    /// <summary>The simulated instant when live, else the real UTC clock — the degrade-not-
    /// fail read every timestamp consumer should use.</summary>
    DateTimeOffset UtcNowOrReal { get; }
}

/// <summary>
/// Live <see cref="ISimClock"/> over the ProSim push read model: time-of-day from
/// <c>simulator.zuluTime</c>, date from <c>simulator.time</c> (only its DATE — that ref's
/// time-of-day component has never been verified as local vs zulu, and the zulu ref is
/// unambiguous). Freshness mirrors the header clock's rule: the refs push about every 2 s,
/// so a value older than 10 s means the connection died and the clock falls back to real UTC
/// rather than freezing at the last simulated minute.
/// </summary>
public sealed class SimClock : ISimClock, IDisposable
{
    private static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(10);

    private readonly IDataRefSubscription<TimeSpan> _zuluTime;
    private readonly IDataRefSubscription<DateTime> _simDate;

    public SimClock(IProsimDataRefs dataRefs)
    {
        ArgumentNullException.ThrowIfNull(dataRefs);
        _zuluTime = dataRefs.Subscribe(ProsimDataRefNames.ZuluTime);
        _simDate = dataRefs.Subscribe(ProsimDataRefNames.SimulatorTime);
    }

    /// <inheritdoc />
    public DateTimeOffset? SimUtcNow
    {
        get
        {
            var zulu = _zuluTime;
            if (zulu.RawValue is null || zulu.IsStale
                || zulu.LastUpdatedUtc is not { } updated
                || DateTimeOffset.UtcNow - updated > FreshFor)
            {
                return null;
            }

            if (SimClockFormat.ToTimeOfDay(zulu.RawValue) is not { } timeOfDay)
            {
                return null;
            }

            // No sim date = still a usable clock: the zulu time on today's real date. The
            // date ref is the refinement (overnight sims, date-shifted flights), not a gate.
            var date = SimClockFormat.TryGetDate(_simDate.RawValue) ?? DateTime.UtcNow.Date;
            return new DateTimeOffset(date.Add(timeOfDay), TimeSpan.Zero);
        }
    }

    /// <inheritdoc />
    public DateTimeOffset UtcNowOrReal => SimUtcNow ?? DateTimeOffset.UtcNow;

    public void Dispose()
    {
        _zuluTime.Dispose();
        _simDate.Dispose();
    }
}
