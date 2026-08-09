using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using ProsimCompanion.Gsx.Mirror;
using ProsimCompanion.Gsx.Protocol;

namespace ProsimCompanion.Gsx.Gate;

/// <summary>A gate reference as it appears in gate.select payloads/candidates.</summary>
public sealed record GsxGateRef(string? UiName, string? Gate, int? Number, string? BglName)
{
    /// <summary>The token to resend on the disambiguation retry (bglName preferred).</summary>
    public string? ResendToken => BglName ?? UiName ?? Gate;

    public static GsxGateRef Parse(JsonObject node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return new GsxGateRef(
            GsxFrame.ReadString(node["uiName"]),
            GsxFrame.ReadString(node["gate"]),
            GsxFrame.ReadInt(node["number"]),
            GsxFrame.ReadString(node["bglName"]));
    }
}

/// <summary>
/// Pure gate-selection logic: normalization, disambiguation candidate picking, the SetGate_*
/// readback letter map, and nearest-name suggestions. See docs/integrations/gsx-remote-api.md §6.
/// </summary>
public static class GsxGateResolver
{
    /// <summary>Strip non-alphanumerics, uppercase — the comparison form for all gate tokens.</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToUpperInvariant(c));
            }
        }
        return builder.ToString();
    }

    /// <summary>
    /// Picks the single unambiguous candidate for a disambiguation retry: exact normalized match
    /// on uiName or gate first; else a unique normalized suffix match on gate (falling back to
    /// uiName). Null when no unique candidate exists (fail with the candidate list).
    /// </summary>
    public static GsxGateRef? PickUniqueCandidate(IReadOnlyList<GsxGateRef> candidates, string requestedGate)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var requested = Normalize(requestedGate);
        if (requested.Length == 0)
        {
            return null;
        }

        var exact = candidates
            .Where(c => Normalize(c.UiName) == requested || Normalize(c.Gate) == requested)
            .ToList();
        if (exact.Count == 1)
        {
            return exact[0];
        }
        if (exact.Count > 1)
        {
            return null;
        }

        var suffix = candidates
            .Where(c =>
            {
                var token = Normalize(c.Gate);
                if (token.Length == 0)
                {
                    token = Normalize(c.UiName);
                }
                return token.Length > 0 && token.EndsWith(requested, StringComparison.Ordinal);
            })
            .ToList();
        return suffix.Count == 1 ? suffix[0] : null;
    }

    /// <summary>
    /// Formats the SetGate_* readback LVARs into a display gate, or null when unassigned.
    /// Letter map: Name 0 = NONE; 10 = "Gate {n}"; 12..37 = A..Z → "{Letter}{n}";
    /// Suffix −1 = unassigned sentinel; anything else unassigned.
    /// </summary>
    public static string? FormatReadback(int name, int number, int suffix)
    {
        if (suffix == -1 || name == 0)
        {
            return null;
        }

        if (name == 10)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Gate {number}");
        }

        if (name is >= 12 and <= 37)
        {
            var letter = (char)('A' + (name - 12));
            return string.Create(CultureInfo.InvariantCulture, $"{letter}{number}");
        }

        return null;
    }

    /// <summary>
    /// Resolves a user token ("D5") to GSX's own parking display name (" Gate D5" at EHAM —
    /// prefixes and whitespace included). gate.select matches exactly against those names, so a
    /// bare token fails not_found even when the gate exists (issue #36). Returns the canonical
    /// name on a normalized-exact match, or on a UNIQUE normalized-suffix match; null otherwise
    /// (unknown gate, or ambiguous — e.g. " Gate D5" vs "Stand D5" both ending in D5).
    /// </summary>
    public static string? ResolveCanonical(IReadOnlyList<GsxParking> parkings, string requestedGate)
    {
        ArgumentNullException.ThrowIfNull(parkings);
        var requested = Normalize(requestedGate);
        if (requested.Length == 0)
        {
            return null;
        }

        var names = parkings
            .Select(p => p.UiGateName ?? p.UiName ?? p.BglName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var exact = names.Where(n => Normalize(n) == requested).ToList();
        if (exact.Count == 1)
        {
            return exact[0];
        }
        if (exact.Count > 1)
        {
            return null;
        }

        var suffix = names
            .Where(n => Normalize(n).EndsWith(requested, StringComparison.Ordinal))
            .ToList();
        return suffix.Count == 1 ? suffix[0] : null;
    }

    /// <summary>
    /// Resolves the CURRENT gate-context key (the parking uiName GSX pushes, e.g.
    /// "D-Pier =&lt; Medium | Gate D27") to the token <c>gate.select</c> accepts — the parking's
    /// display gate name. Used to re-anchor GSX's remembered facility to the stand the aircraft
    /// actually occupies at departure prep (issue #44: GSX kept the previous session's gate and
    /// silently dropped every service trigger). Null when the mirror doesn't know the parking.
    /// </summary>
    public static string? ResolveAnchorToken(IReadOnlyList<GsxParking> parkings, string gateContextKey)
    {
        ArgumentNullException.ThrowIfNull(parkings);
        var key = Normalize(gateContextKey);
        if (key.Length == 0)
        {
            return null;
        }

        var parking = parkings.FirstOrDefault(p =>
            Normalize(p.UiName) == key || Normalize(p.UiGateName) == key || Normalize(p.BglName) == key);
        return parking is null ? null : parking.UiGateName ?? parking.UiName ?? parking.BglName;
    }

    /// <summary>Nearest-name suggestions for a not_found failure: exact → suffix → contains,
    /// max 3, drawn from the mirrored parkings.</summary>
    public static IReadOnlyList<string> NearestNames(IReadOnlyList<GsxParking> parkings, string requestedGate)
    {
        ArgumentNullException.ThrowIfNull(parkings);
        var requested = Normalize(requestedGate);
        if (requested.Length == 0)
        {
            return [];
        }

        var names = parkings
            .Select(p => p.UiGateName ?? p.UiName ?? p.BglName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<string> ranked =
        [
            .. names.Where(n => Normalize(n) == requested),
            .. names.Where(n => Normalize(n).EndsWith(requested, StringComparison.Ordinal) && Normalize(n) != requested),
            .. names.Where(n => Normalize(n).Contains(requested, StringComparison.Ordinal)
                && !Normalize(n).EndsWith(requested, StringComparison.Ordinal)
                && Normalize(n) != requested),
        ];

        return [.. ranked.Take(3)];
    }
}
