namespace ProsimCompanion.Core.State;

/// <summary>
/// Lists the ElevenLabs premade voices for the settings page's "Fetch voices" picker. Kept in
/// Core so the Web project (which references only Core) can inject it; implemented by the
/// speech pillar, which owns the HTTP call. Takes the DRAFT key and base URL rather than the
/// saved options, so the pilot can pick a voice before saving the key for the first time.
/// </summary>
public interface IElevenLabsVoiceCatalog
{
    /// <summary>Fetches <c>GET {baseUrl}/v2/voices?category=premade</c>. Throws on any
    /// failure with the HTTP status and a truncated body — never the key — so the page can
    /// show one line.</summary>
    Task<IReadOnlyList<(string Id, string Name, string? Accent, string? Gender, string? PreviewUrl)>> FetchPremadeVoicesAsync(
        string baseUrl, string apiKey, CancellationToken cancellationToken);
}
