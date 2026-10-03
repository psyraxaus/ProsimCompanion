using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Deice;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Hosting;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Notifications;

/// <summary>
/// Turns the Core signals and store edges into <see cref="NotificationMessage"/>s and hands
/// them to the dispatcher (issue #151). Every milestone fires ONCE per flight cycle — a second
/// "boarding completed" from a GSX re-sync, or a loadsheet re-sent as the same edition, is
/// swallowed; a <see cref="GroundOpsSignals.FlightCycleReset"/> re-arms everything. All work
/// here is a dictionary check and a channel write: a raiser is never delayed.
/// </summary>
public sealed class NotificationSignalSource : IStartupModule, IDisposable
{
    private readonly NotificationDispatcher _dispatcher;
    private readonly GroundOpsSignals _signals;
    private readonly LoadsheetStore _loadsheet;
    private readonly FlightTimesStore _times;
    private readonly OfpStore _ofp;
    private readonly DeiceHoldoverService? _holdover;
    private readonly IOptionsMonitor<NotificationOptions> _options;
    private readonly ILogger<NotificationSignalSource> _logger;
    private readonly Func<DateTimeOffset> _clock;
    private readonly IDataRefSubscription<double>? _fuel;
    private readonly object _gate = new();
    private readonly HashSet<FlightEvent> _firedThisCycle = [];
    private readonly List<IDisposable> _subscriptions = [];
    private int _lastFinalEdition;
    private bool _holdoverWarned;

    public NotificationSignalSource(
        NotificationDispatcher dispatcher,
        GroundOpsSignals signals,
        LoadsheetStore loadsheet,
        FlightTimesStore times,
        OfpStore ofp,
        IOptionsMonitor<NotificationOptions> options,
        ILogger<NotificationSignalSource> logger,
        DeiceHoldoverService? holdover = null,
        IProsimDataRefs? dataRefs = null,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(loadsheet);
        ArgumentNullException.ThrowIfNull(times);
        ArgumentNullException.ThrowIfNull(ofp);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _dispatcher = dispatcher;
        _signals = signals;
        _loadsheet = loadsheet;
        _times = times;
        _ofp = ofp;
        _holdover = holdover;
        _options = options;
        _logger = logger;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _fuel = dataRefs?.Subscribe(ProsimDataRefNames.FuelTotal);
    }

    public void Start()
    {
        _dispatcher.Start();
        _signals.RefuelCompleted += OnRefuelCompleted;
        _signals.BoardingCompleted += OnBoardingCompleted;
        _signals.DepartureServicesCompleted += OnDepartureServicesCompleted;
        _signals.CabinSecured += OnCabinSecured;
        _signals.TodApproaching += OnTodApproaching;
        _signals.ArrivalCompleted += OnDeboardingCompleted;
        _signals.FlightCycleReset += OnFlightCycleReset;
        _subscriptions.Add(_loadsheet.Observe(OnLoadsheet));
        _subscriptions.Add(_times.Observe(OnTimes));
        if (_holdover is not null)
        {
            _holdover.Changed += OnHoldoverChanged;
        }
    }

    public void Dispose()
    {
        _signals.RefuelCompleted -= OnRefuelCompleted;
        _signals.BoardingCompleted -= OnBoardingCompleted;
        _signals.DepartureServicesCompleted -= OnDepartureServicesCompleted;
        _signals.CabinSecured -= OnCabinSecured;
        _signals.TodApproaching -= OnTodApproaching;
        _signals.ArrivalCompleted -= OnDeboardingCompleted;
        _signals.FlightCycleReset -= OnFlightCycleReset;
        if (_holdover is not null)
        {
            _holdover.Changed -= OnHoldoverChanged;
        }

        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _fuel?.Dispose();
    }

    // ---- signals -----------------------------------------------------------------------------

    private void OnRefuelCompleted()
    {
        var fob = _fuel is { RawValue: not null, IsStale: false } ? _fuel.Value : (double?)null;
        var fuelText = fob is { } kg && kg > 0 ? $" Fuel on board {Tonnes(kg)} tonnes." : "";
        Raise(FlightEvent.RefuelComplete, "Refuel complete", "Refuelling finished." + fuelText,
            fob is { } f ? new Dictionary<string, object?> { ["fuelOnBoardKg"] = Math.Round(f) } : null);
    }

    private void OnBoardingCompleted()
    {
        var pax = _loadsheet.Snapshot().Final is { Status: LoadsheetSlotStatus.Sent, Pax: > 0 } final ? final.Pax : _ofp.Current?.PaxCount ?? 0;
        var paxText = pax > 0 ? $" {pax.ToString(CultureInfo.InvariantCulture)} passengers aboard." : "";
        Raise(FlightEvent.BoardingComplete, "Boarding complete", "All passengers are on board." + paxText,
            pax > 0 ? new Dictionary<string, object?> { ["paxCount"] = pax } : null);
    }

    private void OnDepartureServicesCompleted()
        => Raise(FlightEvent.ReadyForPushback, "Ready for pushback", "Every departure service is complete.");

    private void OnCabinSecured()
        => Raise(FlightEvent.CabinSecure, "Cabin secure", "The purser reports the cabin secure.");

    private void OnTodApproaching(TodApproaching tod)
        => Raise(FlightEvent.TopOfDescentApproaching, "Top of descent approaching",
            $"About {Math.Round(tod.MinutesToTod).ToString("0", CultureInfo.InvariantCulture)} minutes to the top of descent (3 to 1 estimate), {Math.Round(tod.DistanceToGoNm).ToString("0", CultureInfo.InvariantCulture)} nautical miles to go.",
            new Dictionary<string, object?>
            {
                ["minutesToTod"] = Math.Round(tod.MinutesToTod, 1),
                ["distanceToGoNm"] = Math.Round(tod.DistanceToGoNm, 1),
                ["estimate"] = true,
            });

    private void OnDeboardingCompleted()
        => Raise(FlightEvent.DeboardingComplete, "Deboarding complete", "All passengers are off the aircraft.");

    private void OnLoadsheet(LoadsheetSnapshot snapshot)
    {
        var final = snapshot.Final;
        if (final.Status != LoadsheetSlotStatus.Sent || final.EditionNumber <= 0)
        {
            return;
        }

        lock (_gate)
        {
            if (final.EditionNumber == _lastFinalEdition)
            {
                return;
            }

            _lastFinalEdition = final.EditionNumber;
            // A REVISED final is news again: the once-per-cycle latch is lifted for it.
            _firedThisCycle.Remove(FlightEvent.FinalLoadsheetSent);
        }

        Raise(FlightEvent.FinalLoadsheetSent, "Final loadsheet sent",
            $"Final loadsheet edition {final.EditionNumber.ToString(CultureInfo.InvariantCulture)}: ZFW {Tonnes(final.ZfwKg)} tonnes, TOW {Tonnes(final.TowKg)} tonnes, {final.Pax.ToString(CultureInfo.InvariantCulture)} passengers.",
            new Dictionary<string, object?>
            {
                ["edition"] = final.EditionNumber,
                ["zfwKg"] = Math.Round(final.ZfwKg),
                ["towKg"] = Math.Round(final.TowKg),
                ["fuelKg"] = Math.Round(final.FuelKg),
                ["paxCount"] = final.Pax,
            });
    }

    private void OnTimes(FlightTimesSnapshot times)
    {
        if (times.LandingUtc is { } landing)
        {
            Raise(FlightEvent.Landed, "Landed", $"Landed at {landing:HH:mm} zulu.", at: landing);
        }

        if (times.OnBlocksUtc is { } onBlocks)
        {
            var block = times.OffBlocksUtc is { } off ? $" Block time {Duration(onBlocks - off)}." : "";
            Raise(FlightEvent.OnBlocks, "On blocks", $"On blocks at {onBlocks:HH:mm} zulu.{block}", at: onBlocks,
                details: times.OffBlocksUtc is { } o ? new Dictionary<string, object?> { ["blockMinutes"] = Math.Round((onBlocks - o).TotalMinutes) } : null);
        }
    }

    private void OnHoldoverChanged(object? sender, EventArgs e)
    {
        var snapshot = _holdover!.Snapshot();
        var threshold = Math.Max(1, _options.CurrentValue.HoldoverExpiringMinutes) * 60;
        lock (_gate)
        {
            if (!snapshot.Active)
            {
                _holdoverWarned = false; // a new deice re-arms the warning
                return;
            }

            // The LOW end of the representative window is the conservative figure; one warning
            // per holdover when it drops through the threshold (or has already expired).
            if (_holdoverWarned || (snapshot.RemainingLowSeconds > threshold && !snapshot.Expired))
            {
                return;
            }

            _holdoverWarned = true;
            _firedThisCycle.Remove(FlightEvent.DeiceHoldoverExpiring); // one per holdover, not per cycle
        }

        var minutes = Math.Max(0, snapshot.RemainingLowSeconds) / 60;
        Raise(FlightEvent.DeiceHoldoverExpiring, snapshot.Expired ? "Deice holdover expired" : "Deice holdover expiring",
            snapshot.Expired
                ? $"The {snapshot.FluidLabel} holdover time has expired."
                : $"About {minutes.ToString(CultureInfo.InvariantCulture)} minutes of {snapshot.FluidLabel} holdover left.",
            new Dictionary<string, object?>
            {
                ["remainingLowSeconds"] = snapshot.RemainingLowSeconds,
                ["expired"] = snapshot.Expired,
                ["fluid"] = snapshot.FluidLabel,
            });
    }

    private void OnFlightCycleReset()
    {
        lock (_gate)
        {
            _firedThisCycle.Clear();
            _lastFinalEdition = 0;
            _holdoverWarned = false;
        }
    }

    // ---- the one gate ------------------------------------------------------------------------

    /// <summary>Queues the message unless this milestone already fired this cycle. Exposed to
    /// tests through the dispatcher's queue; returns true when handed over.</summary>
    internal bool Raise(FlightEvent flightEvent, string title, string text, IReadOnlyDictionary<string, object?>? details = null, DateTimeOffset? at = null)
    {
        lock (_gate)
        {
            if (!_firedThisCycle.Add(flightEvent))
            {
                return false;
            }
        }

        var ofp = _ofp.Current;
        var route = ofp is { OriginIcao.Length: > 0, DestinationIcao.Length: > 0 } ? $"{ofp.OriginIcao}–{ofp.DestinationIcao}" : "";
        var flight = ofp?.Callsign is { Length: > 0 } callsign ? callsign : ofp?.FlightNumber ?? "";
        var message = new NotificationMessage(flightEvent, title, text, flight, route, at ?? _clock(), details);
        var queued = _dispatcher.Enqueue(message);
        _logger.LogDebug("Milestone {Event}: {Queued}", message.EventName, queued ? "queued for notification" : "no target");
        return queued;
    }

    private static string Tonnes(double kg) => ((double)Math.Round((decimal)kg / 1000m, 1, MidpointRounding.AwayFromZero)).ToString("0.0", CultureInfo.InvariantCulture);

    private static string Duration(TimeSpan span)
        => span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h {span.Minutes:00}m"
            : $"{span.Minutes} min";
}
