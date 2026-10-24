using ProsimCompanion.Core.Flight;

namespace ProsimCompanion.Speech.Abnormals;

/// <summary>The memory items of the engine-failure-after-V1 drill, in order (issue #187).</summary>
public enum EfatoItem
{
    Rudder,
    Rotate,
    V2,
    GearUp,
    EcamActions,
}

/// <summary>One sample for the drill sequencer.</summary>
public sealed record EfatoSample(
    double IasKt,
    bool OnGround,
    double VerticalSpeedFpm,
    double RadioAltitudeFt,
    double AltitudeAglFt,
    bool GearDown,
    int VrKt,
    int V2Kt,
    DateTimeOffset NowUtc);

/// <summary>
/// Pure pacing of the EFATO memory items on the flight data (issue #187): "rudder" at once;
/// "rotate" at VR (skipped when already airborne); "V2" airborne at V2; "positive climb — gear
/// up" on a climbing radio altitude (skipped when the gear is already up); "ECAM actions" at
/// 400 ft AAL, the hand-over. Each item waits at most <see cref="ItemTimeout"/> for its cue and
/// then falls through, so the drill always ends. Clock in the sample.
/// </summary>
public sealed class EfatoCore
{
    public static readonly TimeSpan ItemTimeout = TimeSpan.FromSeconds(25);
    public const double HandoverAglFt = 400;

    private DateTimeOffset _armedAt;
    private DateTimeOffset _lastItemAt;
    private EfatoItem? _next;
    private double _previousRadio = double.NaN;

    /// <summary>Which engine failed (1 or 2); 0 before arming.</summary>
    public int FailedEngine { get; private set; }

    public bool Running => _next is not null;

    /// <summary>Arms the sequence; the failed engine is the one with the lower N2.</summary>
    public void Arm(double n2Engine1, double n2Engine2, DateTimeOffset nowUtc)
    {
        FailedEngine = n2Engine1 <= n2Engine2 ? 1 : 2;
        _armedAt = nowUtc;
        _lastItemAt = nowUtc;
        _next = EfatoItem.Rudder;
        _previousRadio = double.NaN;
    }

    /// <summary>The items due on this sample (zero, one or two), in order.</summary>
    public IReadOnlyList<EfatoItem> Process(EfatoSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var due = new List<EfatoItem>();
        var guard = 0;
        while (_next is { } item && guard++ < 5)
        {
            var timedOut = sample.NowUtc - _lastItemAt >= ItemTimeout;
            var (fire, skip) = Judge(item, sample, timedOut);
            if (!fire && !skip)
            {
                break;
            }

            if (fire)
            {
                due.Add(item);
            }

            _lastItemAt = sample.NowUtc;
            _next = item == EfatoItem.EcamActions ? null : item + 1;
        }

        _previousRadio = sample.RadioAltitudeFt;
        return due;
    }

    public void Reset()
    {
        _next = null;
        FailedEngine = 0;
        _previousRadio = double.NaN;
    }

    private (bool Fire, bool Skip) Judge(EfatoItem item, EfatoSample s, bool timedOut) => item switch
    {
        EfatoItem.Rudder => (true, false),
        EfatoItem.Rotate => !s.OnGround ? (false, true) : (s.VrKt > 0 && s.IasKt >= s.VrKt) || timedOut ? (true, false) : (false, false),
        EfatoItem.V2 => (!s.OnGround && (s.V2Kt <= 0 || s.IasKt >= s.V2Kt - 2)) || timedOut ? (true, false) : (false, false),
        EfatoItem.GearUp => !s.GearDown && !s.OnGround ? (false, true)
            : (!s.OnGround && s.VerticalSpeedFpm > 100 && s.RadioAltitudeFt > 10 && !double.IsNaN(_previousRadio) && s.RadioAltitudeFt > _previousRadio) || timedOut
                ? (true, false) : (false, false),
        EfatoItem.EcamActions => (!s.OnGround && s.AltitudeAglFt >= HandoverAglFt) || timedOut ? (true, false) : (false, false),
        _ => (false, true),
    };
}
