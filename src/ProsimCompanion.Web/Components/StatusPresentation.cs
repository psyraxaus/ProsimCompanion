using Microsoft.Extensions.Localization;

namespace ProsimCompanion.Web.Components;

/// <summary>
/// A text a status row shows, as the presentation modules hand it to the markup: either a
/// language-pack id with its placeholder arguments (ADR-0014 — the module never holds the
/// per-circuit localizer) or a literal the pack does not own (a GSX wire state, a dataref
/// word, an ICAO code, the neutral dash). An argument may itself be a <see cref="DisplayText"/>
/// when a sentence embeds a translated word ("re-asked · counts as no at 10:45").
/// </summary>
/// <param name="TextId">The pack id, or null for a literal.</param>
/// <param name="Arguments">The <c>{0}</c>… placeholder values of <paramref name="TextId"/>.</param>
/// <param name="Literal">The literal text when <paramref name="TextId"/> is null.</param>
public sealed record DisplayText(string? TextId, IReadOnlyList<object> Arguments, string? Literal)
{
    /// <summary>The neutral "not known yet" cell every card uses.</summary>
    public static DisplayText Dash { get; } = Raw("—");

    /// <summary>A pack text with its placeholder arguments.</summary>
    public static DisplayText Id(string textId, params object[] arguments)
    {
        ArgumentNullException.ThrowIfNull(textId);
        return new DisplayText(textId, arguments, null);
    }

    /// <summary>A text the pack does not own, shown as it is.</summary>
    public static DisplayText Raw(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new DisplayText(null, [], text);
    }

    /// <summary>The literal, or null — a literal with no text at all stays null so the markup
    /// can fall back to the dash.</summary>
    public static DisplayText? RawOrNull(string? text) => text is null ? null : Raw(text);

    /// <summary>The text in the page's language, nested arguments resolved first.</summary>
    public string Resolve(IStringLocalizer text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (TextId is null)
        {
            return Literal ?? "";
        }

        if (Arguments.Count == 0)
        {
            return text[TextId].Value;
        }

        var arguments = new object[Arguments.Count];
        for (var i = 0; i < arguments.Length; i++)
        {
            arguments[i] = Arguments[i] is DisplayText nested ? nested.Resolve(text) : Arguments[i];
        }

        return text[TextId, arguments].Value;
    }
}

/// <summary>The three row shapes of a status card — the Prosim2GSX Indicator / KV / pill rows
/// the Flight Status cards and the GSX status panel both draw.</summary>
public enum StatusRowKind
{
    /// <summary>A label, a green/red dot and "OK" / "—".</summary>
    Indicator,

    /// <summary>A label and a monospace value.</summary>
    Value,

    /// <summary>A label and a toned pill with an optional hover title.</summary>
    Pill,
}

/// <summary>
/// One row of a status card as a presentation module produces it and the markup renders it:
/// the label's text id, the shape, and the value with its tone and hover title. The
/// <paramref name="Id"/> names the row so the markup can hang a control on it (the Re-check
/// button, the fuel-confirm button) without the module knowing about buttons.
/// </summary>
/// <param name="Id">A stable name for the row ("aircraftState"), unique within its card.</param>
/// <param name="LabelTextId">The pack id of the row label.</param>
/// <param name="Kind">Which of the three shapes to draw.</param>
/// <param name="Ok">Indicator rows: the dot colour.</param>
/// <param name="Value">The cell text (pill word, monospace value, or "OK" / "—").</param>
/// <param name="Tone">Pill rows: the <c>tone-*</c> class; empty for the other shapes.</param>
/// <param name="Tooltip">Pill rows: the hover title, or null for none.</param>
public sealed record StatusRow(
    string Id,
    string LabelTextId,
    StatusRowKind Kind,
    bool Ok,
    DisplayText Value,
    string Tone,
    DisplayText? Tooltip)
{
    /// <summary>The "OK" / "—" indicator row: the dot is green with "OK" or red with the dash.</summary>
    public static StatusRow Indicator(string id, string labelTextId, bool ok)
        => new(id, labelTextId, StatusRowKind.Indicator, ok, ok ? DisplayText.Id("flightStatus.common.ok") : DisplayText.Dash, "", null);

    /// <summary>A monospace value row; null shows the dash.</summary>
    public static StatusRow Mono(string id, string labelTextId, string? value)
        => Mono(id, labelTextId, DisplayText.RawOrNull(value) ?? DisplayText.Dash);

    /// <summary>A monospace value row.</summary>
    public static StatusRow Mono(string id, string labelTextId, DisplayText value)
        => new(id, labelTextId, StatusRowKind.Value, false, value, "", null);

    /// <summary>A pill row; a null value shows the dash.</summary>
    public static StatusRow Pill(string id, string labelTextId, string tone, DisplayText? value, DisplayText? tooltip = null)
        => new(id, labelTextId, StatusRowKind.Pill, false, value ?? DisplayText.Dash, tone, tooltip);
}
