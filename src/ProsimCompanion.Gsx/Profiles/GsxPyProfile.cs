using System.Globalization;
using System.Text.RegularExpressions;
using ProsimCompanion.Core.Airports.Parking;

namespace ProsimCompanion.Gsx.Profiles;

/// <summary>One name group of a GSX <c>.py</c> customisation (<c>GATE_W : { … }</c>).</summary>
/// <param name="DefaultTemplate">The <c>None</c> entry's template, applied to every stand of
/// the group not listed explicitly; null when the group has no default or it is not a literal.</param>
/// <param name="Entries">Explicit stands by (number, suffix) → template, or null when the stand
/// is listed but its name is computed by code the reader cannot evaluate.</param>
public sealed record GsxPyGroup(string? DefaultTemplate, IReadOnlyDictionary<(int Number, string Suffix), string?> Entries);

/// <summary>GSX's resolved names for one stand: the full menu line and the part after the bar.</summary>
public sealed record GsxResolvedName(string UiName, string GateName);

/// <summary>
/// The parking-name part of a GSX airport <c>.py</c> customisation. Only the static shape is
/// read — <c>X = CustomizedName("Terminal 1 | Gate #§", 1)</c> assignments and the
/// <c>parkings = { GROUP : { key : (X, …) } }</c> dict — because that is what decides the names
/// GSX prints and <c>gate.select</c> matches. Stop-position functions and anything computed
/// (<c>PresetNames(...)</c>) are ignored; such stands stay known (membership) but unnamed.
/// </summary>
public sealed record GsxPyProfile(IReadOnlyDictionary<ParkingName, GsxPyGroup> Groups)
{
    public static readonly GsxPyProfile Empty = new(new Dictionary<ParkingName, GsxPyGroup>());

    /// <summary>Every stand the file lists explicitly.</summary>
    public IEnumerable<ParkingIdentity> ListedStands()
        => Groups.SelectMany(g => g.Value.Entries.Keys.Select(k => new ParkingIdentity(g.Key, k.Number, k.Suffix)));

    /// <summary>GSX's names for the stand, or null when the file does not decide them (no
    /// group, no template, or a computed one). Manual p.127: <c>#</c> → number, <c>§</c> →
    /// suffix — the GATE letter is never expanded, which is why "Gate W40" prints as "Gate 40"
    /// under an <c>"… | Gate #§"</c> template (EFHK, 2026-10-04).</summary>
    public GsxResolvedName? Resolve(ParkingIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!Groups.TryGetValue(identity.Name, out var group))
        {
            return null;
        }

        string? template;
        if (group.Entries.TryGetValue((identity.Number, identity.Suffix), out var explicitTemplate))
        {
            template = explicitTemplate;
        }
        else
        {
            template = group.DefaultTemplate;
        }

        if (string.IsNullOrWhiteSpace(template))
        {
            return null;
        }

        var expanded = template
            .Replace("#", identity.Number.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("§", identity.Suffix, StringComparison.Ordinal)
            .Trim();
        var bar = expanded.LastIndexOf('|');
        var gateName = bar >= 0 ? expanded[(bar + 1)..].Trim() : expanded;
        return new GsxResolvedName(expanded, gateName);
    }
}

public static class GsxPyProfileParser
{
    private static readonly Regex Assignment = new(
        @"(?m)^\s*(?<var>[A-Za-z_]\w*)\s*=\s*CustomizedName\s*\(\s*[urb]?(?<q>[""'])(?<tpl>(?:\\.|(?!\k<q>).)*)\k<q>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ParkingsStart = new(
        @"(?m)^\s*parkings\s*=\s*(?=\{)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static GsxPyProfile Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var templates = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Assignment.Matches(text))
        {
            templates[m.Groups["var"].Value] = m.Groups["tpl"].Value;
        }

        var start = ParkingsStart.Match(text);
        if (!start.Success)
        {
            return GsxPyProfile.Empty;
        }

        if (PyLiteral.Parse(text[(start.Index + start.Length)..]) is not Dictionary<string, object?> dict)
        {
            return GsxPyProfile.Empty;
        }

        var groups = new Dictionary<ParkingName, GsxPyGroup>();
        foreach (var (groupKey, groupValue) in dict)
        {
            if (ParkingIdentity.ParsePyGroupKey(groupKey) is not { } name || groupValue is not Dictionary<string, object?> entries)
            {
                continue;
            }

            string? defaultTemplate = null;
            var stands = new Dictionary<(int, string), string?>();
            foreach (var (key, value) in entries)
            {
                var template = TemplateOf(value, templates);
                if (key == "None")
                {
                    defaultTemplate = template;
                    continue;
                }

                if (ParseStandKey(key) is { } id)
                {
                    stands[id] = template;
                }
            }

            // A group listed twice (seen in hand-edited files) merges; the later default wins
            // only when the earlier had none, matching how Python would keep the last dict —
            // close enough for names, and explicit entries never collide.
            if (groups.TryGetValue(name, out var existing))
            {
                var merged = new Dictionary<(int, string), string?>(existing.Entries);
                foreach (var (k, v) in stands)
                {
                    merged[k] = v;
                }
                groups[name] = new GsxPyGroup(defaultTemplate ?? existing.DefaultTemplate, merged);
            }
            else
            {
                groups[name] = new GsxPyGroup(defaultTemplate, stands);
            }
        }

        return new GsxPyProfile(groups);
    }

    /// <summary>"40" → (40, ""); "34B" → (34, "B"); "218r" → (218, "R"); anything else null.</summary>
    public static (int Number, string Suffix)? ParseStandKey(string key)
    {
        var k = key.Trim();
        var digits = 0;
        while (digits < k.Length && char.IsDigit(k[digits]))
        {
            digits++;
        }

        if (digits == 0 || !int.TryParse(k[..digits], NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }

        var suffix = k[digits..].Trim();
        if (suffix.Length > 2 || !suffix.All(char.IsLetter))
        {
            return null;
        }

        return (number, suffix.ToUpperInvariant());
    }

    private static string? TemplateOf(object? value, Dictionary<string, string> templates)
    {
        if (value is not List<object?> { Count: > 0 } tuple)
        {
            return null;
        }

        return tuple[0] switch
        {
            PyLiteral.PyIdentifier id when templates.TryGetValue(id.Name, out var t) => t,
            PyLiteral.PyCall { Name: "CustomizedName" } call when call.Args.Count > 0 && call.Args[0] is string s => s,
            _ => null,
        };
    }
}
