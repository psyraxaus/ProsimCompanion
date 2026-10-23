namespace ProsimCompanion.Core.Flight;

/// <summary>The ride, as the cabin feels it (issue #186).</summary>
public enum TurbulenceLevel
{
    None = 0,
    Light = 1,
    Moderate = 2,
    Severe = 3,
}

/// <summary>What <c>aircraft.acceleration.Y</c> is measured in — guessed from the at-rest
/// baseline (owner decision 2026-10-24, option 1: auto-calibrate rather than ask) or forced
/// by <c>cabin.turbulenceUnit</c>.</summary>
public enum AccelerationUnit
{
    /// <summary>No baseline yet (never at rest with a live value, or the override is auto and
    /// the baseline fits no known unit) — G assumed.</summary>
    Unknown = 0,

    /// <summary>G including gravity: about 1 at rest.</summary>
    G,

    /// <summary>G with gravity removed: about 0 at rest. Indistinguishable from "no signal",
    /// so the detector takes it only when forced or when the baseline is near 0 AND the
    /// signal moves in flight.</summary>
    GWithoutGravity,

    /// <summary>Metres per second squared: about 9.8 at rest.</summary>
    MetresPerSecondSquared,

    /// <summary>Feet per second squared: about 32.2 at rest.</summary>
    FeetPerSecondSquared,
}

/// <summary>One 100 ms sample for the detector.</summary>
/// <param name="AtUtc">Sample time.</param>
/// <param name="RawAccelerationY">The dataref as pushed; null when NaN / absent.</param>
/// <param name="OnGround">The engine's committed ground contact.</param>
/// <param name="GroundSpeedKt">For the at-rest test (parked = calibration time).</param>
/// <param name="Airborne">The phase is an airborne one — only then is a level judged
/// (taxi bumps and the landing roll are not turbulence).</param>
public sealed record TurbulenceSample(DateTimeOffset AtUtc, double? RawAccelerationY, bool OnGround, double GroundSpeedKt, bool Airborne);

/// <summary>The thresholds in G, deviation from the baseline over the window.</summary>
public sealed record TurbulenceThresholds(double LightG, double ModerateG, double SevereG)
{
    /// <summary>The usual bands: light 0.2, moderate 0.5, severe 1.0 g. Placeholders until the
    /// unit is confirmed live — they are in G, so a wrong unit guess scales them all.</summary>
    public static TurbulenceThresholds Default { get; } = new(0.2, 0.5, 1.0);
}

/// <summary>What the detector saw on one sample.</summary>
/// <param name="LevelChanged">The level is new on this sample (the event edge).</param>
/// <param name="EpisodeEnded">Two minutes below Light have passed since the last Light-or-worse — one edge per episode.</param>
public readonly record struct TurbulenceStep(bool LevelChanged, bool EpisodeEnded);

/// <summary>
/// Pure turbulence detector over the 100 ms tier (issue #186): a rolling two-second window on
/// vertical acceleration, the level from the largest deviation from the baseline in the window,
/// hysteresis on the way down, a minimum gap between level changes, and the episode bookkeeping
/// the cabin reactions depend on. The baseline is learned at rest (parked, not moving, live value)
/// — its size names the unit — and otherwise from a slow average in flight (level flight is
/// one G too). Clock in the sample; no timers.
/// </summary>
public sealed class TurbulenceDetector
{
    /// <summary>The rolling window the level is judged over.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(2);

    /// <summary>No level change sooner than this after the previous one.</summary>
    public static readonly TimeSpan MinimumGap = TimeSpan.FromSeconds(60);

    /// <summary>Below Light for this long = the episode is over.</summary>
    public static readonly TimeSpan EpisodeTail = TimeSpan.FromMinutes(2);

    /// <summary>A level is left only once the metric is under 70 % of its threshold.</summary>
    public const double HysteresisFactor = 0.7;

    /// <summary>At-rest samples needed before the baseline counts as calibrated (5 s).</summary>
    public const int CalibrationSamples = 50;

    private const double StandardGravityMps2 = 9.80665;
    private const double StandardGravityFtps2 = 32.174;
    private const double AtRestGroundSpeedKt = 2.0;
    private const double InFlightBaselineTauSeconds = 30.0;

    private readonly Queue<(DateTimeOffset At, double Raw)> _window = new();
    private readonly TurbulenceThresholds _thresholds;
    private readonly AccelerationUnit _forcedUnit;
    private double _restSum;
    private int _restCount;
    private double? _restBaseline;
    private double? _flightBaseline;
    private DateTimeOffset? _lastSampleAt;
    private DateTimeOffset? _lastChangeAt;
    private DateTimeOffset? _belowLightSince;
    private bool _episodeActive;

    public TurbulenceDetector(TurbulenceThresholds? thresholds = null, AccelerationUnit forcedUnit = AccelerationUnit.Unknown)
    {
        _thresholds = thresholds ?? TurbulenceThresholds.Default;
        _forcedUnit = forcedUnit;
    }

    public TurbulenceLevel Level { get; private set; }

    /// <summary>Largest deviation from the baseline in the window, G (0 when not judged).</summary>
    public double PeakG { get; private set; }

    /// <summary>The baseline in the raw unit; null until learned.</summary>
    public double? BaselineRaw => _restBaseline ?? _flightBaseline;

    /// <summary>True once the baseline came from at-rest samples (the trustworthy kind).</summary>
    public bool Calibrated => _restBaseline is not null;

    /// <summary>The unit in force: the override, else the guess from the baseline.</summary>
    public AccelerationUnit Unit => _forcedUnit != AccelerationUnit.Unknown ? _forcedUnit : GuessUnit(BaselineRaw);

    /// <summary>True from the first Light-or-worse until <see cref="EpisodeTail"/> below Light.</summary>
    public bool EpisodeActive => _episodeActive;

    public DateTimeOffset? EpisodeStartedAtUtc { get; private set; }

    public DateTimeOffset? LastChangeAtUtc => _lastChangeAt;

    /// <summary>The unit a baseline of this size implies; Unknown when it fits none.</summary>
    public static AccelerationUnit GuessUnit(double? baselineRaw)
    {
        if (baselineRaw is not { } b || !double.IsFinite(b))
        {
            return AccelerationUnit.Unknown;
        }

        var size = Math.Abs(b);
        return size switch
        {
            >= 0.8 and <= 1.2 => AccelerationUnit.G,
            >= 8.5 and <= 11.0 => AccelerationUnit.MetresPerSecondSquared,
            >= 28.0 and <= 36.0 => AccelerationUnit.FeetPerSecondSquared,
            < 0.2 => AccelerationUnit.GWithoutGravity,
            _ => AccelerationUnit.Unknown,
        };
    }

    /// <summary>Raw-unit → G factor for a unit.</summary>
    public static double ScaleToG(AccelerationUnit unit) => unit switch
    {
        AccelerationUnit.MetresPerSecondSquared => 1.0 / StandardGravityMps2,
        AccelerationUnit.FeetPerSecondSquared => 1.0 / StandardGravityFtps2,
        _ => 1.0,
    };

    /// <summary>The level a deviation (G) maps to without hysteresis.</summary>
    public static TurbulenceLevel LevelFor(double deviationG, TurbulenceThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        if (deviationG >= thresholds.SevereG)
        {
            return TurbulenceLevel.Severe;
        }

        if (deviationG >= thresholds.ModerateG)
        {
            return TurbulenceLevel.Moderate;
        }

        return deviationG >= thresholds.LightG ? TurbulenceLevel.Light : TurbulenceLevel.None;
    }

    /// <summary>One sample in; the edges out.</summary>
    public TurbulenceStep Process(TurbulenceSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var now = sample.AtUtc;
        var raw = sample.RawAccelerationY is { } r && double.IsFinite(r) ? r : (double?)null;

        if (raw is { } value)
        {
            Learn(value, sample, now);
            _window.Enqueue((now, value));
        }

        while (_window.Count > 0 && now - _window.Peek().At > Window)
        {
            _window.Dequeue();
        }

        _lastSampleAt = now;

        var levelChanged = false;
        if (!sample.Airborne || BaselineRaw is not { } baseline || _window.Count == 0)
        {
            PeakG = 0;
            levelChanged = Settle(TurbulenceLevel.None, now, force: !sample.Airborne);
        }
        else
        {
            var scale = ScaleToG(Unit);
            var peak = 0.0;
            foreach (var (_, v) in _window)
            {
                peak = Math.Max(peak, Math.Abs(v - baseline) * scale);
            }

            PeakG = peak;
            var candidate = LevelFor(peak, _thresholds);
            if (candidate < Level && !ClearOf(Level, peak))
            {
                candidate = Level; // hysteresis: the metric must fall well under the band to leave it
            }

            levelChanged = Settle(candidate, now, force: false);
        }

        // Episode bookkeeping on the SETTLED level.
        var episodeEnded = false;
        if (Level != TurbulenceLevel.None)
        {
            _belowLightSince = null;
            if (!_episodeActive)
            {
                _episodeActive = true;
                EpisodeStartedAtUtc = now;
            }
        }
        else if (_episodeActive)
        {
            _belowLightSince ??= now;
            if (now - _belowLightSince.Value >= EpisodeTail)
            {
                _episodeActive = false;
                _belowLightSince = null;
                episodeEnded = true;
            }
        }

        return new TurbulenceStep(levelChanged, episodeEnded);
    }

    /// <summary>Forget everything (a new sim session).</summary>
    public void Reset()
    {
        _window.Clear();
        _restSum = 0;
        _restCount = 0;
        _restBaseline = null;
        _flightBaseline = null;
        _lastSampleAt = null;
        _lastChangeAt = null;
        _belowLightSince = null;
        _episodeActive = false;
        EpisodeStartedAtUtc = null;
        Level = TurbulenceLevel.None;
        PeakG = 0;
    }

    private bool Settle(TurbulenceLevel candidate, DateTimeOffset now, bool force)
    {
        if (candidate == Level)
        {
            return false;
        }

        if (!force && _lastChangeAt is { } last && now - last < MinimumGap)
        {
            return false;
        }

        Level = candidate;
        _lastChangeAt = now;
        return true;
    }

    private bool ClearOf(TurbulenceLevel level, double peakG)
    {
        var threshold = level switch
        {
            TurbulenceLevel.Severe => _thresholds.SevereG,
            TurbulenceLevel.Moderate => _thresholds.ModerateG,
            TurbulenceLevel.Light => _thresholds.LightG,
            _ => 0,
        };
        return peakG < threshold * HysteresisFactor;
    }

    private void Learn(double raw, TurbulenceSample sample, DateTimeOffset now)
    {
        if (sample.OnGround && sample.GroundSpeedKt < AtRestGroundSpeedKt)
        {
            _restSum += raw;
            _restCount++;
            if (_restCount >= CalibrationSamples)
            {
                _restBaseline = _restSum / _restCount;
            }

            return;
        }

        if (_restBaseline is not null || !sample.Airborne)
        {
            return;
        }

        // In flight without a rest baseline: a slow average (level flight is one G too).
        if (_flightBaseline is not { } current || _lastSampleAt is not { } previous)
        {
            _flightBaseline = raw;
            return;
        }

        var dt = Math.Clamp((now - previous).TotalSeconds, 0.01, 5.0);
        var alpha = dt / (InFlightBaselineTauSeconds + dt);
        _flightBaseline = current + alpha * (raw - current);
    }
}
