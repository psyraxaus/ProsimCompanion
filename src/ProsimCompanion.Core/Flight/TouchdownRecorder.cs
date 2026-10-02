using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.EventLog;

namespace ProsimCompanion.Core.Flight;

/// <summary>
/// Feeds <see cref="TouchdownRecorderCore"/> every 100 ms from the cached datarefs and writes
/// the one <c>touchdown</c> session event a landing produces (issue #146). The debrief and
/// the logbook read that event back from the session log, so a flight folded later — or
/// backfilled — carries the same figures. Holds while the flight is not live (CONTEXT.md):
/// ProSim pushes plausible data with no sim attached, and a "touchdown" from it would be
/// fiction. Read-only: never writes to the aircraft. Startup module.
/// </summary>
public sealed class TouchdownRecorder : IDisposable
{
    /// <summary>The Critical dataref tier: faster buys nothing, slower loses the flare.</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>Session event type, shared with the fact extractor.</summary>
    public const string EventType = "touchdown";

    private readonly IFlightPhaseSource _flight;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<TouchdownRecorder> _logger;
    private readonly IDataRefSubscription<bool> _rawOnGround;
    private readonly IDataRefSubscription<double> _verticalSpeed;
    private readonly IDataRefSubscription<double> _ias;
    private readonly IDataRefSubscription<double> _groundSpeed;
    private readonly IDataRefSubscription<double> _pitch;
    private readonly IDataRefSubscription<double> _bank;
    private readonly IDataRefSubscription<double> _accelerationY;
    private readonly TouchdownRecorderCore _core = new();
    private readonly Timer _timer;
    private bool _wasLive;
    private int _ticking;

    public TouchdownRecorder(
        IFlightPhaseSource flight,
        IProsimDataRefs dataRefs,
        JsonlEventLog eventLog,
        ILogger<TouchdownRecorder> logger)
    {
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _flight = flight;
        _eventLog = eventLog;
        _logger = logger;

        _rawOnGround = dataRefs.Subscribe(ProsimDataRefNames.OnGroundGate);
        _verticalSpeed = dataRefs.Subscribe(ProsimDataRefNames.VerticalSpeed);
        _ias = dataRefs.Subscribe(ProsimDataRefNames.IndicatedAirspeed);
        _groundSpeed = dataRefs.Subscribe(ProsimDataRefNames.GroundSpeed);
        _pitch = dataRefs.Subscribe(ProsimDataRefNames.PitchAngle);
        _bank = dataRefs.Subscribe(ProsimDataRefNames.BankAngle);
        _accelerationY = dataRefs.Subscribe(ProsimDataRefNames.AccelerationYRaw);

        _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(2), TickInterval);
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            var view = _flight.Snapshot();
            if (!view.IsLive || view.Data is not { IsValid: true } data)
            {
                if (_wasLive)
                {
                    _wasLive = false;
                    _core.Reset();
                }

                return;
            }

            _wasLive = true;
            var report = _core.Process(new TouchdownSample(
                DateTimeOffset.UtcNow,
                _rawOnGround.Value,
                // The engine's last sample carries the COMMITTED ground contact.
                data.OnGround,
                _verticalSpeed.Value,
                _ias.Value,
                _groundSpeed.Value,
                Finite(_pitch),
                Finite(_bank),
                Finite(_accelerationY)));
            if (report is not null)
            {
                Publish(report);
            }
        }
        catch (Exception ex)
        {
            // Runs on a timer thread: anything that escapes terminates the process.
            _logger.LogWarning(ex, "Touchdown recorder tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    private void Publish(TouchdownReport report)
    {
        _logger.LogInformation(
            "Touchdown: {VerticalSpeedFpm:F0} fpm, IAS {IasKt:F0} kt, GS {GroundSpeedKt:F0} kt, pitch {PitchDeg}, bank {BankDeg}, "
            + "{Bounces} bounce(s){WentAround} — acceleration.Y raw max {AccelMax} min {AccelMin} before contact {AccelBefore} (unit unknown)",
            report.VerticalSpeedFpm, report.IasKt, report.GroundSpeedKt, report.PitchDeg, report.BankDeg,
            report.Bounces, report.WentAround ? ", then airborne again (go-around)" : "",
            report.AccelerationYRawMax, report.AccelerationYRawMin, report.AccelerationYRawBeforeContact);
        _eventLog.Record(EventType, new
        {
            contactUtc = report.ContactAt,
            verticalSpeedFpm = Math.Round(report.VerticalSpeedFpm),
            iasKt = Math.Round(report.IasKt, 1),
            groundSpeedKt = Math.Round(report.GroundSpeedKt, 1),
            pitchDeg = Round1(report.PitchDeg),
            bankDeg = Round1(report.BankDeg),
            bounces = report.Bounces,
            wentAround = report.WentAround,
            // The same fact as a word: the probe reducer keys duplicates on string fields.
            outcome = report.WentAround ? "went-around" : "landed",
            // RAW aircraft.acceleration.Y — the unit is undocumented. Do not read these as G.
            accelerationYRawMax = Round3(report.AccelerationYRawMax),
            accelerationYRawMin = Round3(report.AccelerationYRawMin),
            accelerationYRawBeforeContact = Round3(report.AccelerationYRawBeforeContact),
        });
    }

    /// <summary>Pushed, fresh and a real number — else null (the figure stays blank).</summary>
    private static double? Finite(IDataRefSubscription<double> subscription)
        => subscription.RawValue is null || subscription.IsStale || !double.IsFinite(subscription.Value)
            ? null
            : subscription.Value;

    private static double? Round1(double? value) => value is { } v ? Math.Round(v, 1) : null;

    private static double? Round3(double? value) => value is { } v ? Math.Round(v, 3) : null;

    public void Dispose()
    {
        _timer.Dispose();
        _rawOnGround.Dispose();
        _verticalSpeed.Dispose();
        _ias.Dispose();
        _groundSpeed.Dispose();
        _pitch.Dispose();
        _bank.Dispose();
        _accelerationY.Dispose();
    }
}
