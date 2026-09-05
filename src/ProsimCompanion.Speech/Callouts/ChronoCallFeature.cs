using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Callouts;

/// <summary>
/// The takeoff chrono PM action (issue #126): the pilot's "takeoff" call on the roll gets the
/// FO's "Takeoff." response and a press of the F/O-side chrono (<c>S_MIP_CHRONO_FO</c>,
/// momentary via the serialized press path); the touchdown commit presses it again — silently,
/// the FO is busy calling spoilers — to stop the timer. Sibling of <see cref="GearCallFeature"/>.
/// The stop press only fires when THIS feature started the chrono: a blind press at touchdown
/// would START a chrono the pilot never ran. Handled only in TaxiOut/TakeoffRoll so "takeoff"
/// said anywhere else routes on normally; before this feature the 2026-09-05 lineup answered
/// the pilot's "Take off." with "Repeat please.".
/// </summary>
public sealed class ChronoCallFeature : IVoiceFeature, Core.Hosting.IStartupModule, IDisposable
{
    private static readonly string[] CallPhrases = ["takeoff", "take off"];

    private readonly IProsimDataRefs _dataRefs;
    private readonly ISpeechArbiter _arbiter;
    private readonly IFlightPhaseSource _flight;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<ChronoCallFeature> _logger;
    private bool _chronoRunning;

    public ChronoCallFeature(
        IProsimDataRefs dataRefs,
        ISpeechArbiter arbiter,
        IFlightPhaseSource flight,
        JsonlEventLog eventLog,
        ILogger<ChronoCallFeature> logger)
    {
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _dataRefs = dataRefs;
        _arbiter = arbiter;
        _flight = flight;
        _eventLog = eventLog;
        _logger = logger;
    }

    public void Start() => _flight.PhaseChanged += OnPhaseChanged;

    public void Dispose() => _flight.PhaseChanged -= OnPhaseChanged;

    public bool Enabled => true;

    public IEnumerable<string> Phrases => CallPhrases;

    public bool ValueParse => false;

    /// <summary>Pure gate — the call only means "start the clock" on the roll. Exposed for
    /// tests. TaxiOut is included because the call habitually lands as thrust comes up,
    /// seconds before the TakeoffRoll commit (2026-09-05: call at 09:53:15, commit :28).</summary>
    internal static bool CallArmed(FlightPhase phase)
        => phase is FlightPhase.TaxiOut or FlightPhase.TakeoffRoll;

    public bool TryHandle(string utterance)
    {
        var text = CommandMatcher.Normalize(utterance);
        if (!CallPhrases.Contains(text, StringComparer.Ordinal))
        {
            return false;
        }

        if (!CallArmed(_flight.CurrentPhase))
        {
            return false; // not our moment — let the normal routing decide what it was
        }

        _ = PressAsync(start: true);
        return true;
    }

    private void OnPhaseChanged(object? sender, FlightPhaseChangedEventArgs e)
    {
        if (e.Current == FlightPhase.LandingRollout && _chronoRunning)
        {
            _ = PressAsync(start: false);
        }
        else if (e.Current is FlightPhase.Unknown && _chronoRunning)
        {
            // Session gone mid-flight — the physical clock state is unknowable now; a press
            // on the next touchdown could start a fresh chrono instead of stopping one.
            _chronoRunning = false;
            _logger.LogInformation("Session ended — chrono latch reset without a stop press");
        }
    }

    private async Task PressAsync(bool start)
    {
        try
        {
            _chronoRunning = start;
            if (start)
            {
                Speak("Takeoff.");
            }

            await _dataRefs.PressMomentaryAsync(ProsimDataRefNames.MipChronoFo).ConfigureAwait(false);
            _eventLog.Record("voicecommand.chrono", new { action = start ? "start" : "stop" });
        }
        catch (Exception ex)
        {
            _chronoRunning = false;
            _logger.LogError(ex, "Chrono press failed ({Action})", start ? "start" : "stop");
        }
    }

    private void Speak(string text)
        => _ = _arbiter.EnqueueAsync(new SpeechRequest(
            text, SpeechPriority.High, Ttl: TimeSpan.FromSeconds(10), Tag: "fo.chrono"));
}
