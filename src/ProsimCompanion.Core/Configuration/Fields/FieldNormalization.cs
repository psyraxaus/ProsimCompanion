namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// What a settings page does to a text value on Save before the diff decides what changed
/// (candidate C3, 2026-10-19). Until then every page carried its own <c>OnBeforeSave</c> with
/// fifty <c>Trim()</c> / <c>ToLowerInvariant()</c> / <c>Trim('"')</c> lines; the rule now sits
/// on the property and <see cref="OptionFieldNormalizer"/> applies it to every draft. Applied
/// in this order: whitespace trim, then the quote strip, then the case fold.
/// </summary>
[Flags]
public enum FieldNormalization
{
    /// <summary>Saved exactly as typed (spoken wording, where a leading space is the pilot's).</summary>
    None = 0,

    /// <summary>Leading and trailing whitespace removed.</summary>
    Trim = 1,

    /// <summary>Surrounding double quotes removed — Explorer's "Copy as path" wraps paths in them.</summary>
    StripQuotes = 2,

    /// <summary>Folded to lower case (the camelCase choice values: "left", "manual").</summary>
    LowerCase = 4,

    /// <summary>Folded to upper case (ICAO codes, runway designators, procedure names).</summary>
    UpperCase = 8,

    /// <summary>The path rule: trim, then strip the quotes.</summary>
    Path = Trim | StripQuotes,

    /// <summary>The choice-value rule: trim, then lower case.</summary>
    TrimLower = Trim | LowerCase,

    /// <summary>The aviation-identifier rule: trim, then upper case.</summary>
    TrimUpper = Trim | UpperCase,
}
