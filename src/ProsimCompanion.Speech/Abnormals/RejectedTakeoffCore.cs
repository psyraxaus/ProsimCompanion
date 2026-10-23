using ProsimCompanion.Core.Flight;

namespace ProsimCompanion.Speech.Abnormals;

/// <summary>Where the rejected-takeoff core stands.</summary>
public enum RtoState
{
    /// <summary>Not on a takeoff roll.</summary>
    Idle,

    /// <summary>On the roll: a "stop" or the levers to idle rejects.</summary>
    Armed,

    /// <summary>Rejected; the rollout callouts run until the aircraft stops.</summary>
    Rejecting,

    /// <summary>Stopped and reported; waits for the next roll or the airborne edge.</summary>
    Done,
}

/// <summary>What the captain's word meant to the core.</summary>
public enum StopVerdict
{
    /// <summary>Not armed — not our moment, let the routing decide what it was.</summary>
    Ignored,

    /// <summary>Rejected below 100 kt: the callouts start, no read-back.</summary>
    Rejected,

    /// <summary>Rejected above 100 kt: the FO reads "stop" back first, then the callouts.</summary>
    RejectedWithReadback,

    /// <summary>Already airborne: "we're flying, captain".</summary>
    RefusedAirborne,

    /// <summary>A second word during the rejection — nothing more to do.</summary>
    AlreadyRejecting,
}

/// <summary>The FO's lines during the rejection, in the order they can fall.</summary>
public enum RtoCallout
{
    ReverseGreen,
    Decel,
    SeventyKnots,
    Stopped,
    EcamOffer,
}

/// <summary>One 100 ms sample.</summary>
/// <param name="Phase">The flight phase.</param>
/// <param name="IasKt">Indicated airspeed.</param>
/// <param name="GroundSpeedKt">Ground speed.</param>
/// <param name="OnGround">Committed ground contact.</param>
/// <param name="ReversersMaxBoth">Both reversers at max.</param>
/// <param name="ParkBrakeSet">The park brake.</param>
/// <param name="Lever1">Thrust lever 1, 0..1+ (idle about 0); null = unknown.</param>
/// <param name="Lever2">Thrust lever 2.</param>
/// <param name="NowUtc">Clock.</param>
public sealed record RtoSample(
    FlightPhase Phase,
    double IasKt,
    double GroundSpeedKt,
    bool OnGround,
    bool ReversersMaxBoth,
    bool ParkBrakeSet,
    double? Lever1,
    double? Lever2,
    DateTimeOffset NowUtc);

/// <summary>
/// Pure rejected-takeoff rules (issue #187). Armed from the takeoff roll to the airborne edge;
/// rejected by the captain's word (<see cref="OnStopWord"/>) or by both thrust levers brought to
/// idle above 40 kt on the ground after they were up; then the PM calls in order — reverse
/// green, decel, seventy knots, "aircraft stopped, park brake" — and the ECAM offer. The
/// departure cycle is untouched: a new roll re-arms. Clock in the sample.
/// </summary>
public sealed class RejectedTakeoffCore
{
    /// <summary>Above this the FO reads the stop word back before the callouts.</summary>
    public const double ReadbackSpeedKt = 100;

    /// <summary>Below this a lever retard is not a rejection (taxi / line-up).</summary>
    public const double LeverTriggerMinSpeedKt = 40;

    /// <summary>Levers below this read idle.</summary>
    public const double LeverIdle = 0.15;

    /// <summary>Levers above this read "up" — the retard edge needs them up first.</summary>
    public const double LeverUp = 0.5;

    /// <summary>"Decel" when IAS fell this much under the reject speed.</summary>
    public const double DecelDropKt = 10;

    /// <summary>Stopped below this ground speed.</summary>
    public const double StoppedSpeedKt = 2;

    /// <summary>The ECAM offer follows the stop by this, or sooner on the park brake.</summary>
    public static readonly TimeSpan OfferDelay = TimeSpan.FromSeconds(4);

    private bool _leversWereUp;
    private DateTimeOffset? _stoppedAt;
    private readonly HashSet<RtoCallout> _called = [];

    public RtoState State { get; private set; }

    /// <summary>IAS at the rejection.</summary>
    public double RejectSpeedKt { get; private set; }

    /// <summary>"word" or "levers".</summary>
    public string? Trigger { get; private set; }

    /// <summary>The captain said "stop" / "reject".</summary>
    public StopVerdict OnStopWord(RtoSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        Observe(sample);
        if (State == RtoState.Rejecting)
        {
            return StopVerdict.AlreadyRejecting;
        }

        if (State != RtoState.Armed)
        {
            // Airborne right after the roll: the FO refuses — "we're flying".
            return sample.Phase == FlightPhase.InitialClimb || (!sample.OnGround && sample.Phase.IsAirborne())
                ? StopVerdict.RefusedAirborne
                : StopVerdict.Ignored;
        }

        if (!sample.OnGround)
        {
            return StopVerdict.RefusedAirborne;
        }

        Reject(sample, "word");
        return sample.IasKt > ReadbackSpeedKt ? StopVerdict.RejectedWithReadback : StopVerdict.Rejected;
    }

    /// <summary>One sample; the callouts due on it (in order), the lever trigger included.</summary>
    public IReadOnlyList<RtoCallout> Process(RtoSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        Observe(sample);

        if (State == RtoState.Armed && sample.OnGround && sample.IasKt > LeverTriggerMinSpeedKt
            && _leversWereUp && sample.Lever1 is { } l1 && sample.Lever2 is { } l2 && l1 < LeverIdle && l2 < LeverIdle)
        {
            Reject(sample, "levers");
        }

        if (State != RtoState.Rejecting)
        {
            return [];
        }

        var due = new List<RtoCallout>();
        if (sample.ReversersMaxBoth && _called.Add(RtoCallout.ReverseGreen))
        {
            due.Add(RtoCallout.ReverseGreen);
        }

        if (sample.IasKt <= RejectSpeedKt - DecelDropKt && sample.IasKt > 20 && _called.Add(RtoCallout.Decel))
        {
            due.Add(RtoCallout.Decel);
        }

        if (RejectSpeedKt > 80 && sample.IasKt <= 70 && sample.IasKt > 20 && _called.Add(RtoCallout.SeventyKnots))
        {
            due.Add(RtoCallout.SeventyKnots);
        }

        if (sample.GroundSpeedKt < StoppedSpeedKt && _called.Add(RtoCallout.Stopped))
        {
            _stoppedAt = sample.NowUtc;
            due.Add(RtoCallout.Stopped);
        }

        if (_stoppedAt is { } stopped && (sample.ParkBrakeSet || sample.NowUtc - stopped >= OfferDelay) && _called.Add(RtoCallout.EcamOffer))
        {
            due.Add(RtoCallout.EcamOffer);
            State = RtoState.Done;
        }

        return due;
    }

    /// <summary>A new session: back to idle.</summary>
    public void Reset()
    {
        State = RtoState.Idle;
        _leversWereUp = false;
        _stoppedAt = null;
        _called.Clear();
        RejectSpeedKt = 0;
        Trigger = null;
    }

    private void Observe(RtoSample sample)
    {
        switch (State)
        {
            case RtoState.Idle or RtoState.Done when sample.Phase == FlightPhase.TakeoffRoll && sample.OnGround:
                // A roll (the first, or another attempt on the same cycle) arms the core.
                if (State == RtoState.Done && sample.IasKt < LeverTriggerMinSpeedKt)
                {
                    Arm();
                }
                else if (State == RtoState.Idle)
                {
                    Arm();
                }

                break;

            case RtoState.Armed when !sample.OnGround || sample.Phase.IsAirborne():
                // Airborne: the window closed, nothing to reject.
                State = RtoState.Idle;
                break;

            case RtoState.Armed when sample.Phase is not FlightPhase.TakeoffRoll and not FlightPhase.TaxiOut:
                State = RtoState.Idle;
                break;

            case RtoState.Rejecting when !sample.OnGround && sample.Phase.IsAirborne():
                // The captain continued after all — the rollout calls stop here.
                State = RtoState.Idle;
                break;
        }

        if (State == RtoState.Armed && sample.Lever1 is { } a && sample.Lever2 is { } b && a > LeverUp && b > LeverUp)
        {
            _leversWereUp = true;
        }
    }

    private void Arm()
    {
        State = RtoState.Armed;
        _leversWereUp = false;
        _stoppedAt = null;
        _called.Clear();
        RejectSpeedKt = 0;
        Trigger = null;
    }

    private void Reject(RtoSample sample, string trigger)
    {
        State = RtoState.Rejecting;
        RejectSpeedKt = sample.IasKt;
        Trigger = trigger;
    }
}
