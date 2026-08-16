namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Distinct speaker-role voices for non-FO speech (cabin purser, company/dispatch), carried
/// from Prosim2FO's voices settings. Voice ids are provider-specific and must match whatever
/// TTS provider actually serves the utterance (Kokoro ids like "af_heart" for a Kokoro chain,
/// Google names like "en-AU-Chirp3-HD-Aoede" for a Google chain) — the id is passed to the
/// first willing provider verbatim. A blank id falls back to that provider's First Officer
/// voice (logged once per role, not per utterance).
/// </summary>
public sealed class VoicesOptions : IOptionSection
{
    public static string SectionName => "voices";

    /// <summary>Cabin purser voice id (default is a Kokoro id). Blank = FO voice.</summary>
    public string Purser { get; set; } = "af_heart";

    /// <summary>Company / dispatch voice id (default is a Kokoro id). Blank = FO voice.</summary>
    public string Company { get; set; } = "am_onyx";

    /// <summary>Ground-crew voice id (default is a Kokoro id). Blank = FO voice. May be
    /// overridden per airport by accent localization (<see cref="AccentOptions"/>).</summary>
    public string Ground { get; set; } = "am_michael";

    /// <summary>Apply the interphone band-pass to purser speech (matches the real interphone
    /// timbre), regardless of the FO's own intercom-filter setting.</summary>
    public bool PurserIntercomFilter { get; set; } = true;

    /// <summary>Apply the interphone filter to ground-crew speech — ground talks to the
    /// flight deck on the INT interphone, same timbre as the purser.</summary>
    public bool GroundIntercomFilter { get; set; } = true;

    /// <summary>Apply the interphone filter to company speech. Off by default — a company
    /// message is an ACARS readout, not interphone audio.</summary>
    public bool CompanyIntercomFilter { get; set; }
}
