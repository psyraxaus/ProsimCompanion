namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// The one description of how an option property is edited on a settings page (candidate C3,
/// 2026-10-19): its control kind, its texts, its save-time normalisation, the parent it is
/// disabled under, its numeric bounds, its choice list. Declared ONCE, on the property, beside
/// the default and the doc comment that explain what the option does — so a new option is one
/// property with one attribute, not five files (commit a0bba07 touched fifteen for one bool).
/// <para>
/// Why an attribute and not a static table or a partial-class companion: the attribute sits on
/// the line above the property, so the property, its default, its doc comment and its field
/// description are read and changed as one unit (locality); it is found by reflection from the
/// property the page names in <c>For="o =&gt; o.Volume"</c>, so nothing has to be kept in step
/// by hand; and it is constant data, which is all a field description is — anything that needs
/// code (device lists, live voice lists) is named here and supplied by the page. A table would
/// have to be kept parallel to the class; a companion would double the files.
/// </para>
/// <para>
/// Texts: <see cref="TextId"/> is the root of the field's pack ids (ADR-0014). By convention the
/// label is <c>{root}.label</c> when the English pack has it, else <c>{root}</c> itself; the hint
/// is <c>{root}.hint</c>, the fold <c>{root}.more</c>, the input placeholder
/// <c>{root}.placeholder</c>, the DirtyBar tally name <c>{root}.tally</c> and the empty-list line
/// <c>{root}.empty</c> — each only when the English pack defines it. A text that lives under a
/// shared id (<c>speechSettings.common.enabled</c>) is named explicitly through
/// <see cref="Label"/>, <see cref="Hint"/> or <see cref="More"/>. The existing ids are kept
/// verbatim, so the seven packs did not change.
/// </para>
/// <para>
/// Disabled-when: <see cref="DependsOn"/> lists conditions separated by <c>;</c>, every one of
/// which must hold for the field to be enabled; inside one condition <c>|</c> separates
/// alternatives of which one suffices. A condition is <c>[!]path[=value]</c>: the path is a
/// property chain on the OBJECT THAT OWNS THIS PROPERTY (<c>Enabled</c>, <c>GearStillDown.Enabled</c>),
/// with <c>../</c> stepping up to the owner's owner (<c>../Enabled</c> for a nested block gated
/// by the section's master switch). Without <c>=value</c> a bool must be true and a string
/// non-blank; with it the value's text (enums as their camelCase JSON name) must match.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class OptionFieldAttribute : Attribute
{
    /// <param name="kind">Which control edits the property.</param>
    /// <param name="textId">The root of the field's pack text ids; empty only for
    /// <see cref="FieldKind.RangeUpper"/>, which is rendered by its lower bound's row.</param>
    public OptionFieldAttribute(FieldKind kind, string textId = "")
    {
        Kind = kind;
        TextId = textId ?? "";
    }

    /// <summary>Which control edits the property.</summary>
    public FieldKind Kind { get; }

    /// <summary>The root of the field's pack text ids (see the class remarks).</summary>
    public string TextId { get; }

    /// <summary>Explicit label text id, for a label shared between fields
    /// (<c>speechSettings.common.enabled</c>). Null derives it from <see cref="TextId"/>.</summary>
    public string? Label { get; set; }

    /// <summary>Explicit hint text id where the hint does not sit at <c>{root}.hint</c>.</summary>
    public string? Hint { get; set; }

    /// <summary>Explicit "More" fold text id where it does not sit at <c>{root}.more</c>.</summary>
    public string? More { get; set; }

    /// <summary>A literal input placeholder (a URL, a path) — shown as typed, never translated.
    /// A translated placeholder sits in the pack at <c>{root}.placeholder</c> instead.</summary>
    public string? Placeholder { get; set; }

    /// <summary>Explicit text id of the blank choice's label (voice pickers: "Same as the First
    /// Officer"). Null shows the picker's own "Pick a voice" placeholder.</summary>
    public string? BlankLabel { get; set; }

    /// <summary>Explicit text id of the line shown under an empty voice list, where it does not
    /// sit at <c>{root}.empty</c>.</summary>
    public string? EmptyListHint { get; set; }

    /// <summary>What Save does to the text before the diff (text kinds only).</summary>
    public FieldNormalization Normalize { get; set; }

    /// <summary>The conditions under which the field is enabled (see the class remarks);
    /// null or empty = always enabled.</summary>
    public string? DependsOn { get; set; }

    /// <summary>True for a tuning constant that the pages fold under "Show advanced settings"
    /// (ADR-0010). The fold itself stays a page-level grouping — consecutive advanced fields
    /// fold as one run — so this is the field's own statement of what it is.</summary>
    public bool Advanced { get; set; }

    /// <summary>Number kinds: the input's <c>min</c> attribute, as text.</summary>
    public string? Min { get; set; }

    /// <summary>Number kinds: the input's <c>max</c> attribute, as text.</summary>
    public string? Max { get; set; }

    /// <summary>Number kinds: the input's <c>step</c> attribute; null = any.</summary>
    public string? Step { get; set; }

    /// <summary>Number kinds: the unit shown after the input ("s", "kg", "%").</summary>
    public string? Suffix { get; set; }

    /// <summary>Number kinds: do not show the out-of-the-box value and the reset button. Only
    /// for the rows that never showed one; a new numeric option shows its default (ADR-0010).</summary>
    public bool NoDefault { get; set; }

    /// <summary><see cref="FieldKind.Range"/>: the sibling property holding the upper bound.</summary>
    public string? RangeEnd { get; set; }

    /// <summary><see cref="FieldKind.RangeUpper"/>: the sibling property holding the lower bound.</summary>
    public string? RangeStart { get; set; }

    /// <summary><see cref="FieldKind.Select"/>: the name of the choice list. Static lists live in
    /// the web layer's choice catalog; a list that depends on runtime data (audio devices) is
    /// named here and handed in by the page.</summary>
    public string? Choices { get; set; }

    /// <summary><see cref="FieldKind.Text"/>: the shape the value is checked against.</summary>
    public TextFieldKind TextKind { get; set; }

    /// <summary><see cref="FieldKind.Text"/>: render as a password input (keys, tokens).</summary>
    public bool Password { get; set; }

    /// <summary><see cref="FieldKind.Text"/>: a nullable string whose empty input is stored as
    /// null (the ProSim API key: unset is null, never "").</summary>
    public bool EmptyAsNull { get; set; }

    /// <summary>Path kinds: the Windows dialog filter (<c>JSON (*.json)|*.json</c>).</summary>
    public string? BrowseFilter { get; set; }

    /// <summary>Output-file kinds: the file name the Save dialog suggests.</summary>
    public string? BrowseFileName { get; set; }

    /// <summary>Path kinds: the name of the expected-folders list the Browse… dialog opens in
    /// when the value gives no folder (a method of <c>ExpectedFolders</c>, resolved by the web
    /// layer at click time).</summary>
    public string? BrowseStartIn { get; set; }

    /// <summary><see cref="FieldKind.VoicePicker"/>: store the bare voice id instead of
    /// <c>provider:id</c> (the per-provider fallback fields).</summary>
    public bool PlainIds { get; set; }
}
