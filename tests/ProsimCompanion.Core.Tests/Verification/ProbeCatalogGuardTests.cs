using System.Text.Json;
using System.Text.RegularExpressions;
using ProsimCompanion.Speech.Arbiter;
using Xunit;

namespace ProsimCompanion.Core.Tests.Verification;

/// <summary>
/// Lint for <c>docs/agents/verification-probes.json</c> (issue #233): the catalog is hand-edited
/// JSON that the support reducer and the flight-verification agent both read, and nothing
/// checked it — 43 probes sat <c>open</c> on closed issues, two carried <c>issue: null</c>, 28
/// signature blocks had their fields at the wrong level (so they evaluated as untested) and one
/// asserted on an event name no code emits (#227, a vacuous pass). These tests hold the shape
/// the catalog header documents and, above all, that every event <c>type</c> a machine block
/// names is one the code can write — derived from the source tree at test time, never a list
/// typed by hand. The GitHub state of each issue is outside the test's reach; that part of the
/// hygiene stays with the maintenance rule in docs/agents/flight-verification.md.
/// </summary>
public sealed class ProbeCatalogGuardTests
{
    /// <summary>The state vocabulary of flight-verification.md "Maintaining the probe file".</summary>
    private static readonly string[] AllowedStates = ["open", "fixed-unverified", "closed-regression-watch"];

    /// <summary>The machine kinds ProbeEvaluator implements (and the header documents).</summary>
    private static readonly string[] MachineKinds = ["signature", "required-fields", "duplicate-within", "consecutive-duplicates"];

    /// <summary>Property names inside a machine block whose string value is a regular expression.
    /// None today (the evaluator has no regex operator yet); the test is ready for one.</summary>
    private static readonly string[] RegexProperties = ["matches", "regex", "pattern"];

    /// <summary>Names every emitter family must contribute — if the scan below ever stops seeing
    /// one of these, the derivation broke, not the catalog.</summary>
    private static readonly string[] EmitterCanaries =
    [
        "phase-changed",            // literal .Record("…")
        "session-started",          // const passed by name (SessionHeader.StartedEvent)
        "sim-paused",               // literal inside a switch expression (CoreBootstrapService)
        "scenario.clearFailed",     // literal inside a ternary (ScenarioService)
        "gsx-cycle-reset",          // IGsxDepartureDecisionLog.RecordEvent("…")
        "gsx-turnaround-question",  // RecordEventEffect("…") through the departure pump
        "speech.spoken",            // "speech." + SpeechEventKind
        "deice-question",           // *Phrases.cs EventName table
        "log.warning",              // SessionLogEventFormatter.EventName
    ];

    private static readonly Lazy<JsonDocument> Catalog = new(() => JsonDocument.Parse(File.ReadAllText(CatalogPath())));
    private static readonly Lazy<List<JsonElement>> ProbeList = new(() => [.. Catalog.Value.RootElement.GetProperty("probes").EnumerateArray()]);
    private static readonly Lazy<HashSet<string>> Emitted = new(EmittedEventNames);

    [Fact]
    public void Ids_AreUniqueAndNonEmpty()
    {
        var ids = Probes().Select(p => Text(p, "id") ?? "").ToList();
        var duplicates = ids.GroupBy(id => id, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        Assert.True(ids.All(id => id.Length > 0), "every probe needs an id");
        Assert.True(duplicates.Count == 0, "duplicate probe ids: " + string.Join(", ", duplicates));
    }

    /// <summary>0 means "no issue number" (a feature shipped without one); null was coerced to 0
    /// by the reducer silently and read as a hole by everyone else.</summary>
    [Fact]
    public void Issue_IsANonNegativeInteger()
    {
        var offenders = Probes()
            .Where(p => !p.TryGetProperty("issue", out var issue) || issue.ValueKind != JsonValueKind.Number || !issue.TryGetInt32(out var n) || n < 0)
            .Select(Id)
            .ToList();

        Assert.True(offenders.Count == 0, "issue must be an integer (0 = no issue number): " + string.Join(", ", offenders));
    }

    [Fact]
    public void Kind_IsOneTheCatalogHeaderDefines()
    {
        var allowed = Catalog.Value.RootElement.GetProperty("kinds").EnumerateObject().Select(k => k.Name).ToHashSet(StringComparer.Ordinal);
        var offenders = Probes().Where(p => !allowed.Contains(Text(p, "kind") ?? "")).Select(p => $"{Id(p)} ({Text(p, "kind")})").ToList();

        Assert.True(offenders.Count == 0, "kind must be one of the header's 'kinds': " + string.Join(", ", offenders));
    }

    [Fact]
    public void State_IsOneTheWorkflowDefines()
    {
        var offenders = Probes().Where(p => !AllowedStates.Contains(Text(p, "state") ?? "")).Select(p => $"{Id(p)} ({Text(p, "state")})").ToList();

        Assert.True(offenders.Count == 0, "state must be open | fixed-unverified | closed-regression-watch: " + string.Join(", ", offenders));
    }

    /// <summary>Even a requires-human probe names where its evidence comes from (the header's
    /// <c>human</c> source) — an empty list reads as "nobody knows".</summary>
    [Fact]
    public void Sources_AreNonEmptyAndKnown()
    {
        var allowed = Catalog.Value.RootElement.GetProperty("sources").EnumerateObject().Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var offenders = new List<string>();
        foreach (var probe in Probes())
        {
            if (!probe.TryGetProperty("sources", out var sources) || sources.ValueKind != JsonValueKind.Array || sources.GetArrayLength() == 0)
            {
                offenders.Add($"{Id(probe)} (empty)");
                continue;
            }

            foreach (var source in sources.EnumerateArray())
            {
                if (!allowed.Contains(source.GetString() ?? ""))
                {
                    offenders.Add($"{Id(probe)} ({source})");
                }
            }
        }

        Assert.True(offenders.Count == 0, "sources must be non-empty and from the header's 'sources': " + string.Join(", ", offenders));
    }

    /// <summary>The shapes the header documents and ProbeEvaluator.Machine() reads. A signature
    /// block with its fields at the machine level (not under <c>signature</c>) is the silent
    /// failure this test exists for: the evaluator returned "untested — machine block has no
    /// signature" for 28 probes before 2026-11-03.</summary>
    [Fact]
    public void MachineBlocks_HaveTheDocumentedShape()
    {
        var offenders = new List<string>();
        foreach (var probe in Probes())
        {
            if (!probe.TryGetProperty("machine", out var machine) || machine.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var kind = Text(machine, "kind") ?? "signature";
            var problem = kind switch
            {
                "signature" when !machine.TryGetProperty("signature", out var s) || s.ValueKind != JsonValueKind.Object
                    => "signature kind needs a nested 'signature' object",
                "signature" when machine.TryGetProperty("type", out _) || machine.TryGetProperty("contains", out _) || machine.TryGetProperty("source", out _)
                    => "signature fields belong under 'signature', not at the machine level",
                "required-fields" when Text(machine, "type") is null
                    || !(machine.TryGetProperty("fields", out _) || machine.TryGetProperty("fieldsWhenPresent", out _))
                    => "required-fields needs 'type' and 'fields' or 'fieldsWhenPresent'",
                "duplicate-within" when Text(machine, "type") is null || Text(machine, "key") is null || !machine.TryGetProperty("windowSeconds", out _)
                    => "duplicate-within needs 'type', 'key' and 'windowSeconds'",
                "consecutive-duplicates" when Text(machine, "type") is null
                    => "consecutive-duplicates needs 'type'",
                _ when !MachineKinds.Contains(kind) => $"unknown machine kind '{kind}'",
                _ => null,
            };
            if (problem is not null)
            {
                offenders.Add($"{Id(probe)}: {problem}");
            }
        }

        Assert.True(offenders.Count == 0, string.Join("; ", offenders));
    }

    /// <summary>Every <c>type</c> inside a machine block — the block's own, its signature's, its
    /// trigger's — must be a session event name the code emits, or the probe can never bind
    /// (#227: <c>gsx.simbriefReload</c> for <c>gsx-simbrief-reload</c> passed forever).</summary>
    [Fact]
    public void MachineEventTypes_AreEmittedByTheCode()
    {
        var offenders = new List<string>();
        foreach (var probe in Probes())
        {
            if (!probe.TryGetProperty("machine", out var machine))
            {
                continue;
            }

            foreach (var (path, value) in StringProperties(machine, "type"))
            {
                if (!Emitted.Value.Contains(value))
                {
                    offenders.Add($"{Id(probe)} {path} = '{value}'");
                }
            }
        }

        Assert.True(offenders.Count == 0, "machine blocks name event types nothing emits: " + string.Join("; ", offenders));
    }

    [Fact]
    public void MachineRegexes_Compile()
    {
        var offenders = new List<string>();
        foreach (var probe in Probes())
        {
            if (!probe.TryGetProperty("machine", out var machine))
            {
                continue;
            }

            foreach (var name in RegexProperties)
            {
                foreach (var (path, value) in StringProperties(machine, name))
                {
                    try
                    {
                        _ = new Regex(value, RegexOptions.None, TimeSpan.FromSeconds(1));
                    }
                    catch (ArgumentException ex)
                    {
                        offenders.Add($"{Id(probe)} {path}: {ex.Message}");
                    }
                }
            }
        }

        Assert.True(offenders.Count == 0, "machine regexes must compile: " + string.Join("; ", offenders));
    }

    /// <summary>Guards the derivation itself: each emitter family has a canary the scan must
    /// find, so a refactor of an emitter (or of this scan) fails here with a clear name instead
    /// of as a wall of "unknown type" probes.</summary>
    [Fact]
    public void EmittedEventNames_CoverEveryEmitterFamily()
    {
        var missing = EmitterCanaries.Where(name => !Emitted.Value.Contains(name)).ToList();

        Assert.True(Emitted.Value.Count > 250, $"only {Emitted.Value.Count} emitted names derived — the source scan is broken");
        Assert.True(missing.Count == 0, "emitter families the scan no longer sees: " + string.Join(", ", missing));
    }

    // ---- the emitted-name derivation -------------------------------------------------------

    /// <summary>
    /// Every session event name the code can write, read from <c>src/**/*.cs</c>:
    /// <list type="bullet">
    /// <item>the first argument of every <c>eventLog.Record(…)</c> / <c>RecordEvent(…)</c> /
    /// <c>RecordEventEffect(…)</c> call — string literals anywhere in that expression (so a
    /// switch or ternary of literals counts) and identifiers resolved against every
    /// <c>const string</c> / <c>static readonly string</c> in the tree (<c>SessionHeader.StartedEvent</c>,
    /// <c>ClearedEvent</c>, …);</item>
    /// <item><c>"speech." + SpeechEventKind</c> (SpeechArbiterService.Raise), by reflection;</item>
    /// <item>the captain's-question tables: <c>EventName: "…"</c> in <c>*Phrases.cs</c>
    /// (CaptainsQuestionDialogue records <c>table.EventName</c>);</item>
    /// <item><c>log.warning/error/fatal</c> from SessionLogEventFormatter.EventName's switch.</item>
    /// </list>
    /// Receivers other than the event log (<c>_decisions.Record("force next service", …)</c>,
    /// latency metrics, the radio tuner) are not event emitters and are skipped on purpose.
    /// </summary>
    private static HashSet<string> EmittedEventNames()
    {
        var sources = ProductionSources().ToDictionary(file => file, File.ReadAllText, StringComparer.OrdinalIgnoreCase);

        var constants = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var constantPattern = new Regex(@"\b(?:const|static\s+readonly)\s+string\s+(\w+)\s*=\s*""([^""]*)""\s*;", RegexOptions.Compiled);
        foreach (var text in sources.Values)
        {
            foreach (Match match in constantPattern.Matches(text))
            {
                if (!constants.TryGetValue(match.Groups[1].Value, out var values))
                {
                    constants[match.Groups[1].Value] = values = new HashSet<string>(StringComparer.Ordinal);
                }

                values.Add(match.Groups[2].Value);
            }
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        var callPattern = new Regex(@"(?:\b_?eventLog\??\.Record|\bRecordEvent(?:Effect)?)\s*\(", RegexOptions.Compiled);
        var literalPattern = new Regex(@"""([^""\\]*)""", RegexOptions.Compiled);
        var identifierPattern = new Regex(@"\b([A-Za-z_]\w*)\b", RegexOptions.Compiled);
        foreach (var text in sources.Values)
        {
            foreach (Match call in callPattern.Matches(text))
            {
                var argument = FirstArgument(text, call.Index + call.Length);
                foreach (Match literal in literalPattern.Matches(argument))
                {
                    names.Add(literal.Groups[1].Value);
                }

                foreach (Match identifier in identifierPattern.Matches(argument))
                {
                    if (constants.TryGetValue(identifier.Groups[1].Value, out var values))
                    {
                        names.UnionWith(values);
                    }
                }
            }
        }

        foreach (var kind in Enum.GetNames<SpeechEventKind>())
        {
            names.Add("speech." + kind.ToLowerInvariant());
        }

        var tablePattern = new Regex(@"\bEventName\s*[:=]\s*""([^""]+)""", RegexOptions.Compiled);
        foreach (var table in sources.Where(pair => pair.Key.EndsWith("Phrases.cs", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (Match match in tablePattern.Matches(table.Value))
            {
                names.Add(match.Groups[1].Value);
            }
        }

        var formatter = sources.Single(pair => pair.Key.EndsWith("SessionLogEventFormatter.cs", StringComparison.OrdinalIgnoreCase)).Value;
        foreach (Match match in Regex.Matches(formatter, @"LogEventLevel\.\w+\s*=>\s*""([^""]+)"""))
        {
            names.Add(match.Groups[1].Value);
        }

        // "speech." itself (the prefix literal) and empty strings are artefacts of the scan.
        names.RemoveWhere(name => name.Length == 0 || name.EndsWith('.'));
        return names;
    }

    /// <summary>The text of the first argument starting at <paramref name="start"/>: up to the
    /// first comma or closing parenthesis at nesting depth zero, strings skipped — so a switch
    /// expression or an object initialiser as the first argument is taken whole.</summary>
    private static string FirstArgument(string text, int start)
    {
        var depth = 0;
        var inString = false;
        var i = start;
        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '(' or '{' or '[':
                    depth++;
                    break;
                case ')' or '}' or ']':
                    if (depth == 0)
                    {
                        return text[start..i];
                    }

                    depth--;
                    break;
                case ',' when depth == 0:
                    return text[start..i];
            }
        }

        return text[start..];
    }

    // ---- helpers ------------------------------------------------------------------------

    private static List<JsonElement> Probes() => ProbeList.Value;

    private static string Id(JsonElement probe) => Text(probe, "id") ?? "(no id)";

    private static string? Text(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Every string-valued property called <paramref name="name"/> anywhere under
    /// <paramref name="element"/>, with its dotted path.</summary>
    private static IEnumerable<(string Path, string Value)> StringProperties(JsonElement element, string name, string prefix = "machine")
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var property in element.EnumerateObject())
        {
            var path = $"{prefix}.{property.Name}";
            if (property.Name == name && property.Value.ValueKind == JsonValueKind.String)
            {
                yield return (path, property.Value.GetString()!);
            }

            foreach (var nested in StringProperties(property.Value, name, path))
            {
                yield return nested;
            }
        }
    }

    private static string CatalogPath() => Path.Combine(RepoRoot(), "docs", "agents", "verification-probes.json");

    private static IEnumerable<string> ProductionSources()
    {
        var srcDir = Path.Combine(RepoRoot(), "src");
        return Directory
            .EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
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
