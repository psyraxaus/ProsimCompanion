using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Speech;

namespace ProsimCompanion.Core.State;

/// <summary>One voice a provider can speak with, as the Voices page lists it.</summary>
/// <param name="Provider">Chain name: kokoro, elevenlabs, google, winrt, sapi.</param>
/// <param name="Id">The id the provider wants (Kokoro name, ElevenLabs voice_id, Google
/// voice name, Windows display name).</param>
/// <param name="Name">Short human name for the dropdown row.</param>
/// <param name="Region">Language / accent group label, e.g. "British English",
/// "English (Australia)", "Australian".</param>
/// <param name="Gender">"female", "male" or "".</param>
/// <param name="Grade">Kokoro's published overall grade; null elsewhere.</param>
/// <param name="Blurb">Plain sentences for the details line under the picker.</param>
public sealed record VoiceEntry(
    string Provider,
    string Id,
    string Name,
    string Region,
    string Gender,
    string? Grade,
    string Blurb)
{
    /// <summary>The stored form, <c>provider:id</c>.</summary>
    public string Value => VoiceRef.Format(Provider, Id);

    /// <summary>Dropdown row: "George (male), grade C" / "Aoede (female)".</summary>
    public string Label
    {
        get
        {
            var gender = Gender.Length > 0 ? $" ({Gender})" : "";
            return Grade is null ? $"{Name}{gender}" : $"{Name}{gender}, grade {Grade}";
        }
    }
}

/// <summary>
/// Lists the voices each TTS provider can actually speak with, for the Voices page's
/// pickers (owner, 2026-10-12: one dropdown over every provider, grouped by provider then
/// region, and nothing pre-populated — only what the service reports). Kept in Core so the
/// Web project (which references only Core) can inject it; implemented by the speech pillar.
/// Takes the DRAFT options so a first-time setup can list before saving.
/// </summary>
public interface IVoiceCatalog
{
    /// <summary>Lists one provider's voices. Returns an empty list when the provider is not
    /// configured in <paramref name="draft"/>; throws with one readable line when the
    /// service is configured but does not answer.</summary>
    Task<IReadOnlyList<VoiceEntry>> ListVoicesAsync(string provider, SpeechOptions draft, CancellationToken cancellationToken);
}
