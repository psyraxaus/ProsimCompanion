using ProsimCompanion.Core.VoiceReference;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Gsx;

/// <summary>
/// How <see cref="GsxVoiceService"/> appears in the Voice Reference drawer: the single-shot
/// phrases grouped by the command they run, straight from <see cref="GsxVoicePhrases"/> — the
/// answer is the FO's real confirmation line. The cabin-flavoured boarding phrases (purser
/// ack) go to the Cabin tab. Composed live because the binding table is the grammar's source;
/// a static row list would be a third copy of it.
/// </summary>
internal static class GsxVoiceReference
{
    private const string Group = "voiceReference.group.gsx.";
    private const string CabinGroup = "voiceReference.group.gsxCabin.";

    public static IEnumerable<VoiceReferenceGroup> Compose(IVoiceFeature feature, IReadOnlyList<string> live, VoiceReferenceContext context)
    {
        var reason = feature.Enabled ? null : feature.Declaration.Gate.OffReason;
        var ground = new List<VoiceReferenceEntry>();
        var cabin = new List<VoiceReferenceEntry>();

        var start = GsxVoicePhrases.StartGroundServicesPhrases.Where(p => live.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
        if (start.Count > 0)
        {
            ground.Add(new VoiceReferenceEntry(start, new(Group + "start"), "Ground services started."));
        }

        foreach (var byCommand in GsxVoicePhrases.Bindings.GroupBy(b => (b.Value.Command, b.Value.CabinAck)))
        {
            var phrases = byCommand.Select(b => b.Key).Where(p => live.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
            if (phrases.Count == 0)
            {
                continue;
            }

            var binding = byCommand.First().Value;
            if (binding.CabinAck)
            {
                cabin.Add(new VoiceReferenceEntry(phrases, new(CabinGroup + "boarding"), "Boarding underway.", VoiceSpeaker.Purser));
            }
            else
            {
                ground.Add(new VoiceReferenceEntry(phrases, CommandWhat(binding.Command), binding.SuccessPhrase));
            }
        }

        yield return new VoiceReferenceGroup("gsx", new(Group + "title"), VoiceReferenceTab.Ground,
            VoiceSpeaker.FirstOfficer, feature.Enabled, reason, ground);
        yield return new VoiceReferenceGroup("gsxCabin", new(CabinGroup + "title"), VoiceReferenceTab.Cabin,
            VoiceSpeaker.Purser, feature.Enabled, reason, cabin);
    }

    /// <summary>The pack text for a named command: <c>gsx.requestBoarding</c> reads from
    /// <c>voiceReference.group.gsx.command.requestBoarding</c>. Every shipped binding has one
    /// (the roster test checks the English pack); an unknown command shows its id.</summary>
    internal static VoiceReferenceText CommandWhat(string command)
        => new(Group + "command." + (command.StartsWith("gsx.", StringComparison.Ordinal) ? command["gsx.".Length..] : command));
}
