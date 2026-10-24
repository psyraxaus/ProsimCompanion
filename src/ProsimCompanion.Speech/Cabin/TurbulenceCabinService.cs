using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Arbiter;

namespace ProsimCompanion.Speech.Cabin;

/// <summary>
/// The cabin's reactions to turbulence (issue #186), a 1 Hz shell over
/// <see cref="TurbulenceCabinCore"/>: the FO's seat-belt advice, the purser's call plus the
/// "secure the cabin?" captain's question, the Severe report, the secured report after the
/// captain's yes, and the back-to-normal report after the episode. Purser lines go through the
/// cabin-crew service's call flow (chime, CAB latch, purser voice); the FO's through the arbiter.
/// Read-and-speak only.
/// </summary>
public sealed class TurbulenceCabinService : Core.Hosting.IStartupModule, IDisposable
{
    public const string SeatbeltAdviceText = "Seat belt signs, captain?";

    private readonly IOptionsMonitor<CabinOptions> _options;
    private readonly TurbulenceStore _turbulence;
    private readonly IFlightDataSource _flightData;
    private readonly IFlightPhaseSource _flight;
    private readonly CabinSecureQuestionStore _question;
    private readonly CabinCrewService _cabin;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<TurbulenceCabinService> _logger;
    private readonly GroundOpsSignals _signals;
    private readonly TurbulenceCabinCore _core = new();
    private readonly object _gate = new();
    private Timer? _timer;
    private int _ticking;
    private bool _episodeEndedPending;
    private bool _episodeWasActive;
    private bool _purserCallPending;

    public TurbulenceCabinService(
        IOptionsMonitor<CabinOptions> options,
        TurbulenceStore turbulence,
        IFlightDataSource flightData,
        IFlightPhaseSource flight,
        CabinSecureQuestionStore question,
        CabinCrewService cabin,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        ILogger<TurbulenceCabinService> logger,
        GroundOpsSignals signals)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(turbulence);
        ArgumentNullException.ThrowIfNull(flightData);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(cabin);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(signals);
        _options = options;
        _turbulence = turbulence;
        _flightData = flightData;
        _flight = flight;
        _question = question;
        _cabin = cabin;
        _arbiter = arbiter;
        _eventLog = eventLog;
        _logger = logger;
        _signals = signals;
    }

    public void Start()
    {
        Attach();
        _timer = new Timer(_ => Tick(), null, 1000, 1000);
    }

    /// <summary>The event hooks without the timer — the seam the tests drive <see cref="Tick(DateTimeOffset)"/> through.</summary>
    internal void Attach()
    {
        _turbulence.EpisodeEnded += OnEpisodeEnded;
        _question.Answered += OnAnswered;
        _signals.FlightCycleReset += OnCycleReset;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _turbulence.EpisodeEnded -= OnEpisodeEnded;
        _question.Answered -= OnAnswered;
        _signals.FlightCycleReset -= OnCycleReset;
    }

    private void OnEpisodeEnded()
    {
        lock (_gate)
        {
            _episodeEndedPending = true;
        }
    }

    private void OnCycleReset()
    {
        lock (_gate)
        {
            _core.Reset();
            _episodeEndedPending = false;
            _episodeWasActive = false;
            _purserCallPending = false;
        }
    }

    private void OnAnswered(object? sender, (bool Accepted, string Source) answer)
    {
        if (!answer.Accepted)
        {
            _eventLog.Record("cabin.turbulence", new { action = "secureDeclined", source = answer.Source });
            return;
        }

        var delay = TurbulenceCabinCore.SecuredDelay(Random.Shared.NextDouble);
        lock (_gate)
        {
            _core.OnSecureAccepted(DateTimeOffset.UtcNow, delay);
        }

        _logger.LogInformation("Cabin secure accepted ({Source}): crew seated, secured report in {Seconds:F0} s", answer.Source, delay.TotalSeconds);
        _eventLog.Record("cabin.turbulence", new { action = "secureAccepted", source = answer.Source, securedInSeconds = Math.Round(delay.TotalSeconds) });
        _ = _arbiter.EnqueueAsync(new SpeechRequest(
            _options.CurrentValue.TurbulenceSeatedAckText, SpeechPriority.Normal, Tag: "cabin.turbulence.seated", Role: SpeechRole.Purser));
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            Tick(DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Turbulence cabin tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    /// <summary>One evaluation with an explicit clock.</summary>
    public void Tick(DateTimeOffset nowUtc)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled || !options.TurbulenceDetection || !_flight.IsLive)
        {
            return;
        }

        var ride = _turbulence.Snapshot();
        var data = _flightData.Sample();
        TurbulenceCabinAction action;
        var newEpisode = false;
        lock (_gate)
        {
            if (ride.EpisodeActive && !_episodeWasActive)
            {
                newEpisode = true;
            }

            _episodeWasActive = ride.EpisodeActive;
            var ended = _episodeEndedPending;
            _episodeEndedPending = false;
            if (_purserCallPending)
            {
                action = TurbulenceCabinAction.PurserCall; // the cabin was busy last time: try again
            }
            else
            {
                action = _core.Evaluate(new TurbulenceCabinSample(
                    ride.Level, ride.EpisodeActive, ended, data.SeatbeltSignsMode == 1, _flight.CurrentPhase.IsAirborne(), nowUtc), options);
            }
        }

        if (newEpisode)
        {
            _question.NewEpisode();
        }

        if (action == TurbulenceCabinAction.None)
        {
            return;
        }

        _ = RunAsync(action, options, ride.Level);
    }

    private async Task RunAsync(TurbulenceCabinAction action, CabinOptions options, TurbulenceLevel level)
    {
        try
        {
            switch (action)
            {
                case TurbulenceCabinAction.SeatbeltAdvice:
                    Record(action, level);
                    await _arbiter.EnqueueAsync(new SpeechRequest(SeatbeltAdviceText, SpeechPriority.Normal, Tag: "cabin.turbulence.seatbelts", Ttl: TimeSpan.FromSeconds(30))).ConfigureAwait(false);
                    break;

                case TurbulenceCabinAction.PurserCall:
                    if (!await _cabin.AnnounceAsync("cabin.turbulence-call", options.TurbulenceCallText).ConfigureAwait(false))
                    {
                        lock (_gate)
                        {
                            _purserCallPending = true;
                        }

                        return;
                    }

                    lock (_gate)
                    {
                        _purserCallPending = false;
                    }

                    Record(action, level);
                    if (!_question.Ask(options.TurbulenceCallText))
                    {
                        _logger.LogDebug("Cabin secure question not raised — one already stands");
                    }

                    break;

                case TurbulenceCabinAction.SevereReport:
                    Record(action, level);
                    await _cabin.AnnounceAsync("cabin.turbulence-severe", options.TurbulenceSevereText).ConfigureAwait(false);
                    break;

                case TurbulenceCabinAction.SecuredReport:
                    Record(action, level);
                    await _cabin.AnnounceAsync("cabin.turbulence-secured", options.TurbulenceSecuredText,
                        options.FoAcknowledge ? "Thank you, cabin." : null).ConfigureAwait(false);
                    break;

                case TurbulenceCabinAction.BackToNormal:
                    Record(action, level);
                    await _cabin.AnnounceAsync("cabin.turbulence-normal", options.TurbulenceNormalText).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Turbulence cabin action {Action} failed", action);
        }
    }

    private void Record(TurbulenceCabinAction action, TurbulenceLevel level)
    {
        _logger.LogInformation("Cabin turbulence reaction {Action} at {Level}", action, level);
        _eventLog.Record("cabin.turbulence", new { action = action.ToString(), level = level.ToString() });
    }
}
