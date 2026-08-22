using System.Reflection;
using System.Text.RegularExpressions;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx;
using Xunit;

namespace ProsimCompanion.Core.Tests.Aircraft;

/// <summary>
/// Catalog fence for the typed DataRef&lt;T&gt; migration (campaign #83): the catalogs are the
/// single place wire names may live, so these tests hold the invariants the compiler cannot —
/// no wire string declared under two symbols, no symbol shared across catalog families, every
/// subscribed ProSim wire present in the A322 CSV of record, gate refs typed bool, and the
/// raw-string subscribe escape hatch confined to the audited user-content call sites.
/// </summary>
public sealed class DataRefCatalogGuardTests
{
    /// <summary>Wires knowingly absent from Prosim_A322_Dataref.csv. Each entry needs an
    /// issue-backed justification; the list existing at all is a flight-test follow-up.</summary>
    private static readonly string[] CsvExceptions =
    [
        // #83: subscribed as the preferred kg refuel target but missing from the A322 CSV.
        // Live verification pending (2026-08 flight test); resolve by regenerating the CSV
        // or deleting the subscription — then empty this list.
        "aircraft.refuel.fuelTarget.kg",
    ];

    /// <summary>Files allowed to call the raw-string SubscribeDynamic escape hatch: the typed
    /// adapter itself plus the audited sites whose names come from user-editable content.</summary>
    private static readonly string[] DynamicSubscribeAllowList =
    [
        @"src\ProsimCompanion.Core\Aircraft\TypedSubscriptionExtensions.cs",
        @"src\ProsimCompanion.Core\Checklists\ChecklistService.cs",
        @"src\ProsimCompanion.Gsx\Sync\AircraftStateCheckService.cs",
        @"src\ProsimCompanion.Speech\Abnormals\FailureMonitor.cs",
        @"src\ProsimCompanion.Speech\Checklists\ControlMonitor.cs",
        @"src\ProsimCompanion.Speech\Checklists\SpokenChecklistEngine.cs",
        @"src\ProsimCompanion.Speech\Briefings\ProcedureSource.cs",
        // #49: verify datarefs come from the user-editable commands.json.
        @"src\ProsimCompanion.Speech\Commands\ConfiguredVoiceCommands.cs",
    ];

    [Fact]
    public void NoWireString_IsDeclaredUnderTwoSymbols()
    {
        var duplicates = AllCatalogEntries()
            .GroupBy(entry => entry.Wire, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key} <- {string.Join(", ", group.Select(e => e.Symbol))}")
            .ToList();

        Assert.True(
            duplicates.Count == 0,
            "Each wire string must have exactly one catalog symbol; duplicates: "
            + string.Join("; ", duplicates));
    }

    [Fact]
    public void NoSymbolName_IsSharedAcrossCatalogFamilies()
    {
        var collisions = AllCatalogEntries()
            .GroupBy(entry => entry.Symbol.Split('.').Last(), StringComparer.Ordinal)
            .Where(group => group.Select(e => e.Wire).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(group => $"{group.Key}: {string.Join(" vs ", group.Select(e => $"{e.Symbol}={e.Wire}"))}")
            .ToList();

        Assert.True(
            collisions.Count == 0,
            "The same symbol name bound to different wires invites the Engine1N1 mistake (#83): "
            + string.Join("; ", collisions));
    }

    [Fact]
    public void EverySubscribedProsimWire_IsInTheA322Csv()
    {
        var csvNames = A322CsvNames();
        var missing = DescriptorEntries(typeof(ProsimDataRefNames))
            .Where(entry => !CsvExceptions.Contains(entry.Wire, StringComparer.Ordinal))
            .Where(entry => !csvNames.Contains(entry.Wire))
            .Select(entry => $"{entry.Symbol} ({entry.Wire})")
            .ToList();

        Assert.True(
            missing.Count == 0,
            "Subscribed ProSim wires must exist in Prosim_A322_Dataref.csv (or carry a "
            + "documented exception): " + string.Join("; ", missing));
    }

    [Fact]
    public void GateRefs_AreDeclaredBool()
    {
        var offenders = DescriptorFields(typeof(ProsimDataRefNames))
            .Where(field => WireOf(field).StartsWith("system.gates.B_", StringComparison.Ordinal))
            .Where(field => field.FieldType.GetGenericArguments()[0] != typeof(bool))
            .Select(field => field.Name)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "system.gates.B_* refs are booleans by ProSim convention (#83 decision 6): "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void SubscribeDynamic_IsCalledOnlyFromAuditedUserContentSites()
    {
        var callPattern = new Regex(@"\.\s*SubscribeDynamic\s*\(", RegexOptions.Singleline);
        var offenders = ProductionSources()
            .Where(file => callPattern.IsMatch(File.ReadAllText(file.Absolute)))
            .Where(file => !DynamicSubscribeAllowList.Any(allowed =>
                file.Relative.EndsWith(allowed, StringComparison.OrdinalIgnoreCase)))
            .Select(file => file.Relative)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Raw-string subscribes are the escape hatch for user-content names only (#83); "
            + "compile-time refs must use a typed catalog descriptor. Offenders: "
            + string.Join(", ", offenders));
    }

    /// <summary>Every catalog entry: typed descriptors, legacy const names, and the bounded
    /// ServiceDone(id) LVAR family, each as (symbol, wire).</summary>
    private static IEnumerable<(string Symbol, string Wire)> AllCatalogEntries()
    {
        Type[] catalogs =
        [
            typeof(ProsimDataRefNames),
            typeof(ProsimDataRefNames.SimVars),
            typeof(GsxLvarNames),
            typeof(CompanionLvarNames),
        ];

        foreach (var catalog in catalogs)
        {
            foreach (var entry in DescriptorEntries(catalog))
            {
                yield return entry;
            }

            foreach (var field in catalog.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral && f.FieldType == typeof(string)))
            {
                // The companion prefix is a rule, not a wire — every producible name is
                // covered by the ServiceDone expansion below.
                if (catalog == typeof(CompanionLvarNames) && field.Name == nameof(CompanionLvarNames.Prefix))
                {
                    continue;
                }

                yield return ($"{catalog.Name}.{field.Name}", (string)field.GetRawConstantValue()!);
            }
        }

        foreach (var idField in typeof(GsxServiceIds).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)))
        {
            var id = (string)idField.GetRawConstantValue()!;
            yield return ($"CompanionLvarNames.ServiceDone({id})", CompanionLvarNames.ServiceDone(id).Name);
        }
    }

    private static IEnumerable<(string Symbol, string Wire)> DescriptorEntries(Type catalog)
        => DescriptorFields(catalog).Select(field => ($"{catalog.Name}.{field.Name}", WireOf(field)));

    private static IEnumerable<FieldInfo> DescriptorFields(Type catalog)
        => catalog.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType.IsGenericType
                && (field.FieldType.GetGenericTypeDefinition() == typeof(DataRef<>)
                    || field.FieldType.GetGenericTypeDefinition() == typeof(SimVarRef<>)));

    private static string WireOf(FieldInfo field)
        => (string)field.FieldType.GetProperty("Name")!.GetValue(field.GetValue(null))!;

    private static HashSet<string> A322CsvNames()
    {
        var path = Path.Combine(RepoRoot(), "Prosim_A322_Dataref.csv");
        // Name is the first column and dataref names never contain commas or quotes.
        return File.ReadLines(path)
            .Skip(1)
            .Select(line => line.Split(',')[0].Trim())
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static IEnumerable<(string Absolute, string Relative)> ProductionSources()
    {
        var root = RepoRoot();
        var srcDir = Path.Combine(root, "src");
        return Directory
            .EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(path => (path, Path.GetRelativePath(root, path)));
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ProsimCompanion.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent!;
        }
        throw new InvalidOperationException("Repo root (ProsimCompanion.slnx) not found above the test assembly.");
    }
}
