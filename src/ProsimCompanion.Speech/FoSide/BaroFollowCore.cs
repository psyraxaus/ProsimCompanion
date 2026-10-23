using ProsimCompanion.Core.Flight;

namespace ProsimCompanion.Speech.FoSide;

/// <summary>What the FO should do to its own altimeter.</summary>
public enum BaroActionKind
{
    /// <summary>Push STD on the FO side.</summary>
    SetStd,

    /// <summary>Pull QNH on the FO side and set the value.</summary>
    SetQnh,

    /// <summary>Change the FO's QNH value (both already on QNH).</summary>
    SetValue,

    /// <summary>The FO side reads inches — the FO says so and does nothing (once per episode).</summary>
    InchesNotice,
}

/// <summary>One decided action; <paramref name="Hpa"/> for the QNH kinds.</summary>
public sealed record BaroAction(BaroActionKind Kind, int Hpa);

/// <summary>One sample of both altimeters.</summary>
/// <param name="Live">Flight live and both sides' subscriptions fresh.</param>
/// <param name="CaptainStd">The captain's effective STD gate.</param>
/// <param name="CaptainHpa">The captain's baro, hPa.</param>
/// <param name="FoStd">The FO's effective STD gate.</param>
/// <param name="FoHpa">The FO's baro, hPa.</param>
/// <param name="FoModeHpa">The FO's baro unit is hPa.</param>
/// <param name="Phase">The flight phase.</param>
/// <param name="NowUtc">Clock.</param>
public sealed record BaroSample(
    bool Live,
    bool CaptainStd,
    double CaptainHpa,
    bool FoStd,
    double FoHpa,
    bool FoModeHpa,
    FlightPhase Phase,
    DateTimeOffset NowUtc);

/// <summary>
/// Pure baro-follow rules (issue #188): the FO FOLLOWS the captain, never leads. Only a change
/// on the captain's side (the STD gate flipping, or the QNH value moving by a hectopascal) arms
/// a candidate; the candidate fires once it has stood for <see cref="Debounce"/> and the FO's
/// side still differs. A mismatch that was there before the captain touched anything is left
/// alone — the FO does not "correct" a captain who has not acted. Clock in the sample.
/// </summary>
public sealed class BaroFollowCore
{
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);

    /// <summary>Below this the captain's value is noise (a cold aircraft reads 0).</summary>
    public const double MinPlausibleHpa = 900;

    private bool? _captainStdPrev;
    private int? _captainHpaPrev;
    private BaroAction? _candidate;
    private DateTimeOffset _candidateSince;
    private bool _inchesNoticed;
    private bool _inhibited;

    /// <summary>The candidate waiting out its debounce, for tests and the status line.</summary>
    public BaroAction? Pending => _candidate;

    /// <summary>Set after two failed writes: nothing more this cycle.</summary>
    public bool Inhibited => _inhibited;

    public BaroAction? Evaluate(BaroSample s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (!s.Live || s.Phase is FlightPhase.ColdAndDark or FlightPhase.Unknown)
        {
            _candidate = null;
            _captainStdPrev = null;
            _captainHpaPrev = null;
            return null;
        }

        var captainHpa = (int)Math.Round(s.CaptainHpa);
        var edge = (_captainStdPrev is { } prevStd && prevStd != s.CaptainStd)
            || (_captainHpaPrev is { } prevHpa && !s.CaptainStd && Math.Abs(prevHpa - captainHpa) >= 1 && captainHpa >= MinPlausibleHpa);
        _captainStdPrev = s.CaptainStd;
        _captainHpaPrev = captainHpa;

        if (!s.FoModeHpa)
        {
            _candidate = null;
            if (edge && !_inchesNoticed)
            {
                _inchesNoticed = true;
                return new BaroAction(BaroActionKind.InchesNotice, 0);
            }

            return null;
        }

        _inchesNoticed = false;
        if (_inhibited)
        {
            return null;
        }

        var desired = Desired(s, captainHpa);
        if (edge && desired is not null)
        {
            _candidate = desired;
            _candidateSince = s.NowUtc;
            return null;
        }

        if (_candidate is null)
        {
            return null;
        }

        if (desired is null || desired.Kind != _candidate.Kind)
        {
            _candidate = null; // the FO side caught up (or the captain changed again) — nothing to do
            return null;
        }

        if (s.NowUtc - _candidateSince < Debounce)
        {
            return null;
        }

        var fire = desired;
        _candidate = null;
        return fire;
    }

    /// <summary>Two strikes on the write: the FO leaves the altimeter alone until the next cycle.</summary>
    public void Inhibit() => _inhibited = true;

    public void Reset()
    {
        _captainStdPrev = null;
        _captainHpaPrev = null;
        _candidate = null;
        _inchesNoticed = false;
        _inhibited = false;
    }

    private static BaroAction? Desired(BaroSample s, int captainHpa)
    {
        if (s.CaptainStd && !s.FoStd)
        {
            return new BaroAction(BaroActionKind.SetStd, 0);
        }

        if (!s.CaptainStd && captainHpa >= MinPlausibleHpa)
        {
            if (s.FoStd)
            {
                return new BaroAction(BaroActionKind.SetQnh, captainHpa);
            }

            if (Math.Abs(Math.Round(s.FoHpa) - captainHpa) >= 1)
            {
                return new BaroAction(BaroActionKind.SetValue, captainHpa);
            }
        }

        return null;
    }
}
