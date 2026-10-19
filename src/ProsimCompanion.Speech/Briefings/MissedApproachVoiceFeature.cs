using ProsimCompanion.Core.VoiceReference;
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

    /// <summary>Roster declaration (see <see cref="VoiceFeatureDeclaration"/>): the missed-approach
    /// phrases dispatch through this small feature so the re-brief gate logic stays out of
    /// <see cref="BriefingService"/>.</summary>
    public static VoiceFeatureDeclaration Declared { get; } = new()
    {
        Precedence = 160,
        Gate = VoiceFeatureGate.AlwaysOn,
        Description = new DescribedVoiceReference("missedApproach", "voiceReference.group.missedApproach.title", VoiceReferenceTab.FirstOfficer, VoiceSpeaker.FirstOfficer,
        [
            new(["missed approach brief", "missed approach briefing", "brief the missed approach"], "voiceReference.group.missedApproach.rebrief"),
        ]),
    };

    VoiceFeatureDeclaration IVoiceFeature.Declaration => Declared;

    public bool Enabled => true;

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
