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
    [OptionField(FieldKind.Bool, "speechSettings.common.enabled", Hint = "speechSettings.mcdu.enabledHint")]
    public bool Enabled { get; set; } = true;

    /// <summary>Arms MCDU key actuation. Off by default: the FO can read the box but never
    /// presses a key until this is deliberately enabled.</summary>
    [OptionField(FieldKind.Bool, "speechSettings.mcdu.actuation", DependsOn = "Enabled")]
    public bool AllowActuation { get; set; }

    /// <summary>Cancel-window pause between announcing an MCDU action and touching the box —
    /// "negative"/"disregard"/"my aircraft" during the pause aborts.</summary>
    [OptionField(FieldKind.Number, "speechSettings.mcdu.verifyPause", Suffix = "s", Min = "0", DependsOn = "Enabled; AllowActuation")]
    public double VerifyPauseSeconds { get; set; } = 3.0;

    /// <summary>How long the FO stays on each page of the MCDU check ("check the box") before
    /// the next page key, in seconds; each page varies a quarter around it, and the sub-pages
    /// of DATA take half. The check turns pages without <see cref="AllowActuation"/> (owner
    /// decision 2026-11-15): a page key changes what the FO's own screen shows and nothing
    /// in the aircraft — the predecessor's "check the box" macro pressed the same keys.</summary>
    [OptionField(FieldKind.Number, "speechSettings.mcdu.boxCheckPage", Suffix = "s", Min = "1", Max = "20", DependsOn = "Enabled")]
    public double BoxCheckPageSeconds { get; set; } = 5.0;

    /// <summary>After the MCDU check the FO offers to correct what it knows the answer to
    /// ("Cost index is 30, the plan says 45. Shall I enter it?"). Off: the FO reports only.
    /// Weights and takeoff speeds are written as datarefs; an entry that needs MCDU keys is
    /// offered only while <see cref="AllowActuation"/> is on.</summary>
    [OptionField(FieldKind.Bool, "speechSettings.mcdu.boxCheckOfferFixes", DependsOn = "Enabled")]
    public bool BoxCheckOfferFixes { get; set; } = true;
}
