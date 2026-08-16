namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Settings for the voice MCDU features on the FO's CDU2: read-back ("read the MCDU"),
/// RAD NAV ILS tuning and arrival-runway changes. Reading is safe and on by default;
/// anything that presses a key — and can therefore mutate the flight plan — must be armed
/// deliberately via <see cref="AllowActuation"/>. This mirrors the predecessor's proven
/// two-switch arming (<c>mcdu.enabled</c> + <c>mcdu.allowActuation</c>): a user who only
/// wants the FO to read the box never risks a stray key press.
/// </summary>
public sealed class McduOptions : IOptionSection
{
    public static string SectionName => "mcdu";

    /// <summary>Master switch for all MCDU voice features (read-back included).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Arms MCDU key actuation. Off by default: the FO can read the box but never
    /// presses a key until this is deliberately enabled.</summary>
    public bool AllowActuation { get; set; }

    /// <summary>Cancel-window pause between announcing an MCDU action and touching the box —
    /// "negative"/"disregard"/"my aircraft" during the pause aborts.</summary>
    public double VerifyPauseSeconds { get; set; } = 3.0;
}
