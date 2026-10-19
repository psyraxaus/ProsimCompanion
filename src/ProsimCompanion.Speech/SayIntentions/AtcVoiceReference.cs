using ProsimCompanion.Core.VoiceReference;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.SayIntentions;

/// <summary>
/// How <see cref="SayIntentionsService"/> appears in the Voice Reference drawer: one row per
/// atc-requests.json request on the ATC tab, the station as the "To …" line and the ICAO
/// shorthand as the answer. Composed live because the request file is user content.
/// </summary>
internal static class AtcVoiceReference
{
    private const string Group = "voiceReference.group.atc.";

    public static IEnumerable<VoiceReferenceGroup> Compose(IVoiceFeature feature, IReadOnlyList<string> live, VoiceReferenceContext context)
    {
        var sayIntentions = (SayIntentionsService)feature;
        var entries = sayIntentions.Requests
            .Select(r => (Phrases: r.Phrases.Where(p => live.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList(), Request: r))
            .Where(r => r.Phrases.Count > 0)
            .Select(r => new VoiceReferenceEntry(r.Phrases,
                r.Request.Station.Length > 0 ? new VoiceReferenceText(Group + "to", [r.Request.Station]) : null,
                r.Request.Icao.Length > 0 ? r.Request.Icao : null, VoiceSpeaker.Atc))
            .ToList();
        yield return new VoiceReferenceGroup("atc", new(Group + "title"), VoiceReferenceTab.Atc, VoiceSpeaker.Atc,
            sayIntentions.Enabled, sayIntentions.Enabled ? null : feature.Declaration.Gate.OffReason, entries);
    }
}
