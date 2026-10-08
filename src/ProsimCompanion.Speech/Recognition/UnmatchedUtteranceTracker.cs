using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Speech.Recognition;

/// <summary>Why an utterance fell through the router (issue #112).</summary>
public enum UnmatchedReason
{
    /// <summary>The interpreter scored it below the snapping threshold.</summary>
    Reject,

    /// <summary>It snapped to a phrase, yet no command, feature, drill or start claimed it.</summary>
    NoHandler,

    /// <summary>A checklist line was awaiting its answer and this was not an accepted one.</summary>
    NotAnAnswer,

    /// <summary>The gray-band "did you mean …?" was not affirmed.</summary>
    ConfirmDeclined,

    /// <summary>The recognizer itself rejected it (offline-engine confidence).</summary>
    EngineReject,
}

/// <summary>
/// Records what the FO heard but nothing acted on (issue #112): one <c>voice.unmatched</c>
/// session event and one row in <see cref="UnmatchedUtteranceStore"/> per miss. The router
/// calls it at every fall-through; the single <c>speech.trackUnmatched</c> switch gates both
/// outputs. Never speaks — the did-not-catch line stays the router's.
/// </summary>
public sealed class UnmatchedUtteranceTracker
{
    private readonly IOptionsMonitor<SpeechOptions> _options;
    private readonly UnmatchedUtteranceStore _store;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<UnmatchedUtteranceTracker> _logger;
    private readonly Core.Flight.IFlightPhaseSource? _flightPhase;
    private readonly TimeProvider _time;

    public UnmatchedUtteranceTracker(
        IOptionsMonitor<SpeechOptions> options,
        UnmatchedUtteranceStore store,
        JsonlEventLog eventLog,
        ILogger<UnmatchedUtteranceTracker> logger,
        Core.Flight.IFlightPhaseSource? flightPhase = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _store = store;
        _eventLog = eventLog;
        _logger = logger;
        _flightPhase = flightPhase;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Records one miss. <paramref name="suppressed"/> marks a sterile-phase
    /// absorption: counted (the pilot said something during rotation) but not a candidate.</summary>
    public void Record(string text, double score, string context, UnmatchedReason reason, bool suppressed = false)
    {
        if (!_options.CurrentValue.TrackUnmatched || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var normalized = CommandMatcher.Normalize(text);
        if (normalized.Length == 0)
        {
            return;
        }

        var phase = _flightPhase?.CurrentPhase.ToString() ?? "Unknown";
        var reasonKey = ReasonKey(reason);
        var utterance = new UnmatchedUtterance(
            _time.GetUtcNow(), text.Trim(), normalized, score, context, phase, reasonKey, suppressed);

        try
        {
            _store.Record(utterance);
            _eventLog.Record("voice.unmatched", new
            {
                text = utterance.Text,
                normalized,
                score = Math.Round(score, 2),
                context,
                phase,
                reason = reasonKey,
                suppressed,
            });
        }
        catch (Exception ex)
        {
            // Bookkeeping must never take the routing path down with it.
            _logger.LogDebug(ex, "Unmatched-utterance record failed for \"{Heard}\"", text);
        }
    }

    /// <summary>The wire spelling of a reason (matches the probe catalog and the reducer).</summary>
    public static string ReasonKey(UnmatchedReason reason) => reason switch
    {
        UnmatchedReason.Reject => "reject",
        UnmatchedReason.NoHandler => "no-handler",
        UnmatchedReason.NotAnAnswer => "not-an-answer",
        UnmatchedReason.ConfirmDeclined => "confirm-declined",
        UnmatchedReason.EngineReject => "engine-reject",
        _ => "unknown",
    };
}
