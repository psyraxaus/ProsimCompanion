using ProsimCompanion.Core.VoiceReference;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Crew;

/// <summary>
/// How <see cref="CrewHailService"/> appears in the Voice Reference drawer: the two hails, each
/// on its crew's tab, with the live reply text and the ACP channel rule from the options.
/// Composed live because the wording and the reply lines are settings, not constants.
/// </summary>
internal static class CrewHailVoiceReference
{
    private const string Ground = "voiceReference.group.hailGround.";
    private const string Cabin = "voiceReference.group.hailCabin.";

    /// <summary>"{0} is off — Settings → Voice First Officer → Ground Crew".</summary>
    internal const string OffGroundCrew = "voiceReference.off.groundCrew";

    /// <summary>"{0} is off — Settings → Voice First Officer → Cabin Crew".</summary>
    internal const string OffCabinCrew = "voiceReference.off.cabinCrew";

    public static IEnumerable<VoiceReferenceGroup> Compose(IVoiceFeature feature, IReadOnlyList<string> live, VoiceReferenceContext context)
    {
        // The hail lists are the feature's own (issue #170 added "ground from cockpit",
        // "ground, cockpit", "ground crew" — an " to ground" suffix test would hide them).
        var groundHails = live.Where(p => CrewHailService.GroundHailPhrases.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
        var cabinHails = live.Where(p => !groundHails.Contains(p)).ToList();
        var gating = context.Speech.AcpTransmitGating;
        var ground = context.GroundCrew;
        var cabin = context.Cabin;
        var featureOff = feature.Declaration.Gate.OffReason;

        if (groundHails.Count > 0)
        {
            var enabled = feature.Enabled && ground.Enabled;
            yield return new VoiceReferenceGroup("hailGround", new(Ground + "title"), VoiceReferenceTab.Ground,
                VoiceSpeaker.GroundCrew, enabled,
                enabled ? null : feature.Enabled ? new VoiceReferenceText(OffGroundCrew, ["groundCrew.enabled"]) : featureOff,
                [
                    new VoiceReferenceEntry(groundHails,
                        new(gating ? Ground + "how.gated" : Ground + "how"),
                        ground.HailReplyText, VoiceSpeaker.GroundCrew, gating ? [VoiceBadges.Interphone, VoiceBadges.Dialogue] : [VoiceBadges.Dialogue]),
                    new VoiceReferenceEntry([.. Gsx.GsxVoicePhrases.CancelPhrases], new(Ground + "cancel"), ground.StandingByText, VoiceSpeaker.GroundCrew),
                ]);
        }

        if (cabinHails.Count > 0)
        {
            var enabled = feature.Enabled && cabin.Enabled;
            yield return new VoiceReferenceGroup("hailCabin", new(Cabin + "title"), VoiceReferenceTab.Cabin,
                VoiceSpeaker.Purser, enabled,
                enabled ? null : feature.Enabled ? new VoiceReferenceText(OffCabinCrew, ["cabin.enabled"]) : featureOff,
                [
                    new VoiceReferenceEntry(cabinHails,
                        new(gating ? Cabin + "how.gated" : Cabin + "how"),
                        cabin.HailReplyText, VoiceSpeaker.Purser, gating ? [VoiceBadges.Cab, VoiceBadges.Dialogue] : [VoiceBadges.Dialogue]),
                ]);
        }
    }
}
