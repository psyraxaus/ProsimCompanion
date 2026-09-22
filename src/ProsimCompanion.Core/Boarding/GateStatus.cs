using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Boarding;

/// <summary>
/// The gate monitor's five states (owner spec 2026-09-22), in the only order they can be
/// entered. The two "closed" states are distinct values so the card can show a grey "not
/// called" before boarding and a green tick after it.
/// </summary>
public enum GateState
{
    /// <summary>Before boarding: nothing called, no passengers moving.</summary>
    Closed,

    /// <summary>Boarding called (or the jetway/stairs are on and door 1L is open) but no
    /// passenger has boarded yet.</summary>
    Open,

    /// <summary>Passengers are boarding.</summary>
    Boarding,

    /// <summary>Still boarding, but the pax share or the STD countdown crossed the final-call
    /// threshold.</summary>
    FinalCall,

    /// <summary>Boarding completed (or door 1L closed after boarding). Holds until the next
    /// flight cycle.</summary>
    ClosedAfterBoarding,
}

/// <summary>Everything the gate strip on the Flight Status hero renders.</summary>
/// <param name="State">The latched gate state.</param>
/// <param name="GateId">The confirmed GSX gate ("B12"), or null when unknown.</param>
/// <param name="PaxBoarded">GSX boarded count; null until GSX reports a pax session.</param>
/// <param name="PaxTarget">GSX planned total; null until known.</param>
/// <param name="StdUtc">The effective scheduled departure (manual override beats the OFP).</param>
/// <param name="MinutesToStd">Whole minutes until <paramref name="StdUtc"/> (negative = past).</param>
/// <param name="Detail">The short pill text under the state ("Not called", "Jetway · door 1L",
/// "Doors closed 10:41Z", "GSX offline").</param>
/// <param name="ChangedAtUtc">When <paramref name="State"/> was last entered.</param>
/// <param name="Dimmed">True from taxi-out onward: the strip stays (the hero keeps its shape)
/// but fades like a pending sequence step.</param>
public sealed record GateStatusSnapshot(
    GateState State,
    string? GateId,
    int? PaxBoarded,
    int? PaxTarget,
    DateTimeOffset? StdUtc,
    int? MinutesToStd,
    string Detail,
    DateTimeOffset? ChangedAtUtc,
    bool Dimmed)
{
    public static GateStatusSnapshot Empty { get; } =
        new(GateState.Closed, null, null, null, null, null, "Not called", null, false);

    /// <summary>Boarded share of the target, 0–1, for the progress bar; 0 when unknown.</summary>
    public double Progress => PaxTarget is > 0 && PaxBoarded is { } boarded
        ? Math.Clamp((double)boarded / PaxTarget.Value, 0, 1)
        : State == GateState.ClosedAfterBoarding ? 1 : 0;
}

/// <summary>
/// Live gate-monitor state for the web UI. Written by <see cref="GateMonitorService"/>; read
/// by the Flight Status page. Kept in Core so the Web project (which references only Core)
/// can render it.
/// </summary>
public sealed class GateStatusStore : SnapshotStore<GateStatusSnapshot>
{
    public GateStatusStore()
        : base(GateStatusSnapshot.Empty)
    {
    }
}
