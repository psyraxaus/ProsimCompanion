using ProsimCompanion.Core.VoiceReference;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Commands;

/// <summary>
/// How <see cref="ConfiguredVoiceCommands"/> appears in the Voice Reference drawer: one row per
/// commands.json command, the FO's configured reply as the answer. Composed live because the
/// file is the pilot's own and changes without a build.
/// </summary>
internal static class ConfiguredVoiceReference
{
    public static IEnumerable<VoiceReferenceGroup> Compose(IVoiceFeature feature, IReadOnlyList<string> live, VoiceReferenceContext context)
    {
        var configured = (ConfiguredVoiceCommands)feature;
        var entries = configured.Commands
            .Select(c => (Phrases: c.Phrases.Where(p => live.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList(), Say: c.Say))
            .Where(c => c.Phrases.Count > 0)
            .Select(c => new VoiceReferenceEntry(c.Phrases, null, c.Say))
            .ToList();
        yield return new VoiceReferenceGroup("commandsJson", new("voiceReference.group.commandsJson.title"),
            VoiceReferenceTab.FirstOfficer, VoiceSpeaker.FirstOfficer, configured.Enabled, null, entries);
    }
}
