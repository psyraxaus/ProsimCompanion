using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Speech.Cabin;

/// <summary>One tick's inputs for the PA prompt rules.</summary>
/// <param name="TakeoffUtc">The OFF stamp; null before take-off.</param>
/// <param name="Phase">The flight phase.</param>
/// <param name="TodApproachingSeen">The top-of-descent notice has fired this cycle.</param>
/// <param name="TurbulenceLevel">The ride level.</param>
/// <param name="SeatbeltSignsOn">The sign switch reads ON.</param>
/// <param name="NowUtc">Clock.</param>
public sealed record PaPromptSample(
    DateTimeOffset? TakeoffUtc,
    FlightPhase Phase,
    bool TodApproachingSeen,
    TurbulenceLevel TurbulenceLevel,
    bool SeatbeltSignsOn,
    DateTimeOffset NowUtc);

/// <summary>
/// The pure PA prompt rules (issue #186): after take-off by the clock, at the top-of-descent
/// notice, and on the seat-belt sign's OFF→ON edge in cruise turbulence. Each kind once per
/// cycle — the store holds that latch; this core holds only the edge memory. Clock in the sample.
/// </summary>
public sealed class PaPromptCore
{
    private bool _todHandled;
    private bool? _lastSeatbeltOn;

    /// <summary>The kind due on this tick, or null. <paramref name="done"/> = already prompted this cycle.</summary>
    public PaPromptKind? Evaluate(PaPromptSample sample, CabinOptions options, IReadOnlyList<PaPromptKind> done)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(done);

        var seatbeltEdge = _lastSeatbeltOn == false && sample.SeatbeltSignsOn;
        _lastSeatbeltOn = sample.SeatbeltSignsOn;

        if (!options.PaPrompts)
        {
            return null;
        }

        if (sample.TodApproachingSeen && !_todHandled)
        {
            _todHandled = true;
            if (!done.Contains(PaPromptKind.TopOfDescent) && sample.Phase.IsAirborne())
            {
                return PaPromptKind.TopOfDescent;
            }
        }

        if (!done.Contains(PaPromptKind.AfterTakeoff) && sample.TakeoffUtc is { } off
            && sample.Phase is FlightPhase.Climb or FlightPhase.Cruise
            && sample.NowUtc - off >= TimeSpan.FromMinutes(Math.Max(1, options.PaAfterTakeoffMinutes)))
        {
            return PaPromptKind.AfterTakeoff;
        }

        if (!done.Contains(PaPromptKind.SeatbeltsOn) && seatbeltEdge
            && sample.Phase == FlightPhase.Cruise && sample.TurbulenceLevel >= TurbulenceLevel.Light)
        {
            return PaPromptKind.SeatbeltsOn;
        }

        return null;
    }

    /// <summary>A new cycle.</summary>
    public void Reset()
    {
        _todHandled = false;
        _lastSeatbeltOn = null;
    }
}
