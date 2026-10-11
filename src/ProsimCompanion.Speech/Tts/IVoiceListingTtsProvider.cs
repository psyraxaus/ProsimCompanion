using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Speech.Tts;

/// <summary>
/// A provider that can enumerate its own voices for the Voices page (Google through its
/// API, the two Windows providers from the installed voices). Kokoro and ElevenLabs list
/// through their standalone catalogs instead, which take a draft URL / key.
/// </summary>
public interface IVoiceListingTtsProvider
{
    /// <summary>Lists the voices with the DRAFT options (key file path, etc.). Empty when the
    /// provider is not configured in the draft; throws one readable line on a service error.</summary>
    Task<IReadOnlyList<VoiceEntry>> ListVoicesAsync(SpeechOptions draft, CancellationToken cancellationToken);
}
