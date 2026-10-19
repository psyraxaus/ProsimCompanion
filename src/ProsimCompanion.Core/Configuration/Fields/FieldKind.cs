namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Which settings control renders an option property (candidate C3, 2026-10-19). The kind is
/// declared once on the property through <see cref="OptionFieldAttribute"/>; the web page's
/// <c>OptionField</c> renderer maps it onto the existing field components (BoolField,
/// NumberField, RangeField, TextField, SelectField, VoicePickerField), so the DOM a pilot sees
/// is unchanged while the knowledge of HOW a property is edited lives beside the property.
/// </summary>
public enum FieldKind
{
    /// <summary>A checkbox row over a <see cref="bool"/>.</summary>
    Bool,

    /// <summary>A numeric row over an <see cref="int"/> or a <see cref="double"/>, with its
    /// default shown and a reset button (ADR-0010).</summary>
    Number,

    /// <summary>The lower bound of a min/max pair shown as one "from … to …" row; the upper
    /// bound is the sibling property <see cref="OptionFieldAttribute.RangeEnd"/> names.</summary>
    Range,

    /// <summary>The upper bound of a <see cref="Range"/> pair. Carries no texts of its own —
    /// it is rendered by the lower bound's row and named here only so the completeness test
    /// knows the property has a control.</summary>
    RangeUpper,

    /// <summary>A text row over a <see cref="string"/>; its shape check and Browse… dialog come
    /// from <see cref="OptionFieldAttribute.TextKind"/>.</summary>
    Text,

    /// <summary>A dropdown over a <see cref="string"/>, an <see cref="int"/> or an enum; the
    /// rows come from the choice list <see cref="OptionFieldAttribute.Choices"/> names.</summary>
    Select,

    /// <summary>The voice picker over the live provider lists (owner, 2026-10-12).</summary>
    VoicePicker,
}
