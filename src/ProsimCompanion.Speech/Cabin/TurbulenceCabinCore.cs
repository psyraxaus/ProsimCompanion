using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;

namespace ProsimCompanion.Speech.Cabin;

/// <summary>What the cabin does on one turbulence tick.</summary>
public enum TurbulenceCabinAction
{
    None,

    /// <summary>FO: "seat belt signs, captain?"</summary>
    SeatbeltAdvice,

    /// <summary>Purser call + the captain's question "secure the cabin?".</summary>
    PurserCall,

    /// <summary>Purser, unprompted at Severe: "cabin crew are seated".</summary>
    SevereReport,

    /// <summary>Purser, 30–90 s after the captain's yes: "the cabin is secured".</summary>
    SecuredReport,

    /// <summary>Purser, after the episode: "cabin's back to normal".</summary>
    BackToNormal,
}

/// <summary>One tick's inputs.</summary>
/// <param name="Level">The settled ride level.</param>
/// <param name="EpisodeActive">From the first Light-or-worse until two minutes below Light.</param>
/// <param name="EpisodeEnded">This tick saw the episode-ended edge.</param>
/// <param name="SeatbeltSignsOn">The sign switch reads ON (mode 1; AUTO is not on in cruise).</param>
/// <param name="Airborne">An airborne phase.</param>
/// <param name="NowUtc">Clock.</param>
public sealed record TurbulenceCabinSample(
    TurbulenceLevel Level,
    bool EpisodeActive,
    bool EpisodeEnded,
    bool SeatbeltSignsOn,
    bool Airborne,
    DateTimeOffset NowUtc);

/// <summary>
/// The pure half of the cabin's turbulence reactions (issue #186): once-per-episode latches for
/// the FO's seat-belt advice, the purser's call, the Severe report and the back-to-normal
/// report, plus the timer the captain's yes starts. One action per tick, in priority order;
/// the shell speaks it. Clock in the sample.
/// </summary>
public sealed class TurbulenceCabinCore
{
    private bool _seatbeltAdviceDone;
    private bool _purserCallDone;
    private bool _severeReportDone;
    private bool _episodeHadReaction;
    private DateTimeOffset? _securedDueAtUtc;

    /// <summary>The wait after the captain's yes: 30–90 s, drawn once per yes.</summary>
    public static TimeSpan SecuredDelay(Func<double> random)
    {
        ArgumentNullException.ThrowIfNull(random);
        return TimeSpan.FromSeconds(30 + Math.Clamp(random(), 0, 1) * 60);
    }

    /// <summary>The report the captain's yes schedules; null until then or once reported.</summary>
    public DateTimeOffset? SecuredDueAtUtc => _securedDueAtUtc;

    /// <summary>The captain said yes: the purser seats the crew now and reports in a while.</summary>
    public void OnSecureAccepted(DateTimeOffset nowUtc, TimeSpan delay)
    {
        _securedDueAtUtc = nowUtc + delay;
        _episodeHadReaction = true;
    }

    /// <summary>One tick; the first applicable action.</summary>
    public TurbulenceCabinAction Evaluate(TurbulenceCabinSample sample, CabinOptions options)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(options);

        if (sample.EpisodeEnded)
        {
            var hadReaction = _episodeHadReaction;
            ResetEpisode();
            return hadReaction && options.TurbulencePurserCall ? TurbulenceCabinAction.BackToNormal : TurbulenceCabinAction.None;
        }

        if (!sample.EpisodeActive && !sample.EpisodeEnded)
        {
            // Nothing stands; a stale "secured" timer from a reset must not fire later.
            if (sample.Level == TurbulenceLevel.None)
            {
                return TurbulenceCabinAction.None;
            }
        }

        if (_securedDueAtUtc is { } due && sample.NowUtc >= due)
        {
            _securedDueAtUtc = null;
            return TurbulenceCabinAction.SecuredReport;
        }

        if (!sample.Airborne || sample.Level == TurbulenceLevel.None)
        {
            return TurbulenceCabinAction.None;
        }

        if (options.TurbulencePurserCall && sample.Level >= TurbulenceLevel.Severe && !_severeReportDone)
        {
            _severeReportDone = true;
            _purserCallDone = true; // the crew is seated — no question to ask any more
            _securedDueAtUtc = null;
            _episodeHadReaction = true;
            return TurbulenceCabinAction.SevereReport;
        }

        if (options.TurbulencePurserCall && sample.Level >= TurbulenceLevel.Moderate && !_purserCallDone)
        {
            _purserCallDone = true;
            _episodeHadReaction = true;
            return TurbulenceCabinAction.PurserCall;
        }

        if (options.TurbulenceSeatbeltAdvice && !_seatbeltAdviceDone && !sample.SeatbeltSignsOn)
        {
            _seatbeltAdviceDone = true;
            return TurbulenceCabinAction.SeatbeltAdvice;
        }

        return TurbulenceCabinAction.None;
    }

    /// <summary>A new session / flight cycle.</summary>
    public void Reset() => ResetEpisode();

    private void ResetEpisode()
    {
        _seatbeltAdviceDone = false;
        _purserCallDone = false;
        _severeReportDone = false;
        _episodeHadReaction = false;
        _securedDueAtUtc = null;
    }
}
