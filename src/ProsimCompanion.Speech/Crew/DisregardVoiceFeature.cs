using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.VoiceReference;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Crew;

/// <summary>
/// "Disregard" / "cancel that" as an idle utterance (issue #168, EGCC 2026-10-11 23:35:47Z:
/// the pilot said "Disregard." to a cold-and-dark advisory spoken mid-turnaround and the
/// router answered with a did-not-catch). Silences the current advisory
/// (<see cref="SpokenAdvisories"/>) and acknowledges — "Disregarded." — whether or not
/// anything was playing, so the pilot always hears that the word landed. The FCU, radio and
/// MCDU executors keep their own cancel windows: they see the same words first and return
/// false when nothing of theirs is pending, which is when this feature answers.
/// </summary>
public sealed class DisregardVoiceFeature : IVoiceFeature
{
    /// <summary>The acknowledgement, verbatim — tests and the Voice Reference quote it.</summary>
    public const string Acknowledgement = "Disregarded.";

    private static readonly string[] CancelPhrases = ["disregard", "cancel that", "belay that", "never mind"];

    private static readonly string[] NormalizedPhrases = [.. CancelPhrases.Select(CommandMatcher.Normalize)];

    private readonly SpokenAdvisories _advisories;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<DisregardVoiceFeature> _logger;

    public DisregardVoiceFeature(
        SpokenAdvisories advisories,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        ILogger<DisregardVoiceFeature> logger)
    {
        ArgumentNullException.ThrowIfNull(advisories);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _advisories = advisories;
        _arbiter = arbiter;
        _eventLog = eventLog;
        _logger = logger;
    }

    /// <summary>Roster declaration (see <see cref="VoiceFeatureDeclaration"/>): "disregard" /
    /// "cancel that" while idle (issue #168), after the ground features whose advisories it
    /// silences.</summary>
    public static VoiceFeatureDeclaration Declared { get; } = new()
    {
        Precedence = 250,
        Gate = VoiceFeatureGate.AlwaysOn,
        Description = new DescribedVoiceReference("disregard", "voiceReference.group.disregard.title", VoiceReferenceTab.FirstOfficer, VoiceSpeaker.FirstOfficer,
        [
            new(["disregard", "cancel that", "belay that", "never mind"], "voiceReference.group.disregard.disregard", Acknowledgement),
        ]),
    };

    VoiceFeatureDeclaration IVoiceFeature.Declaration => Declared;

    public bool Enabled => true;

    public IEnumerable<string> Phrases => CancelPhrases;

    public bool ValueParse => false;

    public bool TryHandle(string utterance)
    {
        if (!NormalizedPhrases.Contains(CommandMatcher.Normalize(utterance)))
        {
            return false;
        }

        var cancelled = _advisories.CancelCurrent(out var tag);
        _logger.LogInformation(
            "Disregard: {Outcome}",
            cancelled ? $"advisory '{tag}' silenced" : "nothing to silence — acknowledged");
        _eventLog.Record("voicecommand.disregard", new { cancelled, advisory = tag });
        // High so it is heard before anything still queued behind the advisory.
        _ = _arbiter.EnqueueAsync(new SpeechRequest(Acknowledgement, SpeechPriority.High, Tag: "fo.disregard"));
        return true;
    }
}
