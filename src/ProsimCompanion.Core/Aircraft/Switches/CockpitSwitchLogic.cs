using System.Globalization;
using System.Text;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Aircraft.Switches;

/// <summary>
/// The pure rules of the cockpit-switch relay (2026-10-24): what a press writes, what a key
/// shows, how a row id is formed. No ProSim, no clock — unit-tested on their own.
/// </summary>
public static class CockpitSwitchLogic
{
    /// <summary>
    /// The value a press writes. <c>Toggle</c>: the other of the first two catalogue values
    /// (or 0 ↔ 1 without a catalogue list) — a value outside the pair goes to the first.
    /// <c>Cycle</c>: the next catalogue value, wrapping; <paramref name="longPress"/> steps
    /// back. <c>Momentary</c> never gets here (the caller presses instead of writing).
    /// </summary>
    public static int NextValue(CockpitPressMode mode, int current, IReadOnlyList<SwitchValue> values, bool longPress)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count < 2)
        {
            return current == 0 ? 1 : 0;
        }

        if (mode == CockpitPressMode.Toggle)
        {
            return current == values[0].Value ? values[1].Value : values[0].Value;
        }

        var index = -1;
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i].Value == current)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return values[0].Value;
        }

        var next = longPress ? index - 1 : index + 1;
        if (next < 0)
        {
            next = values.Count - 1;
        }
        else if (next >= values.Count)
        {
            next = 0;
        }

        return values[next].Value;
    }

    /// <summary>
    /// The <c>mode</c> word the <c>cockpit.press</c> session event records: the press mode in
    /// camelCase, and <c>cycleBack</c> only when a Cycle row was long-pressed. A Toggle or
    /// Momentary key held past the plugin's long-press threshold still toggles / presses, so
    /// the audit trail must say so (review 2026-10-25: every long press used to read cycleBack).
    /// </summary>
    public static string ModeLabel(CockpitPressMode mode, bool longPress) => mode switch
    {
        CockpitPressMode.Cycle when longPress => "cycleBack",
        CockpitPressMode.Cycle => "cycle",
        CockpitPressMode.Momentary => "momentary",
        _ => "toggle",
    };

    /// <summary>
    /// Whether <c>cockpit.set</c> may write <paramref name="value"/>: a value from the catalogue
    /// list, or — for a switch the catalogue lists without values — 0 or 1, the same pair a
    /// Toggle press moves between. Presses are bounded (ADR-0015); a list-less switch is no
    /// exception.
    /// </summary>
    public static bool IsCatalogueValue(IReadOnlyList<SwitchValue> values, int value)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            return value is 0 or 1;
        }

        foreach (var v in values)
        {
            if (v.Value == value)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The catalogue label of a value ("Pushed", "NAV"), else the number.</summary>
    public static string Label(IReadOnlyList<SwitchValue> values, int value)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var v in values)
        {
            if (v.Value == value)
            {
                return v.Label;
            }
        }

        return value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The key-face state of a row from the cached dataref values.</summary>
    public static CockpitSwitchState Evaluate(CockpitSwitchRow row, int? value, int? upperIndicator, int? lowerIndicator, bool stale, IReadOnlyList<SwitchValue> values)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(values);

        var upperLit = HasText(row.UpperIndicator) && (upperIndicator ?? 0) != 0;
        var lowerLit = HasText(row.LowerIndicator)
            ? (lowerIndicator ?? 0) != 0
            : (value ?? 0) != 0;
        var position = value is { } v ? Label(values, v) : null;
        return new CockpitSwitchState(row.Id, value, position, upperLit, lowerLit, stale);
    }

    /// <summary>A row id from a dataref: <c>system.switches.S_OH_HYD_LMV_BLUE</c> →
    /// <c>oh_hyd_lmv_blue</c>. Lower-case letters, digits and underscores only.</summary>
    public static string SlugFor(string? dataRef)
    {
        var name = (dataRef ?? "").Trim();
        if (name.StartsWith(SwitchCatalog.Prefix, StringComparison.Ordinal))
        {
            name = name[SwitchCatalog.Prefix.Length..];
        }

        if (name.StartsWith("S_", StringComparison.Ordinal))
        {
            name = name[2..];
        }

        var builder = new StringBuilder(name.Length);
        var lastUnderscore = false;
        foreach (var c in name)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
                lastUnderscore = false;
            }
            else if (!lastUnderscore && builder.Length > 0)
            {
                builder.Append('_');
                lastUnderscore = true;
            }
        }

        return builder.ToString().TrimEnd('_');
    }

    /// <summary>A CSS hex colour (#rgb or #rrggbb); the key renderer trusts nothing else.</summary>
    public static bool IsHexColor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var t = text.Trim();
        if (t.Length is not (4 or 7) || t[0] != '#')
        {
            return false;
        }

        for (var i = 1; i < t.Length; i++)
        {
            if (!char.IsAsciiHexDigit(t[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Case-insensitive, trimmed row lookup.</summary>
    public static CockpitSwitchRow? FindRow(IReadOnlyList<CockpitSwitchRow> rows, string? id)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var wanted = id.Trim();
        foreach (var row in rows)
        {
            if (string.Equals(row.Id.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
            {
                return row;
            }
        }

        return null;
    }

    private static bool HasText(string? s) => !string.IsNullOrWhiteSpace(s);
}
