using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Airports.Parking;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Radios;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Speech.SayIntentions;

/// <summary>
/// The push-and-start clearance over SayIntentions (owner request 2026-11-07): the
/// beacon-orchestrated pushback asks ATC through <see cref="IPushClearanceRadio"/> before it
/// calls the GSX tug, and this class is that radio. The request is the catalog's "request push
/// and start" exactly as the captain's voice would send it (acknowledgement, radio-clear gate,
/// the FO's voice, sayAs); the answer comes from the comms-history poll
/// (<see cref="SayIntentionsFrequencyMonitor.AtcTransmissionObserved"/>) and is read by the
/// pure <see cref="PushClearanceTracker"/>. An approval that names a direction ("Face South")
/// becomes this flight's pushback choice (<see cref="PushbackChoiceStore"/>) so GSX's direction
/// menu is answered with what ATC said, not a guess. A request the captain made by voice counts
/// too (<see cref="SayIntentionsService.RequestTransmitted"/>), so the sequence never asks twice.
/// Reset with the flight cycle.
/// </summary>
public sealed class PushClearanceRadio : IPushClearanceRadio, Core.Hosting.IStartupModule, IDisposable
{
    private const string RequestPhrase = "request push and start";

    private readonly SayIntentionsService _service;
    private readonly SayIntentionsFrequencyMonitor? _monitor;
    private readonly PushbackChoiceStore? _pushback;
    private readonly GroundOpsSignals _signals;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<PushClearanceRadio> _logger;
    private readonly PushClearanceTracker _tracker = new();
    private readonly object _gate = new();

    public PushClearanceRadio(
        SayIntentionsService service,
        GroundOpsSignals signals,
        JsonlEventLog eventLog,
        ILogger<PushClearanceRadio> logger,
        SayIntentionsFrequencyMonitor? monitor = null,
        PushbackChoiceStore? pushback = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);
        _service = service;
        _signals = signals;
        _eventLog = eventLog;
        _logger = logger;
        _monitor = monitor;
        _pushback = pushback;
    }

    public bool Available => _service.CanTransmitRequests;

    public PushClearance Current
    {
        get
        {
            lock (_gate)
            {
                return _tracker.Current;
            }
        }
    }

    public void Start()
    {
        _service.RequestTransmitted += OnRequestTransmitted;
        _signals.FlightCycleReset += OnFlightCycleReset;
        if (_monitor is not null)
        {
            _monitor.AtcTransmissionObserved += OnAtcTransmission;
        }
    }

    public void Dispose()
    {
        _service.RequestTransmitted -= OnRequestTransmitted;
        _signals.FlightCycleReset -= OnFlightCycleReset;
        if (_monitor is not null)
        {
            _monitor.AtcTransmissionObserved -= OnAtcTransmission;
        }
    }

    public async Task<bool> RequestPushAndStartAsync(CancellationToken cancellationToken = default)
    {
        var sent = await _service.TransmitRequestByPhraseAsync(RequestPhrase).ConfigureAwait(false);
        if (!sent)
        {
            _logger.LogInformation("Push clearance: the request could not be transmitted (SayIntentions off, no flight or no key)");
            _eventLog.Record("sayintentions.push-clearance", new { state = "not-sent", source = "beacon" });
        }

        return sent;
    }

    private void OnRequestTransmitted(AtcRequestDefinition request)
    {
        if (!request.Phrases.Any(p => p.Equals(RequestPhrase, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        PushClearance now;
        lock (_gate)
        {
            now = _tracker.NoteRequested(DateTimeOffset.UtcNow);
        }

        _logger.LogInformation("Push clearance: {State} — request push and start on COM1", now.State);
        _eventLog.Record("sayintentions.push-clearance", new { state = now.State.ToString(), text = (string?)null, facingDeg = (double?)null });
    }

    private void OnAtcTransmission(SayIntentionsFrequencyMonitor.AtcTransmission tx)
    {
        PushClearance? changed;
        lock (_gate)
        {
            changed = _tracker.OnAtcLine(tx.Text, DateTimeOffset.UtcNow);
        }

        if (changed is null)
        {
            return;
        }

        _logger.LogInformation("Push clearance: {State} — \"{Atc}\"{Facing}", changed.State, changed.AtcText,
            changed.FacingDeg is { } deg ? $" (facing {Compass.Name(deg)})" : "");
        _eventLog.Record("sayintentions.push-clearance", new
        {
            state = changed.State.ToString(),
            text = changed.AtcText,
            facingDeg = changed.FacingDeg,
        });

        if (changed.State == PushClearanceState.Approved && changed.FacingDeg is { } facing)
        {
            PassDirectionToGsx(facing, changed.AtcText!);
        }
    }

    /// <summary>ATC's direction becomes the flight's pushback choice: the GSX direction menu is
    /// answered with it (the advisor maps the bearing onto the stand's menu lines).</summary>
    private void PassDirectionToGsx(double facingDeg, string atcText)
    {
        if (_pushback is null)
        {
            return;
        }

        try
        {
            var current = _pushback.Snapshot().Choice;
            if (current is { Wish: PushbackWish.Heading, HeadingDeg: { } heading } && Math.Abs(heading - facingDeg) < 1)
            {
                return; // already the captain's choice
            }

            var name = Compass.Name(facingDeg);
            _pushback.Choose(PushbackChoice.Facing(facingDeg, "atc", $"ATC: \"{atcText}\" — facing {name}"));
            _logger.LogInformation("Push clearance: pushback facing {Facing} handed to GSX from ATC's approval", name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Push clearance: could not hand ATC's direction to the pushback choice");
        }
    }

    private void OnFlightCycleReset(FlightCycleResetKind _)
    {
        lock (_gate)
        {
            _tracker.Reset();
        }
    }
}
