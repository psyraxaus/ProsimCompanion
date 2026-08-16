using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Persona;

/// <summary>
/// Session-scoped quiet flag ("quiet please"). Prosim2FO forced persona chattiness to 0 for
/// the rest of the session with no un-quiet phrase; this is the same one-way latch. It lives
/// here (not in the arbiter) because the arbiter's suppression-rule chain is fixed at
/// construction — the arbiter consumes it through <see cref="QuietCockpitRule"/>.
/// </summary>
public sealed class QuietState
{
    private int _quiet;

    /// <summary>True once the crew has asked for quiet; sticks for the process lifetime
    /// (the predecessor's "for the rest of the session").</summary>
    public bool IsQuiet => Volatile.Read(ref _quiet) != 0;

    public void Engage() => Volatile.Write(ref _quiet, 1);

    /// <summary>For tests and any future "as you were" affordance — the predecessor had none.</summary>
    public void Reset() => Volatile.Write(ref _quiet, 0);
}

/// <summary>
/// The quiet-mode suppression rule: while quiet, Low-priority chatter (small talk, advisories,
/// debrief colour — the band SpeechPriority documents as "anything deferrable") is vetoed
/// outright; Normal and above always pass — checklists, replies the crew explicitly asked for,
/// and flight-deck callouts are never chatter. Constructed but NOT active until the arbiter's
/// rule list includes it (see the registration note on <see cref="SmallTalkService"/>).
/// </summary>
public sealed class QuietCockpitRule : ISpeechSuppressionRule
{
    private readonly QuietState _state;

    public QuietCockpitRule(QuietState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
    }

    public string Name => "quietCockpit";

    public SpeechSuppressionVerdict Evaluate(SpeechRequest request, SpeechContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _state.IsQuiet && request.Priority == SpeechPriority.Low
            ? SpeechSuppressionVerdict.Suppress
            : SpeechSuppressionVerdict.Allow;
    }
}

/// <summary>
/// The "quiet please" voice command (Prosim2FO semantics): engages <see cref="QuietState"/>
/// for the rest of the session and acknowledges with the predecessor's exact line. The
/// predecessor's LLM cruise small-talk generator is deliberately not ported (no persona
/// pillar here yet); what quiet mode gates in this repo is the Low speech band, via
/// <see cref="QuietCockpitRule"/>. Re-asking while already quiet re-acknowledges — the
/// predecessor did the same, and silence would read as a failed command.
/// </summary>
public sealed class SmallTalkService : IVoiceFeature
{
    private static readonly string[] QuietPhrases =
        ["quiet please", "quiet cockpit", "pipe down", "less chat", "keep it quiet"];

    private readonly QuietState _quiet;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<SmallTalkService> _logger;

    public SmallTalkService(
        QuietState quiet,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        ILogger<SmallTalkService> logger)
    {
        ArgumentNullException.ThrowIfNull(quiet);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _quiet = quiet;
        _arbiter = arbiter;
        _eventLog = eventLog;
        _logger = logger;
    }

    public bool Enabled => true;

    public IEnumerable<string> Phrases => QuietPhrases;

    public bool ValueParse => false;

    public bool TryHandle(string utterance)
    {
        var text = CommandMatcher.Normalize(utterance);
        if (!QuietPhrases.Contains(text))
        {
            return false;
        }

        _quiet.Engage();
        _logger.LogInformation("Quiet cockpit engaged by \"{Phrase}\"", text);
        _eventLog.Record("smalltalk.silenced", new { phrase = text });

        // Normal priority so the acknowledgement itself clears the quiet rule.
        _ = _arbiter.SpeakAsync("Righto, I'll keep it quiet.", SpeechPriority.Normal);
        return true;
    }
}
