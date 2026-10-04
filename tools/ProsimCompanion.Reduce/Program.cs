using System.Globalization;
using System.Text.Json;
using ProsimCompanion.Reduce;

// reduce --bundle <zip|folder> [--out report.json] [--probes <path>] [--max-kb 60]
//
// Exit codes: 0 report written, 1 usage error, 2 bundle refused (why on stderr).
// Every path must be below the working folder or the system temp folder (CliInput): the
// command line is outside input on the support host, so a path elsewhere is a usage error.
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
            Console.Error.WriteLine($"unknown or incomplete argument '{CliInput.SingleLine(args[i])}'");
            return 1;
    }
}

if (string.IsNullOrWhiteSpace(bundlePath))
{
    Console.Error.WriteLine("usage: reduce --bundle <zip|folder> [--out report.json] [--probes <catalog.json>] [--max-kb 60]");
    return 1;
}

if (CliInput.ResolvePath(bundlePath) is null
    || (outPath is not null && CliInput.ResolvePath(outPath) is null)
    || (probesPath is not null && CliInput.ResolvePath(probesPath) is null))
{
    Console.Error.WriteLine("path refused: --bundle, --out and --probes must be below the working folder or the system temp folder");
    return 1;
}

// From here on no path is the argument's own text: each is found on disk from the allowed
// root down (CliInput.FindExisting), so what was typed never reaches a file API or a log.
var bundleFull = CliInput.FindExisting(bundlePath);
if (bundleFull is null)
{
    Console.Error.WriteLine("bundle refused: the --bundle path is neither a zip file nor a folder");
    return 2;
}

var probesFull = probesPath is null ? null : CliInput.FindExisting(probesPath);
if (probesPath is not null && probesFull is null)
{
    Console.Error.WriteLine("probe catalog unreadable: the --probes file was not found");
    return 1;
}

var outFull = outPath is null ? null : CliInput.OutputFile(outPath);
if (outPath is not null && outFull is null)
{
    Console.Error.WriteLine("path refused: the folder for --out was not found");
    return 1;
}

string catalogJson;
try
{
    catalogJson = probesFull is null ? ProbeEvaluator.EmbeddedCatalogJson() : File.ReadAllText(probesFull);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"probe catalog unreadable: {CliInput.SingleLine(ex.Message)}");
    return 1;
}

IReadOnlyList<ProbeDefinition> catalog;
try
{
    catalog = ProbeEvaluator.LoadCatalog(catalogJson);
}
catch (JsonException ex)
{
    Console.Error.WriteLine($"probe catalog did not parse: {CliInput.SingleLine(ex.Message)}");
    return 1;
}

BundleContents bundle;
try
{
    bundle = BundleReader.Open(bundleFull);
}
catch (BundleRejectedException ex)
{
    Console.Error.WriteLine($"bundle refused: {CliInput.SingleLine(ex.Message)}");
    return 2;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
{
    Console.Error.WriteLine($"bundle refused: {CliInput.SingleLine(ex.Message)}");
    return 2;
}

using (bundle)
{
    var report = Reducer.Run(bundle, catalog, maxKb);
    var json = JsonSerializer.Serialize(report, ReduceReport.Json);
    if (outFull is null)
    {
        // Compact JSON has no raw line breaks (the serializer escapes them inside strings),
        // so this changes nothing in the report; it keeps bundle text from adding lines.
        Console.Out.Write(CliInput.SingleLine(json));
    }
    else
    {
        File.WriteAllText(outFull, json);
        Console.Error.WriteLine($"report written to {CliInput.SingleLine(outFull)} ({json.Length / 1024} KB, {report.Sessions.Count} session(s), {report.Probes.Count} probe(s) evaluated, {report.ForLlm.Count} for the LLM)");
    }
}

return 0;
