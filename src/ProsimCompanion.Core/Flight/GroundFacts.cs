using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Core.Flight;

/// <summary>
/// The committed ground facts (CONTEXT.md "Ground fact"): the states of the aircraft on the
/// ground that the flight-state engine publishes once per live tick beside the phase, taken
/// from the same classified sample the rule table reads. Every consumer that used to hold its
/// own raw beacon / park-brake / APU subscription and its own edge latch (the ground-equipment
/// removal, the pushback sequencer, the arrival chain, the cabin crew) reads these instead,
/// so "beacon on" has one definition and one clock.
/// </summary>
/// <param name="BeaconOn">The beacon switch is on. One definition for the whole app (decided
/// 2026-10-19 when the four raw readers were folded here): <c>S_OH_EXT_LT_BEACON</c> is a
/// two-position switch the catalogue documents as 0 = Off / 1 = On, so "on" is any non-zero
/// value — what the engine's own sample, the equipment removal and the pushback sequencer
/// already used; the cabin crew's <c>== 1</c> was the odd one out and read the same values.</param>
/// <param name="EnginesOff">No engine running (the #59-hardened <see cref="FlightDataSnapshot.AnyEngineRunning"/>
/// is false).</param>
/// <param name="ParkBrakeSet">The park brake is set.</param>
/// <param name="ExternalPowerOn">External power is feeding the buses (<c>B_ELEC_EXTERNAL_CONNECT</c>)
/// — the ground-equipment gradual-removal rule reads its falling edge.</param>
/// <param name="Parked">On the (committed) ground with the engines off, the park brake set and
/// the ground speed below the parked threshold — the instant verdict, no hold.</param>
/// <param name="ParkedSince">When the current parked spell began; null while not parked.</param>
/// <param name="StableParked">The parked spell has lasted at least the turnaround hold
/// (<see cref="FlightStateOptions.TurnaroundHoldSeconds"/>) — the engine's own parked settle,
/// the one the Shutdown → Preflight rule waits for.</param>
public sealed record GroundFacts(
    bool BeaconOn,
    bool EnginesOff,
    bool ParkBrakeSet,
    bool ExternalPowerOn,
    bool Parked,
    DateTimeOffset? ParkedSince,
    bool StableParked)
{
    /// <summary>Nothing committed yet. Every discrete fact starts false, exactly as the raw
    /// subscriptions the facts replace started at their 0 / false fallbacks — so the first
    /// live sample with the beacon on is a <see cref="GroundFactEdge.BeaconOn"/> edge, as the
    /// first raw push with value 1 used to be a rising edge.</summary>
    public static GroundFacts None { get; } = new(false, false, false, false, false, null, false);
}

/// <summary>One committed change of a ground fact.</summary>
public enum GroundFactEdge
{
    BeaconOn,
    BeaconOff,
    EnginesOff,
    EngineRunning,
    ParkBrakeSet,
    ParkBrakeReleased,
    ExternalPowerOn,
    ExternalPowerOff,

    /// <summary>The parked spell began (engines off, brake set, stopped, on the ground).</summary>
    Parked,

    /// <summary>The parked spell ended — movement, an engine start or a released brake.</summary>
    Unparked,

    /// <summary>The parked spell reached the turnaround hold; once per spell.</summary>
    StableParked,
}

/// <summary>Payload of <see cref="IGroundFactsSource.GroundFactChanged"/>: which edge, the
/// facts as they stand after it, and the tick time it was committed at.</summary>
public sealed record GroundFactChange(GroundFactEdge Edge, GroundFacts Facts, DateTimeOffset At);

/// <summary>
/// Read-only view of the committed ground facts — the seam the ground-ops consumers depend on
/// instead of raw datarefs, so tests drive edges directly and a replayed recording exercises
/// the same edges the live engine published. Implemented by <see cref="FlightStateEngine"/>.
/// </summary>
public interface IGroundFactsSource
{
    /// <summary>The facts as last committed. Held across a session gap and while the flight
    /// is not live (the engine only commits from a live, classified sample), exactly as a
    /// raw subscription holds its last pushed value.</summary>
    GroundFacts Facts { get; }

    /// <summary>Raised once per committed edge, on the engine's timer thread, after
    /// <see cref="Facts"/> already shows the new state. Subscribers must not block.</summary>
    event Action<GroundFactChange>? GroundFactChanged;
}

/// <summary>
/// The pure core behind the committed ground facts (testable without the engine's timer):
/// one <see cref="Observe"/> per live tick turns the classified sample into the new facts and
/// the edges that changed. Debounce rule: the discrete switch datarefs (beacon, park brake,
/// external power) and the engines-off verdict commit on ONE live sample — a switch position
/// is a cockpit state, not a sensor; the five recorded flights in the test recordings
/// (about 40,000 live samples) show every beacon flip minutes apart and no one-sample flip,
/// and a second agreeing sample would cost the beacon → equipment-removal path a second
/// engine tick. Ground contact, which does bounce, keeps its agreement count upstream in the
/// engine; <see cref="GroundFacts.Parked"/> is built on the committed ground contact. The
/// parked spell is the only hold here: <see cref="GroundFacts.StableParked"/> after the
/// turnaround hold.
/// </summary>
public sealed class GroundFactsTracker
{
    /// <summary>The facts as last committed.</summary>
    public GroundFacts Facts { get; private set; } = GroundFacts.None;

    /// <summary>Commits the facts the classified sample shows and returns the edges, in a
    /// fixed order (discrete facts first, then the parked spell) so a consumer that acts on
    /// two edges from one tick sees them in the same order every time.</summary>
    public IReadOnlyList<GroundFactChange> Observe(FlightDataSnapshot classified, DateTimeOffset nowUtc, FlightStateOptions options)
    {
        ArgumentNullException.ThrowIfNull(classified);
        ArgumentNullException.ThrowIfNull(options);

        var previous = Facts;
        var parked = classified.OnGround
            && !classified.AnyEngineRunning
            && classified.ParkBrakeSet
            && classified.GroundSpeedKt < options.ParkedGroundSpeedKt;
        DateTimeOffset? parkedSince = parked ? previous.ParkedSince ?? nowUtc : null;
        var hold = TimeSpan.FromSeconds(Math.Max(0, options.TurnaroundHoldSeconds));
        var stable = parkedSince is { } since && nowUtc - since >= hold;

        var next = new GroundFacts(
            BeaconOn: classified.BeaconOn,
            EnginesOff: !classified.AnyEngineRunning,
            ParkBrakeSet: classified.ParkBrakeSet,
            ExternalPowerOn: classified.ExternalPowerOn,
            Parked: parked,
            ParkedSince: parkedSince,
            StableParked: stable);
        Facts = next;

        if (next == previous)
        {
            return [];
        }

        var edges = new List<GroundFactChange>(4);
        Add(edges, previous.BeaconOn, next.BeaconOn, GroundFactEdge.BeaconOn, GroundFactEdge.BeaconOff, next, nowUtc);
        Add(edges, previous.EnginesOff, next.EnginesOff, GroundFactEdge.EnginesOff, GroundFactEdge.EngineRunning, next, nowUtc);
        Add(edges, previous.ParkBrakeSet, next.ParkBrakeSet, GroundFactEdge.ParkBrakeSet, GroundFactEdge.ParkBrakeReleased, next, nowUtc);
        Add(edges, previous.ExternalPowerOn, next.ExternalPowerOn, GroundFactEdge.ExternalPowerOn, GroundFactEdge.ExternalPowerOff, next, nowUtc);
        Add(edges, previous.Parked, next.Parked, GroundFactEdge.Parked, GroundFactEdge.Unparked, next, nowUtc);
        if (next.StableParked && !previous.StableParked)
        {
            edges.Add(new GroundFactChange(GroundFactEdge.StableParked, next, nowUtc));
        }

        return edges;
    }

    /// <summary>The flight session genuinely ended: the parked spell belongs to the flight
    /// that is over and restarts with the next live sample. The discrete facts are kept — a
    /// beacon that stays on across a session end is not a new beacon-on edge, exactly as it
    /// was not a new raw push.</summary>
    public void EndParkedSpell()
        => Facts = Facts with { Parked = false, ParkedSince = null, StableParked = false };

    private static void Add(
        List<GroundFactChange> edges, bool before, bool after,
        GroundFactEdge rising, GroundFactEdge falling, GroundFacts facts, DateTimeOffset at)
    {
        if (before == after)
        {
            return;
        }

        edges.Add(new GroundFactChange(after ? rising : falling, facts, at));
    }
}
