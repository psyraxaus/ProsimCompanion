using System.Globalization;
using System.Text.Json;
using ProsimCompanion.Reduce;

// reduce --bundle <zip|folder> [--out report.json] [--probes <path>] [--max-kb 60]
//
// Exit codes: 0 report written, 1 usage error, 2 bundle refused (why on stderr).
// The support pipeline (n8n) runs this on the Linux host and feeds the JSON to the LLM
// step; the app's own debrief uses the same Core reducer, so the two cannot disagree.

string? bundlePath = null;
string? outPath = null;
string? probesPath = null;
var maxKb = Reducer.DefaultMaxKb;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--bundle" when i + 1 < args.Length:
            bundlePath = args[++i];
            break;
        case "--out" when i + 1 < args.Length:
            outPath = args[++i];
            break;
        case "--probes" when i + 1 < args.Length:
            probesPath = args[++i];
            break;
        case "--max-kb" when i + 1 < args.Length
            && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var kb):
            maxKb = kb;
            i++;
            break;
        case "-h" or "--help":
            Console.Error.WriteLine("usage: reduce --bundle <zip|folder> [--out report.json] [--probes <catalog.json>] [--max-kb 60]");
            return 1;
        default:
            Console.Error.WriteLine($"unknown or incomplete argument '{args[i]}'");
            return 1;
    }
}

if (string.IsNullOrWhiteSpace(bundlePath))
{
    Console.Error.WriteLine("usage: reduce --bundle <zip|folder> [--out report.json] [--probes <catalog.json>] [--max-kb 60]");
    return 1;
}

string catalogJson;
try
{
    catalogJson = probesPath is null ? ProbeEvaluator.EmbeddedCatalogJson() : File.ReadAllText(probesPath);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"probe catalog unreadable: {ex.Message}");
    return 1;
}

IReadOnlyList<ProbeDefinition> catalog;
try
{
    catalog = ProbeEvaluator.LoadCatalog(catalogJson);
}
catch (JsonException ex)
{
    Console.Error.WriteLine($"probe catalog did not parse: {ex.Message}");
    return 1;
}

BundleContents bundle;
try
{
    bundle = BundleReader.Open(bundlePath);
}
catch (BundleRejectedException ex)
{
    Console.Error.WriteLine($"bundle refused: {ex.Message}");
    return 2;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
{
    Console.Error.WriteLine($"bundle refused: {ex.Message}");
    return 2;
}

using (bundle)
{
    var report = Reducer.Run(bundle, catalog, maxKb);
    var json = JsonSerializer.Serialize(report, ReduceReport.Json);
    if (outPath is null)
    {
        Console.Out.Write(json);
    }
    else
    {
        File.WriteAllText(outPath, json);
        Console.Error.WriteLine($"report written to {outPath} ({json.Length / 1024} KB, {report.Sessions.Count} session(s), {report.Probes.Count} probe(s) evaluated, {report.ForLlm.Count} for the LLM)");
    }
}

return 0;
