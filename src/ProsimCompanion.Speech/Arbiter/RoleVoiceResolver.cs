using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Speech.Arbiter;

/// <summary>How one request should be voiced and played, resolved from its speaker role.</summary>
/// <param name="VoiceOverride">Provider voice id, or null for the provider's own configured
/// (First Officer) voice.</param>
/// <param name="IntercomOverride">Per-call intercom-filter decision, or null to keep the
/// options-driven default (the FO's own <c>speech.intercomFilter</c> setting).</param>
/// <param name="FellBackToFoVoice">True when the role wanted its own voice but none is
/// configured — the caller logs this once per role, not per utterance.</param>
public readonly record struct RoleVoice(
    string? VoiceOverride,
    bool? IntercomOverride,
    bool FellBackToFoVoice);

/// <summary>
/// Pure resolution of a <see cref="SpeechRole"/> against <see cref="VoicesOptions"/>. No
/// provider knowledge here: an id equal to the FO voice is passed through unchanged (it lands
/// in the same cache namespace, so nothing is wasted), and whether the id is meaningful is the
/// serving provider's business — this is why the options doc insists ids match the provider.
/// </summary>
public static class RoleVoiceResolver
{
    public static RoleVoice Resolve(SpeechRole role, VoicesOptions voices)
    {
        ArgumentNullException.ThrowIfNull(voices);

        return role switch
        {
            // The intercom decision sticks to the role even when the voice falls back —
            // a purser on the FO voice is still on the interphone.
            SpeechRole.Purser => ForRole(voices.Purser, voices.PurserIntercomFilter),
            SpeechRole.Company => ForRole(voices.Company, voices.CompanyIntercomFilter),
            SpeechRole.GroundCrew => ForRole(voices.Ground, voices.GroundIntercomFilter),
            _ => new RoleVoice(null, null, false),
        };
    }

    private static RoleVoice ForRole(string configuredVoice, bool intercomFilter)
        => string.IsNullOrWhiteSpace(configuredVoice)
            ? new RoleVoice(null, intercomFilter, FellBackToFoVoice: true)
            : new RoleVoice(configuredVoice, intercomFilter, FellBackToFoVoice: false);
}
