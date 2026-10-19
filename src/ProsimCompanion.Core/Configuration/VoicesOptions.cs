namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Distinct speaker-role voices for non-FO speech (cabin purser, company/dispatch, ground), carried
/// from Prosim2FO's voices settings. Since 2026-10-12 a voice is <c>provider:id</c> (see
/// <c>Core.Speech.VoiceRef</c>): the named provider speaks that role first and the chain covers
/// when it is down. A plain id (no known prefix) is legacy: provider-specific and must match whatever
/// TTS provider actually serves the utterance (Kokoro ids like "af_heart" for a Kokoro chain,
/// Google names like "en-AU-Chirp3-HD-Aoede" for a Google chain) — the id is passed to the
/// first willing provider verbatim. A blank id falls back to that provider's First Officer
/// voice (logged once per role, not per utterance).
/// </summary>
public sealed class VoicesOptions : IOptionSection
{
    public static string SectionName => "voices";

    /// <summary>Cabin purser voice id (default is a Kokoro id). Blank = FO voice.</summary>
    [OptionField(FieldKind.VoicePicker, "speechSettings.voices.crew.purser", BlankLabel = "speechSettings.common.sameAsFo", EmptyListHint = "speechSettings.voiceNotes.emptyHint", Normalize = FieldNormalization.Trim)]
    public string Purser { get; set; } = "kokoro:af_heart";

    /// <summary>Company / dispatch voice id (default is a Kokoro id). Blank = FO voice.</summary>
    [OptionField(FieldKind.VoicePicker, "speechSettings.voices.crew.company", BlankLabel = "speechSettings.common.sameAsFo", EmptyListHint = "speechSettings.voiceNotes.emptyHint", Normalize = FieldNormalization.Trim)]
    public string Company { get; set; } = "kokoro:am_onyx";

    /// <summary>Ground-crew voice id (default is a Kokoro id). Blank = FO voice. May be
    /// overridden per airport by accent localization (<see cref="AccentOptions"/>).</summary>
    [OptionField(FieldKind.VoicePicker, "speechSettings.voices.crew.ground", BlankLabel = "speechSettings.common.sameAsFo", EmptyListHint = "speechSettings.voiceNotes.emptyHint", Normalize = FieldNormalization.Trim)]
    public string Ground { get; set; } = "kokoro:am_michael";

    /// <summary>Apply the interphone band-pass to purser speech (matches the real interphone
    /// timbre), regardless of the FO's own intercom-filter setting.</summary>
    [OptionField(FieldKind.Bool, "speechSettings.voices.crew.purserFilter")]
    public bool PurserIntercomFilter { get; set; } = true;

    /// <summary>Apply the interphone filter to ground-crew speech — ground talks to the
    /// flight deck on the INT interphone, same timbre as the purser.</summary>
    [OptionField(FieldKind.Bool, "speechSettings.voices.crew.groundFilter")]
    public bool GroundIntercomFilter { get; set; } = true;

    /// <summary>Apply the interphone filter to company speech. Off by default — a company
    /// message is an ACARS readout, not interphone audio.</summary>
    [OptionField(FieldKind.Bool, "speechSettings.voices.crew.companyFilter")]
    public bool CompanyIntercomFilter { get; set; }
}
