namespace ProsimCompanion.Core.Flight;

/// <summary>One 100 ms look at the aircraft for the touchdown recorder.</summary>
/// <param name="At">Sample time (any monotonic UTC clock — only differences are used).</param>
/// <param name="RawOnGround">The raw weight-on-wheels flag: used only to find the instant of
/// first contact inside the window the committed edge closes.</param>
/// <param name="GroundContact">The COMMITTED ground contact (CONTEXT.md): the phase engine's
/// filtered on-ground state. Touchdowns and bounces are edges of this, never of the raw flag,
/// so a one-sample flicker is neither.</param>
/// <param name="PitchDeg">Null when the ref has no value.</param>
/// <param name="BankDeg">Null when the ref has no value.</param>
/// <param name="AccelerationYRaw">Raw <c>aircraft.acceleration.Y</c>; unit unknown.</param>
public sealed record TouchdownSample(
    DateTimeOffset At,
    bool RawOnGround,
    bool GroundContact,
    double VerticalSpeedFpm,
    double IasKt,
    double GroundSpeedKt,
    double? PitchDeg,
    double? BankDeg,
    double? AccelerationYRaw);

/// <summary>What one landing looked like at the wheels.</summary>
/// <param name="ContactAt">When the wheels first touched (the raw flag's first sample).</param>
/// <param name="VerticalSpeedFpm">The MOST NEGATIVE vertical speed in the second before
/// contact — the rate the aircraft arrived at, not the already-arrested sample at contact.</param>
/// <param name="Bounces">Times ground contact was lost and regained within the bounce window.</param>
/// <param name="WentAround">Ground contact was lost and not regained: a touch-and-go or a
/// go-around after touchdown. The landing that ends the flight reports separately.</param>
/// <param name="AccelerationYRawMax">Largest raw vertical-acceleration value from contact
/// to the end of the capture window. Unit unknown — not G.</param>
/// <param name="AccelerationYRawMin">Smallest raw value over the same window.</param>
/// <param name="AccelerationYRawBeforeContact">Mean raw value over the second before
/// contact — the steady-flight baseline that tells the unit apart (about 1 = G with gravity,
/// about 0 = G without, about 9.8 = m/s², about 32 = ft/s²).</param>
public sealed record TouchdownReport(
    DateTimeOffset ContactAt,
    double VerticalSpeedFpm,
    double IasKt,
    double GroundSpeedKt,
    double? PitchDeg,
    double? BankDeg,
    int Bounces,
    bool WentAround,
    double? AccelerationYRawMax,
    double? AccelerationYRawMin,
    double? AccelerationYRawBeforeContact);

/// <summary>
/// Pure touchdown recorder (issue #146), clock on the sample. Arms after the aircraft has
/// held "no ground contact" for <see cref="MinAirborne"/>; the next committed ground-contact
/// edge is a touchdown. The report is held back until the landing has settled — ground
/// contact kept for <see cref="BounceWindow"/> — so the bounce count is final and exactly
/// one report describes one landing. Contact lost for longer than the window ends that
/// touchdown as a go-around; the recorder is then armed again for the real landing.
/// </summary>
public sealed class TouchdownRecorderCore
{
    /// <summary>How long ground contact must be absent before a contact counts as a landing
    /// (a rejected takeoff or a hop off a bump during the roll is not one).</summary>
    public static readonly TimeSpan MinAirborne = TimeSpan.FromSeconds(5);

    /// <summary>Contact lost and regained inside this is a bounce; kept for this long, the
    /// landing has settled.</summary>
    public static readonly TimeSpan BounceWindow = TimeSpan.FromSeconds(5);

    /// <summary>The "second before contact" the arrival rate is taken from.</summary>
    public static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(1);

    /// <summary>How long after contact the raw vertical acceleration keeps being sampled for
    /// its peak (the oleo compression is over well inside this).</summary>
    public static readonly TimeSpan AccelerationWindow = TimeSpan.FromSeconds(3);

    /// <summary>History kept: the rate window plus the longest the committed edge can trail
    /// the raw contact (the ground-contact agreement filter at the engine's 250 ms tick).</summary>
    private static readonly TimeSpan History = TimeSpan.FromSeconds(4);

    private readonly Queue<TouchdownSample> _history = new();
    private DateTimeOffset? _airborneSince;
    private bool _armed;
    private bool? _lastGroundContact;
    private Pending? _pending;

    /// <summary>Forgets everything (flight no longer live, or a new flight cycle).</summary>
    public void Reset()
    {
        _history.Clear();
        _airborneSince = null;
        _armed = false;
        _lastGroundContact = null;
        _pending = null;
    }

    /// <summary>Feeds one sample. Returns the finished report on the sample that settles a
    /// landing (or confirms a go-around), null otherwise.</summary>
    public TouchdownReport? Process(TouchdownSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);

        _history.Enqueue(sample);
        while (_history.Count > 0 && sample.At - _history.Peek().At > History)
        {
            _history.Dequeue();
        }

        var wasOnGround = _lastGroundContact;
        _lastGroundContact = sample.GroundContact;

        if (_pending is { } pending)
        {
            return Settle(pending, sample, wasOnGround == true);
        }

        if (!sample.GroundContact)
        {
            _airborneSince ??= sample.At;
            if (sample.At - _airborneSince.Value >= MinAirborne)
            {
                _armed = true;
            }

            return null;
        }

        // On the ground. An armed recorder seeing the edge has a touchdown; anything else
        // (parked, taxiing, a hop too short to arm) just clears the airborne timer.
        if (_armed && wasOnGround == false)
        {
            _pending = Capture(sample);
        }

        _airborneSince = null;
        _armed = false;
        return null;
    }

    private TouchdownReport? Settle(Pending pending, TouchdownSample sample, bool wasOnGround)
    {
        if (sample.At - pending.ContactAt <= AccelerationWindow && sample.AccelerationYRaw is { } accel)
        {
            pending.AccelMax = pending.AccelMax is { } max ? Math.Max(max, accel) : accel;
            pending.AccelMin = pending.AccelMin is { } min ? Math.Min(min, accel) : accel;
        }

        if (sample.GroundContact)
        {
            if (!wasOnGround)
            {
                // Regained inside the window (a longer loss already ended this touchdown).
                pending.Bounces++;
                pending.LastContactAt = sample.At;
                pending.LostAt = null;
            }

            if (sample.At - pending.LastContactAt >= BounceWindow)
            {
                _pending = null;
                return pending.ToReport(wentAround: false);
            }

            return null;
        }

        pending.LostAt ??= sample.At;
        if (sample.At - pending.LostAt.Value > BounceWindow)
        {
            // Airborne again and staying there: this touch was a go-around. The aircraft has
            // been off the ground longer than MinAirborne needs, so the next contact counts.
            _pending = null;
            _airborneSince = pending.LostAt;
            _armed = true;
            return pending.ToReport(wentAround: true);
        }

        return null;
    }

    private Pending Capture(TouchdownSample edge)
    {
        // The committed edge trails the wheels by the agreement filter. Walk back through
        // the unbroken run of raw on-ground samples to the first one: that is contact.
        var samples = _history.ToArray();
        var contactIndex = samples.Length - 1;
        while (contactIndex > 0 && samples[contactIndex - 1].RawOnGround)
        {
            contactIndex--;
        }

        var contact = samples[contactIndex];
        var windowStart = contact.At - RateWindow;
        var before = samples
            .Take(contactIndex + 1)
            .Where(s => s.At >= windowStart)
            .ToArray();
        var rate = before.Min(s => s.VerticalSpeedFpm);

        var baseline = before
            .Take(before.Length - 1)
            .Where(s => s.AccelerationYRaw is not null)
            .Select(s => s.AccelerationYRaw!.Value)
            .ToArray();
        var after = samples
            .Skip(contactIndex)
            .Where(s => s.AccelerationYRaw is not null)
            .Select(s => s.AccelerationYRaw!.Value)
            .ToArray();

        return new Pending
        {
            ContactAt = contact.At,
            LastContactAt = edge.At,
            VerticalSpeedFpm = rate,
            IasKt = contact.IasKt,
            GroundSpeedKt = contact.GroundSpeedKt,
            PitchDeg = contact.PitchDeg,
            BankDeg = contact.BankDeg,
            AccelBaseline = baseline.Length > 0 ? baseline.Average() : null,
            AccelMax = after.Length > 0 ? after.Max() : null,
            AccelMin = after.Length > 0 ? after.Min() : null,
        };
    }

    private sealed class Pending
    {
        public DateTimeOffset ContactAt { get; init; }
        public DateTimeOffset LastContactAt { get; set; }
        public DateTimeOffset? LostAt { get; set; }
        public double VerticalSpeedFpm { get; init; }
        public double IasKt { get; init; }
        public double GroundSpeedKt { get; init; }
        public double? PitchDeg { get; init; }
        public double? BankDeg { get; init; }
        public int Bounces { get; set; }
        public double? AccelBaseline { get; init; }
        public double? AccelMax { get; set; }
        public double? AccelMin { get; set; }

        public TouchdownReport ToReport(bool wentAround) => new(
            ContactAt, VerticalSpeedFpm, IasKt, GroundSpeedKt, PitchDeg, BankDeg,
            Bounces, wentAround, AccelMax, AccelMin, AccelBaseline);
    }
}
