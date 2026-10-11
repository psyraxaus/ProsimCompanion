namespace ProsimCompanion.Core.State;

/// <summary>
/// Lists the voices a kokoro-fastapi server actually has, so the settings-page picker shows
/// that server's list (legacy v0 voices, user blends) rather than only the published sheet.
/// Kept in Core so the Web project (which references only Core) can inject it; implemented by
/// the speech pillar, which owns the HTTP call. Takes the DRAFT base URL so a first-time setup
/// can pick a voice before saving.
/// </summary>
public interface IKokoroVoiceCatalog
{
    /// <summary>Fetches <c>GET {baseUrl}/v1/audio/voices</c> and returns the voice ids. Throws
    /// on any failure with the HTTP status and a truncated body so the page can show one line.</summary>
    Task<IReadOnlyList<string>> ListVoiceIdsAsync(string baseUrl, CancellationToken cancellationToken);
}
