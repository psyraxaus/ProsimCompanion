using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ProsimCompanion.Core.Aircraft.Switches;

/// <summary>One value a switch can report, as ProSim describes it ("0:INT").</summary>
public sealed record SwitchValue(int Value, string Label);

/// <summary>
/// One <c>system.switches.*</c> dataref the smart-button picker can offer (2026-10-17):
/// the wire name, ProSim's description with the value list stripped (<see cref="Title"/>),
/// the cockpit area it belongs to (<see cref="Group"/>, a stable id the web UI translates)
/// and the values the description enumerates — empty for a switch ProSim describes without
/// a "[0:…, 1:…]" list, which the page then offers as a plain number.
/// </summary>
public sealed record SwitchDataRef(string Name, string Title, string Group, IReadOnlyList<SwitchValue> Values)
{
    /// <summary>The name without the <c>system.switches.</c> prefix — what a cockpit builder
    /// recognises from the ProSim IOS.</summary>
    public string ShortName => Name.StartsWith(SwitchCatalog.Prefix, StringComparison.Ordinal)
        ? Name[SwitchCatalog.Prefix.Length..]
        : Name;
}

/// <summary>Where the current switch list came from.</summary>
public enum SwitchCatalogSource
{
    /// <summary>The <c>Prosim_A322_Dataref.csv</c> shipped inside the app — always available.</summary>
    Shipped,

    /// <summary>Read from the running ProSim through the SDK's dataref description list, so a
    /// newer ProSim shows switches the shipped file does not know.</summary>
    Live,
}

/// <summary>One raw row from either source before filtering.</summary>
public sealed record SwitchCatalogRow(string Name, string Description, bool CanRead);

/// <summary>The switch list the picker shows, with its provenance.</summary>
/// <param name="Switches">Readable <c>system.switches.*</c> datarefs, in catalogue order.</param>
/// <param name="Source">Shipped file or live ProSim.</param>
/// <param name="ReadAtUtc">When a live list was read; null for the shipped file.</param>
public sealed record SwitchCatalogSnapshot(IReadOnlyList<SwitchDataRef> Switches, SwitchCatalogSource Source, DateTimeOffset? ReadAtUtc)
{
    /// <summary>The entry for a wire name, or null when the list does not know it (a typed-in
    /// dataref, or a switch this ProSim build lacks).</summary>
    public SwitchDataRef? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var trimmed = name.Trim();
        return Switches.FirstOrDefault(s => string.Equals(s.Name, trimmed, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Builds the switch list from either source (the hybrid picker, owner pick 2026-10-17): the
/// shipped CSV is parsed once at startup so the dropdown works with ProSim off; a live read
/// replaces it whenever ProSim connects. Pure: rows in, snapshot out.
/// </summary>
public static partial class SwitchCatalog
{
    /// <summary>The dataref namespace every cockpit switch lives in.</summary>
    public const string Prefix = "system.switches.";

    /// <summary>Group ids in display order; the web UI translates each one.</summary>
    public static readonly IReadOnlyList<string> GroupOrder =
    [
        "overhead", "pedestal", "mcdu", "audio", "fcu", "mainPanel", "ecam", "dcdu",
        "radios", "flightControls", "engines", "circuitBreakers", "other",
    ];

    private const string ShippedResource = "ProsimCompanion.Core.Aircraft.Switches.Prosim_A322_Dataref.csv";

    /// <summary>Parses the CSV shipped inside the assembly.</summary>
    public static SwitchCatalogSnapshot LoadShipped()
    {
        using var stream = typeof(SwitchCatalog).Assembly.GetManifestResourceStream(ShippedResource)
            ?? throw new InvalidOperationException($"Embedded resource {ShippedResource} is missing");
        using var reader = new StreamReader(stream);
        return Build(ParseCsv(reader), SwitchCatalogSource.Shipped, null);
    }

    /// <summary>Filters the rows to readable <c>system.switches.*</c> entries and shapes them.</summary>
    public static SwitchCatalogSnapshot Build(IEnumerable<SwitchCatalogRow> rows, SwitchCatalogSource source, DateTimeOffset? readAtUtc)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var switches = rows
            .Where(r => r.CanRead && r.Name.StartsWith(Prefix, StringComparison.Ordinal))
            .Select(Describe)
            .ToList();
        return new SwitchCatalogSnapshot(switches, source, readAtUtc);
    }

    /// <summary>Reads the ProSim dataref CSV (<c>Name,Description,Data Type,Data Unit,Can
    /// Read,Can Write</c>; quoted fields with doubled quotes). The header row is skipped.</summary>
    public static IReadOnlyList<SwitchCatalogRow> ParseCsv(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var rows = new List<SwitchCatalogRow>();
        var first = true;
        while (reader.ReadLine() is { } line)
        {
            if (first)
            {
                first = false;
                continue;
            }

            var fields = SplitCsv(line);
            if (fields.Count < 5)
            {
                continue;
            }

            rows.Add(new SwitchCatalogRow(fields[0].Trim(), fields[1], fields[4].Trim().Equals("TRUE", StringComparison.OrdinalIgnoreCase)));
        }

        return rows;
    }

    /// <summary>The cockpit area for a wire name (the token after <c>S_</c>, or <c>CB_</c>).</summary>
    public static string GroupOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var local = name.StartsWith(Prefix, StringComparison.Ordinal) ? name[Prefix.Length..] : name;
        if (local.StartsWith("CB_", StringComparison.Ordinal))
        {
            return "circuitBreakers";
        }

        var parts = local.Split('_');
        var area = parts.Length > 1 && parts[0] == "S" ? parts[1] : parts[0];
        return area switch
        {
            "OH" => "overhead",
            "PED" => "pedestal",
            "CDU1" or "CDU2" => "mcdu",
            "ASP" or "ASP2" or "ASP3" or "HAND" or "SIDESTICK" or "AUDIO" => "audio",
            "FCU" => "fcu",
            "MIP" or "DISPLAY" or "STANDBY" or "CHART" => "mainPanel",
            "ECAM" => "ecam",
            "DCDU1" or "DCDU2" => "dcdu",
            "COM1" or "COM2" or "COM3" or "NAV1" or "NAV2" or "ILS1" or "ILS2" or "ADF1" or "ADF2"
                or "XPDR" or "TCAS" or "WR" or "WR2" => "radios",
            "FC" or "HYD" => "flightControls",
            "ENG" or "REFUEL" => "engines",
            _ => "other",
        };
    }

    /// <summary>Splits "ASP INT/RAD Switch [0:INT, 1:Off, 2:RAD]" into the title and the values.</summary>
    public static (string Title, IReadOnlyList<SwitchValue> Values) ParseDescription(string? description)
    {
        var text = (description ?? "").Trim();
        var match = ValueList().Match(text);
        if (!match.Success)
        {
            return (text, []);
        }

        var values = new List<SwitchValue>();
        foreach (var item in match.Groups[1].Value.Split(','))
        {
            var colon = item.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || !int.TryParse(item[..colon].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                // Not a "number:label" list (a range note, say): the switch is a plain number.
                return (text, []);
            }

            values.Add(new SwitchValue(value, item[(colon + 1)..].Trim()));
        }

        return (text[..match.Index].Trim(), values);
    }

    private static SwitchDataRef Describe(SwitchCatalogRow row)
    {
        var (title, values) = ParseDescription(row.Description);
        return new SwitchDataRef(row.Name, title.Length > 0 ? title : row.Name[Prefix.Length..], GroupOf(row.Name), values);
    }

    private static List<string> SplitCsv(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }

    [GeneratedRegex(@"\[([^\]]*)\]\s*$")]
    private static partial Regex ValueList();
}
