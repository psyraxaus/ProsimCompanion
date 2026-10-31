using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Weather;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Briefings;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Callouts;

/// <summary>
/// The shell around <see cref="TransitionReminderCore"/> (issue #198): a 1 Hz tick feeds it
/// the pilot's PFD altitude, the pilot's own STD gate (seat-mapped — the captain's EFIS when
/// the human sits left, the FO's when right), the departure transition altitude and the
/// destination transition level from the Navigraph DFD (looked up once per OFP origin/
/// destination pair, so a diversion re-reads), and the destination QNH from the Flight Status
/// weather card. A reminder is Normal-priority speech tagged <c>transition.reminder</c> and a
/// <c>transition.reminder</c> event. Off by default (<c>sop.transition.enabled</c>); without a
/// DFD it logs once and stays silent — never a guessed figure.
/// </summary>
public sealed class TransitionReminderService : Core.Hosting.IStartupModule, IDisposable
{
    public const string ReminderEvent = "transition.reminder";
    public const string Tag = "transition.reminder";
    private static readonly TimeSpan SpokenTtl = TimeSpan.FromSeconds(20);

    private readonly IOptionsMonitor<SopOptions> _options;
    private readonly IDataRefSubscription<bool> _pilotStd;
    private readonly IFlightDataSource _source;
    private readonly IFlightPhaseSource _flight;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly GroundOpsSignals _signals;
    private readonly ILogger<TransitionReminderService> _logger;
    private readonly OfpStore? _ofp;
    private readonly HeroWeatherStore? _weather;
    private readonly DfdNavDataProvider? _navData;
    private readonly TransitionReminderCore _core = new();
    private readonly object _gate = new();

    private Timer? _timer;
    private int _ticking;
    private bool _disabledLogged;
    private bool _noDfdLogged;
    private (string Origin, string Destination)? _navFor;
    private double? _transitionAltitudeFt;
    private double? _transitionLevel;

    public TransitionReminderService(
        IOptionsMonitor<SopOptions> options,
        IOptionsMonitor<SpeechOptions> speech,
        IProsimDataRefs dataRefs,
        IFlightDataSource source,
        IFlightPhaseSource flight,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        GroundOpsSignals signals,
        ILogger<TransitionReminderService> logger,
        OfpStore? ofp = null,
        HeroWeatherStore? weather = null,
        DfdNavDataProvider? navData = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(speech);
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _source = source;
        _flight = flight;
        _arbiter = arbiter;
        _eventLog = eventLog;
        _signals = signals;
        _logger = logger;
        _ofp = ofp;
        _weather = weather;
        _navData = navData;

        // The pilot's own side, read once: the seat is fixed for the session (as for the FCU).
        var rightSeat = PilotSeatMap.HumanIsRightSeat(speech.CurrentValue);
        _pilotStd = dataRefs.Subscribe(PilotSeatMap.Map(ProsimDataRefNames.Efis1BaroStdGate, rightSeat));
    }

    /// <summary>The clock, injectable for deterministic tests.</summary>
    internal Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;

    public void Start()
    {
        _signals.FlightCycleReset += OnReset;
        _flight.SessionReset += OnReset;
        if (!_options.CurrentValue.Transition.Enabled)
        {
            _logger.LogInformation("Transition reminders disabled (sop.transition.enabled)");
            _disabledLogged = true;
        }

        _timer = new Timer(_ => Tick(), null, 2000, 1000);
    }

    public void Dispose()
    {
        _signals.FlightCycleReset -= OnReset;
        _flight.SessionReset -= OnReset;
        _timer?.Dispose();
        _pilotStd.Dispose();
    }

    /// <summary>One evaluation step — public so tests drive it without the timer.</summary>
    public TransitionReminder? ProcessTick(DateTimeOffset now)
    {
        var transition = _options.CurrentValue.Transition;
        if (!transition.Enabled)
        {
            if (!_disabledLogged)
            {
                _logger.LogInformation("Transition reminders disabled (sop.transition.enabled)");
                _disabledLogged = true;
            }

            return null;
        }

        _disabledLogged = false;
        var sample = _source.Sample();
        var live = _flight.IsLive && sample.IsValid;
        RefreshNavData();

        TransitionReminder? reminder;
        lock (_gate)
        {
            var input = new TransitionReminderInput(
                _flight.CurrentPhase,
                live,
                true,
                sample.SpokenAltitudeFt,
                _pilotStd.RawValue is null ? null : _pilotStd.Value,
                _transitionAltitudeFt,
                _transitionLevel,
                DestinationQnh(),
                now);
            reminder = _core.Process(input, TimeSpan.FromSeconds(Math.Max(0, transition.DelaySeconds)));
        }

        if (reminder is null)
        {
            return null;
        }

        _logger.LogInformation(
            "Transition reminder {Kind}: PFD {Altitude:F0} ft, threshold {Threshold:F0} ft, QNH {Qnh}",
            reminder.Kind, reminder.AltitudeFt, reminder.ThresholdFt, reminder.QnhHpa);
        _eventLog.Record(ReminderEvent, new
        {
            kind = reminder.Kind == TransitionReminderKind.Standard ? "standard" : "qnh",
            altitudeFt = Math.Round(reminder.AltitudeFt),
            thresholdFt = Math.Round(reminder.ThresholdFt),
            qnhHpa = reminder.QnhHpa,
            delaySeconds = transition.DelaySeconds,
            origin = _navFor?.Origin,
            destination = _navFor?.Destination,
            text = reminder.Text,
        });
        _ = _arbiter.EnqueueAsync(new SpeechRequest(reminder.Text, SpeechPriority.Normal, SpokenTtl, Tag: Tag));
        return reminder;
    }

    /// <summary>Re-reads the DFD figures when the OFP's origin/destination pair changes (a
    /// diversion included). No OFP or no DFD leaves both null — the core then stays silent.</summary>
    private void RefreshNavData()
    {
        var ofp = _ofp?.Current;
        var origin = ofp?.OriginIcao?.Trim().ToUpperInvariant() ?? "";
        var destination = ofp?.DestinationIcao?.Trim().ToUpperInvariant() ?? "";
        if (_navFor is { } current && current.Origin == origin && current.Destination == destination)
        {
            return;
        }

        _navFor = (origin, destination);
        _transitionAltitudeFt = null;
        _transitionLevel = null;
        if (origin.Length == 0 && destination.Length == 0)
        {
            return;
        }

        if (_navData is null || !_navData.IsConfigured)
        {
            if (!_noDfdLogged)
            {
                _logger.LogWarning("Transition reminders: no Navigraph DFD configured — no transition altitude or level known, nothing will be said");
                _noDfdLogged = true;
            }

            return;
        }

        if (origin.Length > 0)
        {
            _transitionAltitudeFt = _navData.Lookup(origin, null).TransitionAltitudeFt;
        }

        if (destination.Length > 0)
        {
            _transitionLevel = _navData.Lookup(destination, null).TransitionLevel;
        }

        _logger.LogInformation(
            "Transition reminders: {Origin} TA {TransitionAltitude} ft, {Destination} TL {TransitionLevel}",
            origin, _transitionAltitudeFt, destination, _transitionLevel);
    }

    /// <summary>The destination QNH from whichever Flight Status card shows the destination
    /// (the local card from descent onward, the second card before).</summary>
    private double? DestinationQnh()
    {
        if (_weather is null || _navFor is not { } nav || nav.Destination.Length == 0)
        {
            return null;
        }

        var snapshot = _weather.Snapshot();
        foreach (var card in new[] { snapshot.Local, snapshot.Second })
        {
            if (card.Status == WxProbeStatus.Found && string.Equals(card.Icao, nav.Destination, StringComparison.OrdinalIgnoreCase))
            {
                return card.Facts.QnhHpa;
            }
        }

        return null;
    }

    private void OnReset()
    {
        lock (_gate)
        {
            _core.Reset();
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
            ProcessTick(Clock());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transition reminder tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }
}
