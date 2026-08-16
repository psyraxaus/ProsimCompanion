namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Automatic accent localization for the ground crew (ADR-0006 / issue #53): the GroundCrew
/// voice follows the airport country via an ICAO-prefix → TTS-locale map. Google Chirp 3 HD
/// carries the accents (51 locales); local providers degrade to their US/UK voices, so
/// local-only mode simply narrows the accents instead of failing. Applies to the GroundCrew
/// role only — purser/company voices stay as configured in <see cref="VoicesOptions"/>.
/// </summary>
public sealed class AccentOptions : IOptionSection
{
    public static string SectionName => "accents";

    public bool Enabled { get; set; } = true;

    /// <summary>Chirp 3 HD persona used for localized ground voices — the voice id becomes
    /// "{locale}-Chirp3-HD-{persona}" (e.g. "fr-FR-Chirp3-HD-Charon").</summary>
    public string GooglePersona { get; set; } = "Charon";

    /// <summary>ICAO-prefix → locale overrides, checked before the built-in map (longest
    /// prefix wins). Example: { "LSZH": "de-DE", "LSG": "fr-FR" }.</summary>
    public Dictionary<string, string> Overrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
