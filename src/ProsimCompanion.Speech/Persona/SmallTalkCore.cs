using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;

namespace ProsimCompanion.Speech.Persona;

/// <summary>The four families of cruise small talk (issue #195): each a prompt family for
/// the model and a deterministic bank line as the floor.</summary>
public enum SmallTalkKind
{
    /// <summary>"Coffee, captain?" — a captain's yes/no; yes has the purser send something up.</summary>
    Offer,

    /// <summary>Something about what is outside: a town, the sun, the cloud deck.</summary>
    View,

    /// <summary>The destination: "do you know {city}?" — a free-text reply, one follow-up.</summary>
    Layover,

    /// <summary>The FO about themselves, from the roster record and nothing else.</summary>
    Crew,
}

/// <summary>One scheduler tick's inputs — sampled by the shell, judged by the core.</summary>
/// <param name="Live">The flight-live gate (issue #114).</param>
/// <param name="AltitudeMslFt">Baro altitude; the sterile ceiling is MSL (the arbiter's own rule).</param>
/// <param name="AltitudeValid">False while the sample is stale — then nothing is said.</param>
/// <param name="AbnormalOpen">An ECAM procedure is latched as detected.</param>
/// <param name="QuestionOpen">Any captain's question stands.</param>
/// <param name="Turbulence">The ride level; Moderate or worse holds the chatter.</param>
/// <param name="LastFoSpeechUtc">When the FO (or anyone) last spoke; null = nothing yet.</param>
/// <param name="NextRegionFactDueUtc">The region facts' next due time; null = none scheduled.</param>
/// <param name="Chattiness">The effective chattiness (0 once "quiet please" was said).</param>
/// <param name="Sterile">The sterile rule's verdict for this phase/altitude (belt; the altitude gate is braces).</param>
public sealed record SmallTalkTick(
    FlightPhase Phase,
    bool Live,
    DateTimeOffset NowUtc,
    double AltitudeMslFt,
    bool AltitudeValid,
    bool AbnormalOpen,
    bool QuestionOpen,
    TurbulenceLevel Turbulence,
    DateTimeOffset? LastFoSpeechUtc,
    DateTimeOffset? NextRegionFactDueUtc,
    int Chattiness,
    bool Sterile = false);

/// <summary>
/// The pure scheduler of cruise small talk (issue #195), the <c>RegionFactCore</c> shape:
/// every gate must hold, the per-flight count is the persona's chattiness capped by the
/// option, the kind sequence per flight is drawn without repeats from the enabled kinds, and
/// a claimed line that produced no speech is given back. The shell owns the clock, the
/// stores, the model, the mic and the arbiter.
/// </summary>
public sealed class SmallTalkCore
{
    /// <summary>A region fact due inside this window wins — the two never race.</summary>
    public static readonly TimeSpan RegionFactClearance = TimeSpan.FromMinutes(3);

    /// <summary>A claimed line that produced nothing looks again this much later.</summary>
    public static readonly TimeSpan Retry = TimeSpan.FromMinutes(2);

    private readonly List<SmallTalkKind> _sequence = [];
    private DateTimeOffset? _cruiseSince;
    private DateTimeOffset? _notBeforeUtc;
    private FlightPhase _lastPhase = FlightPhase.Unknown;
    private bool _followUpUsed = true;

    /// <summary>Lines spoken this flight.</summary>
    public int CountThisFlight { get; private set; }

    /// <summary>The kinds still to come this flight, in order.</summary>
    public IReadOnlyList<SmallTalkKind> Remaining => _sequence;

    /// <summary>The actual count for the flight: the persona's chattiness (0–3), never above the option.</summary>
    public static int Ceiling(SmallTalkOptions options, int chattiness)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Math.Min(Math.Max(0, options.MaxPerFlight), Math.Clamp(chattiness, 0, 3));
    }

    /// <summary>The kinds the options allow, in enum order.</summary>
    public static IReadOnlyList<SmallTalkKind> EnabledKinds(SmallTalkOptions options, bool rosterHasOfficer)
    {
        ArgumentNullException.ThrowIfNull(options);
        var kinds = new List<SmallTalkKind>();
        if (options.Kinds.Offer)
        {
            kinds.Add(SmallTalkKind.Offer);
        }

        if (options.Kinds.View)
        {
            kinds.Add(SmallTalkKind.View);
        }

        if (options.Kinds.Layover)
        {
            kinds.Add(SmallTalkKind.Layover);
        }

        if (options.Kinds.Crew && rosterHasOfficer)
        {
            kinds.Add(SmallTalkKind.Crew); // nothing to say about themselves without a record
        }

        return kinds;
    }

    /// <summary>Resets with the cabin's once-per-flight latches (the region facts' rule).</summary>
    public void OnPhaseChanged(FlightPhase from, FlightPhase to)
    {
        if (to == FlightPhase.ColdAndDark
            || (to == FlightPhase.Preflight && from is FlightPhase.Shutdown or FlightPhase.TaxiIn))
        {
            _sequence.Clear();
            CountThisFlight = 0;
            _cruiseSince = null;
            _notBeforeUtc = null;
            _followUpUsed = true;
        }
    }

    /// <summary>Draws the flight's kind sequence: every enabled kind once, shuffled by the
    /// dice. Called when the cruise begins (or when the enabled set is empty at a due tick).</summary>
    public void DrawSequence(IReadOnlyList<SmallTalkKind> enabled, Func<double> roll)
    {
        ArgumentNullException.ThrowIfNull(enabled);
        ArgumentNullException.ThrowIfNull(roll);
        _sequence.Clear();
        var pool = enabled.Distinct().ToList();
        while (pool.Count > 0)
        {
            var index = Math.Min(pool.Count - 1, (int)(Math.Clamp(roll(), 0, 0.999999) * pool.Count));
            _sequence.Add(pool[index]);
            pool.RemoveAt(index);
        }
    }

    /// <summary>The kind that is due now, or null. Every gate below merely holds the line for
    /// a later tick; nothing is consumed until <see cref="Claim"/>.</summary>
    public SmallTalkKind? Due(SmallTalkTick tick, SmallTalkOptions options, double sterileCeilingFt, IReadOnlyList<SmallTalkKind> enabled, Func<double> roll)
    {
        ArgumentNullException.ThrowIfNull(tick);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(enabled);
        ArgumentNullException.ThrowIfNull(roll);

        if (tick.Phase != _lastPhase)
        {
            _lastPhase = tick.Phase;
            if (tick.Phase == FlightPhase.Cruise && _cruiseSince is null)
            {
                _cruiseSince = tick.NowUtc; // a step climb keeps the schedule it had
                if (_sequence.Count == 0 && CountThisFlight == 0)
                {
                    DrawSequence(enabled, roll);
                }
            }
            else if (tick.Phase is not (FlightPhase.Climb or FlightPhase.Cruise))
            {
                _cruiseSince = null;
            }
        }

        var quiet = TimeSpan.FromMinutes(Math.Max(1, options.MinQuietMinutes));
        if (!options.Enabled || !tick.Live || tick.Phase != FlightPhase.Cruise || _cruiseSince is not { } since
            || !tick.AltitudeValid || tick.AltitudeMslFt <= sterileCeilingFt || tick.Sterile
            || tick.AbnormalOpen || tick.QuestionOpen
            || tick.Turbulence >= TurbulenceLevel.Moderate
            || CountThisFlight >= Ceiling(options, tick.Chattiness)
            || tick.NowUtc - since < quiet
            || (tick.LastFoSpeechUtc is { } last && tick.NowUtc - last < quiet)
            || (tick.NextRegionFactDueUtc is { } fact && fact - tick.NowUtc < RegionFactClearance)
            || (_notBeforeUtc is { } notBefore && tick.NowUtc < notBefore)
            || _sequence.Count == 0)
        {
            return null;
        }

        return _sequence[0];
    }

    /// <summary>Consumes the due kind: counts it, opens one follow-up for the exchange and
    /// holds the next line for a quiet gap.</summary>
    public SmallTalkKind? Claim(SmallTalkOptions options, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (_sequence.Count == 0)
        {
            return null;
        }

        var kind = _sequence[0];
        _sequence.RemoveAt(0);
        CountThisFlight++;
        _followUpUsed = false;
        _notBeforeUtc = nowUtc + TimeSpan.FromMinutes(Math.Max(1, options.MinQuietMinutes));
        return kind;
    }

    /// <summary>A claimed line that produced no speech: the kind goes back to the front and
    /// the count is given back; a short retry instead of a whole quiet gap.</summary>
    public void Release(SmallTalkKind kind, DateTimeOffset nowUtc)
    {
        _sequence.Insert(0, kind);
        CountThisFlight = Math.Max(0, CountThisFlight - 1);
        _followUpUsed = true;
        _notBeforeUtc = nowUtc + Retry;
    }

    /// <summary>One follow-up per exchange: true the first time after a claim, false after.</summary>
    public bool TryBeginFollowUp()
    {
        if (_followUpUsed)
        {
            return false;
        }

        _followUpUsed = true;
        return true;
    }
}
