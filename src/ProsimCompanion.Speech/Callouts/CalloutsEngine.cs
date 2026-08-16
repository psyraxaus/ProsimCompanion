using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Arbiter;

namespace ProsimCompanion.Speech.Callouts;

/// <summary>
/// SOP flight-deck callouts, semantics carried from Prosim2FO's proven engine: the FO speaks
/// only what a human PM calls (thrust set, one hundred, V1, rotate, positive climb, altitude
/// crossings, 1000/500 on final, one hundred above/minimums, rollout calls, placard-speed
/// advisories). Deliberately NO radio-altimeter countdown or GPWS-style calls — those remain
/// ProSim's own; overlap is avoided by omission, and the near-overlapping 1000/500 calls are
/// individually disableable. Everything is advisory: this engine only speaks, never commands.
///
/// Crossing detection compares previous vs current sample, so <see cref="ProcessSample"/> is
/// public and deterministic for tests; the internal timer merely feeds it. An invalid sample
/// is skipped INCLUDING the crossing history, so a reconnect can't fabricate a threshold
/// crossing. Minima come exclusively from the crew-entered <see cref="ArrivalMinimaStore"/> —
/// never guessed (there is no DH/MDA dataref).
/// </summary>
public sealed class CalloutsEngine : Core.Hosting.IStartupModule, IDisposable
{
    private const double StdTtlSec = 3.0;   // High callouts
    private const double CritTtlSec = 4.0;  // Critical callouts

    private readonly IOptionsMonitor<SopOptions> _options;
    private readonly ISpeechArbiter _arbiter;
    private readonly IFlightDataSource _source;
    private readonly IFlightPhaseSource _flight;
    private readonly ArrivalMinimaStore _minima;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<CalloutsEngine> _logger;
    private readonly object _lock = new();

    private Timer? _timer;
    private int _ticking;

    // One-shot latches, re-armed by phase transitions (see OnPhaseChanged).
    private bool _thrustSet;
    private bool _s100;
    private bool _v1;
    private bool _rotate;
    private bool _v2;
    private bool _positiveClimb;
    private readonly HashSet<int> _firedAltCallouts = [];
    private double? _toGoFiredAlt;
    private bool _a1000;
    private bool _a500;
    private bool _hundredAbove;
    private bool _minimumsFired;
    private bool _spoilers;
    private bool _reverse;
    private bool _decel;
    private readonly Dictionary<string, long> _placardCooldown = [];
    private readonly HashSet<string> _warned = [];

    // Crossing memory; NaN until the first valid sample after arming.
    private double _prevRadio = double.NaN;
    private double _prevBaro = double.NaN;
    private double _prevIas = double.NaN;

    public CalloutsEngine(
        IOptionsMonitor<SopOptions> options,
        ISpeechArbiter arbiter,
        IFlightDataSource source,
        IFlightPhaseSource flight,
        ArrivalMinimaStore minima,
        JsonlEventLog eventLog,
        ILogger<CalloutsEngine> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(minima);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _arbiter = arbiter;
        _source = source;
        _flight = flight;
        _minima = minima;
        _eventLog = eventLog;
        _logger = logger;
    }

    /// <summary>Starts sampling. The poll interval is read once here (floored to 50 ms);
    /// the enabled flag hot-toggles per tick.</summary>
    public void Start()
    {
        _flight.PhaseChanged += OnPhaseChanged;
        var period = Math.Max(50, _options.CurrentValue.CalloutPollIntervalMs);
        _timer = new Timer(_ => Tick(), null, 0, period);
    }

    public void Dispose()
    {
        _flight.PhaseChanged -= OnPhaseChanged;
        _timer?.Dispose();
    }

    /// <summary>One evaluation step — public so tests/replay can stage deterministic
    /// two-sample crossings; the timer calls this with a live sample.</summary>
    public void ProcessSample(FlightDataSnapshot s)
    {
        ArgumentNullException.ThrowIfNull(s);

        lock (_lock)
        {
            var sop = _options.CurrentValue;
            if (!sop.CalloutsEnabled || !s.IsValid)
            {
                return;
            }

            var phase = _flight.CurrentPhase;
            if (phase is FlightPhase.TakeoffRoll or FlightPhase.InitialClimb)
            {
                Takeoff(sop, s);
            }

            if (phase is FlightPhase.InitialClimb or FlightPhase.Climb)
            {
                PositiveClimb(sop, s);
            }

            if (InFlight(phase))
            {
                AltitudeCallouts(sop, s);
                ToGo(sop, s);
                Placards(sop, s);
            }

            if (phase is FlightPhase.Approach or FlightPhase.Descent)
            {
                ApproachCallouts(sop, s);
            }

            if (phase == FlightPhase.LandingRollout)
            {
                Rollout(sop, s);
            }

            _prevRadio = s.RadioAltitudeFt;
            _prevBaro = s.AltitudeFt;
            _prevIas = s.IndicatedAirspeedKt;
        }
    }

    private void OnPhaseChanged(object? sender, FlightPhaseChangedEventArgs e)
    {
        lock (_lock)
        {
            switch (e.Current)
            {
                case FlightPhase.ColdAndDark:
                case FlightPhase.Preflight:
                case FlightPhase.TakeoffRoll:
                    FullReset();
                    break;

                case FlightPhase.InitialClimb:
                    // Go-around: re-arm approach + climb groups; takeoff latches stay set
                    // (no second "V1").
                    if (e.Previous is FlightPhase.Approach or FlightPhase.LandingRollout)
                    {
                        ResetApproach();
                        ResetClimbCallouts();
                    }

                    _positiveClimb = false; // arm "positive climb" after lift-off
                    break;

                case FlightPhase.Approach:
                    ResetApproach();
                    break;

                case FlightPhase.LandingRollout:
                    ResetRollout();
                    break;
            }
        }
    }

    private void FullReset()
    {
        _thrustSet = _s100 = _v1 = _rotate = _v2 = _positiveClimb = false;
        ResetClimbCallouts();
        ResetApproach();
        ResetRollout();
        _placardCooldown.Clear();
        _warned.Clear();
        _prevRadio = _prevBaro = _prevIas = double.NaN;
    }

    private void ResetClimbCallouts()
    {
        _firedAltCallouts.Clear();
        _toGoFiredAlt = null;
    }

    private void ResetApproach() => _a1000 = _a500 = _hundredAbove = _minimumsFired = false;

    private void ResetRollout() => _spoilers = _reverse = _decel = false;

    private static bool InFlight(FlightPhase phase)
        => phase is FlightPhase.InitialClimb or FlightPhase.Climb or FlightPhase.Cruise
            or FlightPhase.Descent or FlightPhase.Approach;

    // ---- Takeoff roll ----

    private void Takeoff(SopOptions sop, FlightDataSnapshot s)
    {
        var ias = s.IndicatedAirspeedKt;

        // ias < 80 so a late N1 recovery mid-roll doesn't call "thrust set" at 130 kt.
        if (!_thrustSet && sop.ThrustSet.Enabled && ias < 80)
        {
            var target = s.FlexN1Target > 0 ? s.FlexN1Target : s.TogaN1Target;
            if (target <= 0)
            {
                WarnOnce("thrustSet", "\"thrust set\" callout disabled — no FLEX/TOGA N1 target available");
            }
            else if (s.AverageN1Percent >= target - 2)
            {
                _thrustSet = true;
                Fire("thrustSet", sop.ThrustSet.Text, SpeechPriority.High, StdTtlSec);
            }
        }

        // Predecessor quirk preserved: no Enabled check at this call site — a disabled
        // hundredKnots still consumes its latch; Fire() gates the speech.
        if (!_s100 && ias >= sop.HundredKnots.SpeedKt)
        {
            _s100 = true;
            FireIfEnabled("hundredKnots", sop.HundredKnots, SpeechPriority.High, StdTtlSec);
        }

        if (!_v1 && sop.V1.Enabled)
        {
            if (s.V1Kt <= 0)
            {
                WarnOnce("v1", "\"V one\" callout disabled — V1 not entered in the FMS");
            }
            else if (ias >= s.V1Kt)
            {
                _v1 = true;
                Fire("v1", sop.V1.Text, SpeechPriority.Critical, CritTtlSec);
            }
        }

        if (!_rotate && sop.Rotate.Enabled)
        {
            if (s.VrKt <= 0)
            {
                WarnOnce("rotate", "\"rotate\" callout disabled — VR not entered in the FMS");
            }
            else if (ias >= s.VrKt)
            {
                _rotate = true;
                Fire("rotate", sop.Rotate.Text, SpeechPriority.High, StdTtlSec);
            }
        }

        if (!_v2 && sop.V2.Enabled && !s.OnGround && s.V2Kt > 0 && ias >= s.V2Kt)
        {
            _v2 = true;
            Fire("v2", sop.V2.Text, SpeechPriority.High, StdTtlSec);
        }
    }

    private void PositiveClimb(SopOptions sop, FlightDataSnapshot s)
    {
        // Three independent confirmations + a rising radio altitude — deliberately cannot
        // fire on the first pass after arming.
        if (!_positiveClimb && sop.PositiveClimb.Enabled
            && !s.OnGround
            && s.VerticalSpeedFpm > 100
            && s.RadioAltitudeFt > 10
            && !double.IsNaN(_prevRadio)
            && s.RadioAltitudeFt > _prevRadio)
        {
            _positiveClimb = true;
            Fire("positiveClimb", sop.PositiveClimb.Text, SpeechPriority.High, StdTtlSec);
        }
    }

    // ---- Enroute ----

    private void AltitudeCallouts(SopOptions sop, FlightDataSnapshot s)
    {
        if (double.IsNaN(_prevBaro))
        {
            return;
        }

        var baro = s.AltitudeFt;
        var climbing = baro > _prevBaro;
        foreach (var callout in sop.AltitudeCallouts)
        {
            if (!callout.Enabled || _firedAltCallouts.Contains(callout.AtFt))
            {
                continue;
            }

            var up = climbing && _prevBaro < callout.AtFt && baro >= callout.AtFt
                && callout.Direction != AltitudeCalloutDirection.Descent;
            var down = !climbing && _prevBaro > callout.AtFt && baro <= callout.AtFt
                && callout.Direction != AltitudeCalloutDirection.Climb;
            if (up || down)
            {
                _firedAltCallouts.Add(callout.AtFt);
                Fire($"altitude:{callout.AtFt}", callout.Text, SpeechPriority.High, StdTtlSec);
            }
        }
    }

    private void ToGo(SopOptions sop, FlightDataSnapshot s)
    {
        var toGo = sop.OneThousandToGo;
        if (!toGo.Enabled || s.FcuAltitudeFt <= 0)
        {
            return;
        }

        var fcu = s.FcuAltitudeFt;
        var gap = Math.Abs(fcu - s.AltitudeFt);

        // Re-arm with a 200 ft hysteresis band AND a genuinely different FCU target.
        if (_toGoFiredAlt is { } fired
            && gap > toGo.WithinFt + 200
            && Math.Abs(fired - fcu) > toGo.WithinFt)
        {
            _toGoFiredAlt = null;
        }

        var toward = (fcu > s.AltitudeFt && s.VerticalSpeedFpm > 100)
            || (fcu < s.AltitudeFt && s.VerticalSpeedFpm < -100);
        // A target change of > 50 ft counts as a new target.
        if (gap <= toGo.WithinFt && toward
            && (_toGoFiredAlt is null || Math.Abs(_toGoFiredAlt.Value - fcu) > 50))
        {
            _toGoFiredAlt = fcu;
            Fire("oneThousandToGo", toGo.Text, SpeechPriority.High, StdTtlSec);
        }
    }

    // ---- Approach ----

    private void ApproachCallouts(SopOptions sop, FlightDataSnapshot s)
    {
        var radio = s.RadioAltitudeFt;
        var descending = !double.IsNaN(_prevRadio) && radio < _prevRadio;

        if (!_a1000 && sop.OneThousand.Enabled && descending
            && _prevRadio > sop.OneThousand.RaFt && radio <= sop.OneThousand.RaFt)
        {
            _a1000 = true;
            Fire("oneThousand", sop.OneThousand.Text, SpeechPriority.High, StdTtlSec);
        }

        if (!_a500 && sop.FiveHundred.Enabled && descending
            && _prevRadio > sop.FiveHundred.RaFt && radio <= sop.FiveHundred.RaFt)
        {
            _a500 = true;
            Fire("fiveHundred", sop.FiveHundred.Text, SpeechPriority.High, StdTtlSec);
        }

        Minimums(sop, s);
    }

    private void Minimums(SopOptions sop, FlightDataSnapshot s)
    {
        if (_minimumsFired && _hundredAbove)
        {
            return;
        }

        var minima = _minima.Current;
        if (minima is null)
        {
            WarnOnce("minima",
                "\"minimums\"/\"one hundred above\" callouts disabled — arrival minima not entered (Speech page)");
            return;
        }

        // DA/MDA are MSL → baro altitude; DH is a radio height → radio altitude.
        var minAlt = minima.AltitudeFt;
        var msl = minima.Kind is ArrivalMinimumKind.DecisionAltitude
            or ArrivalMinimumKind.MinimumDescentAltitude;
        var cur = msl ? s.AltitudeFt : s.RadioAltitudeFt;
        var prev = msl ? _prevBaro : _prevRadio;
        if (double.IsNaN(prev))
        {
            return;
        }

        if (!_hundredAbove && sop.HundredAbove.Enabled && prev > minAlt + 100 && cur <= minAlt + 100)
        {
            _hundredAbove = true;
            Fire("hundredAbove", sop.HundredAbove.Text, SpeechPriority.Critical, CritTtlSec);
        }

        if (!_minimumsFired && sop.Minimums.Enabled && prev > minAlt && cur <= minAlt)
        {
            _minimumsFired = true;
            Fire("minimums", sop.Minimums.Text, SpeechPriority.Critical, CritTtlSec);
        }
    }

    // ---- Rollout ----

    private void Rollout(SopOptions sop, FlightDataSnapshot s)
    {
        if (!_spoilers && sop.Spoilers.Enabled && s.GroundSpoilersDeployed)
        {
            _spoilers = true;
            Fire("spoilers", sop.Spoilers.Text, SpeechPriority.High, StdTtlSec);
        }

        if (!_reverse && sop.ReverseGreen.Enabled && s.ReversersMaxBoth)
        {
            _reverse = true;
            Fire("reverseGreen", sop.ReverseGreen.Text, SpeechPriority.High, StdTtlSec);
        }

        // ias > 20 so the call doesn't fire again as the aircraft stops.
        var ias = s.IndicatedAirspeedKt;
        if (!_decel && sop.DecelSpeed.Enabled && ias <= sop.DecelSpeed.SpeedKt && ias > 20)
        {
            _decel = true;
            Fire("decelSpeed", sop.DecelSpeed.Text, SpeechPriority.High, StdTtlSec);
        }
    }

    // ---- Placard-speed advisories ----

    private void Placards(SopOptions sop, FlightDataSnapshot s)
    {
        var pa = sop.PlacardAdvisory;
        if (!pa.Enabled || s.OnGround)
        {
            return;
        }

        var ias = s.IndicatedAirspeedKt;
        var handle = s.FlapHandle;
        if (handle > 0)
        {
            var placard = sop.FlapPlacards.FirstOrDefault(p => p.FlapHandle == handle);
            if (placard is not null)
            {
                double max = placard.MaxKt;
                if (ias > max)
                {
                    // Validity re-samples so the arbiter drops the advisory at dequeue if
                    // the speed recovered.
                    Placard("flapLimit", pa.ExceededText, pa, () =>
                    {
                        var t = _source.Sample();
                        return t.IsValid && !t.OnGround && t.IndicatedAirspeedKt > max;
                    });
                }
                else if (ias >= max - pa.WarnWithinKt && !double.IsNaN(_prevIas) && ias > _prevIas)
                {
                    // Accelerating only — decelerating into the band stays quiet.
                    Placard("flapApproach", pa.ApproachingText, pa, () =>
                    {
                        var t = _source.Sample();
                        return t.IsValid && !t.OnGround && t.IndicatedAirspeedKt >= max - pa.WarnWithinKt;
                    });
                }
            }
        }

        if (sop.GearMaxKt > 0 && s.GearDown && ias > sop.GearMaxKt)
        {
            double max = sop.GearMaxKt;
            Placard("gearLimit", pa.GearExceededText, pa, () =>
            {
                var t = _source.Sample();
                return t.IsValid && t.GearDown && t.IndicatedAirspeedKt > max;
            });
        }
    }

    private void Placard(string id, string text, PlacardAdvisoryOptions pa, Func<bool> valid)
    {
        // Cooldown stamped on attempt, not on speak.
        var now = Environment.TickCount64;
        if (_placardCooldown.TryGetValue(id, out var last) && now - last < pa.CooldownSeconds * 1000L)
        {
            return;
        }

        _placardCooldown[id] = now;
        Fire(id, text, SpeechPriority.High, StdTtlSec, valid);
    }

    // ---- Plumbing ----

    private void Fire(string id, string text, SpeechPriority priority, double ttlSec, Func<bool>? valid = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            text = id; // empty profile text falls back to the id, never silence
        }

        _eventLog.Record("callout.fired", new { id, text, priority = priority.ToString() });
        _ = _arbiter.EnqueueAsync(new SpeechRequest(
            text, priority, TimeSpan.FromSeconds(ttlSec), valid, Tag: id));
    }

    private void FireIfEnabled(string id, CalloutSetting setting, SpeechPriority priority, double ttlSec)
    {
        if (!setting.Enabled)
        {
            _eventLog.Record("callout.suppressed", new { id, reason = "disabled" });
            return;
        }

        Fire(id, setting.Text, priority, ttlSec);
    }

    /// <summary>Logged once per flight (cleared with the takeoff latches).</summary>
    private void WarnOnce(string key, string message)
    {
        if (_warned.Add(key))
        {
            _logger.LogWarning("{Message}", message);
            _eventLog.Record("callout.degraded", new { id = key, reason = message });
        }
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            ProcessSample(_source.Sample());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Callouts tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }
}
