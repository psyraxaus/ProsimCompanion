using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Briefings;

/// <summary>The "missed approach brief" voice phrases — a thin dispatch shim so
/// <see cref="MissedApproachRebrief"/> keeps only the go-around gate logic.</summary>
public sealed class MissedApproachVoiceFeature : IVoiceFeature
{
    private static readonly string[] MissedApproachPhrases =
    [
        "missed approach brief", "missed approach briefing", "brief the missed approach",
    ];

    private readonly MissedApproachRebrief _rebrief;

    public MissedApproachVoiceFeature(MissedApproachRebrief rebrief)
    {
        ArgumentNullException.ThrowIfNull(rebrief);
        _rebrief = rebrief;
    }

    public IEnumerable<string> Phrases => MissedApproachPhrases;

    public bool ValueParse => false;

    public bool TryHandle(string utterance)
    {
        if (!MissedApproachPhrases.Contains(CommandMatcher.Normalize(utterance)))
        {
            return false;
        }

        _rebrief.SpeakOnDemand();
        return true;
    }
}
