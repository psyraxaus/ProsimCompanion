using System.Globalization;
using System.Reflection;
using System.Text.Json;
using ProsimCompanion.Core.Logging;

namespace ProsimCompanion.Reduce;

/// <summary>A literal signature: which stream, an optional session event type, a substring,
/// an optional component (log stream) and how many hits count as "present".</summary>
public sealed record ProbeSignature(string Source, string? Type, string Contains, string? Component, int MinCount);

/// <summary>The optional <c>machine</c> block of a probe. Probes without one go to forLlm.</summary>
public sealed record ProbeMachine(
    string Kind,
    ProbeSignature? Signature,
    ProbeSignature? Trigger,
    string? Type,
    IReadOnlyList<string>? Fields,
    string? Key,
    double? WindowSeconds,
    IReadOnlyDictionary<string, string>? Where = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? FieldsWhenPresent = null);

public sealed record ProbeDefinition(
    string Id,
    int Issue,
    string State,
    string Kind,
    IReadOnlyList<string> Sources,
    IReadOnlyList<string> Checks,
    string? Notes,
    ProbeMachine? Machine);

/// <summary>
/// Loads the probe catalog (embedded copy or <c>--probes</c> override) and evaluates every
/// probe that carries a <c>machine</c> block deterministically; the rest are passed through
/// for the LLM step with their text untouched. Verdicts follow the catalog's own semantics:
/// assert-absent passes when the signature is absent; assert-present is <c>untested</c> until
/// its trigger occurred, then pass/fail on the behaviour. Evidence quotes the matching lines.
/// </summary>
public static class ProbeEvaluator
{
    public const int MaxEvidence = 5;

    /// <summary>The catalog shipped inside this build.</summary>
    public static string EmbeddedCatalogJson()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("verification-probes.json")
            ?? throw new InvalidOperationException("The embedded probe catalog is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static IReadOnlyList<ProbeDefinition> LoadCatalog(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var doc = JsonDocument.Parse(json);
        var probes = new List<ProbeDefinition>();
        if (!doc.RootElement.TryGetProperty("probes", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return probes;
        }

        foreach (var p in array.EnumerateArray())
        {
            probes.Add(new ProbeDefinition(
                Str(p, "id") ?? "",
                p.TryGetProperty("issue", out var issue) && issue.ValueKind == JsonValueKind.Number ? issue.GetInt32() : 0,
                Str(p, "state") ?? "",
                Str(p, "kind") ?? "",
                Strings(p, "sources"),
                Strings(p, "checks"),
                Str(p, "notes"),
                p.TryGetProperty("machine", out var m) && m.ValueKind == JsonValueKind.Object ? Machine(m) : null));
        }

        return probes;
    }

    private static ProbeMachine Machine(JsonElement m) => new(
        Str(m, "kind") ?? "signature",
        m.TryGetProperty("signature", out var s) && s.ValueKind == JsonValueKind.Object ? Signature(s) : null,
        m.TryGetProperty("trigger", out var t) && t.ValueKind == JsonValueKind.Object ? Signature(t) : null,
        Str(m, "type"),
        m.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Array ? Strings(m, "fields") : null,
        Str(m, "key"),
        m.TryGetProperty("windowSeconds", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetDouble() : null,
        ReadWhere(m),
        ReadFieldsWhenPresent(m));

    private static ProbeSignature Signature(JsonElement s) => new(
        Str(s, "source") ?? "session",
        Str(s, "type"),
        Str(s, "contains") ?? "",
        Str(s, "component"),
        s.TryGetProperty("minCount", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 1);

    /// <summary>Evaluates the catalog over the whole bundle.</summary>
    public static (IReadOnlyDictionary<string, ProbeResult> Results, IReadOnlyList<ProbeForLlm> ForLlm) Evaluate(
        IReadOnlyList<ProbeDefinition> catalog,
        IReadOnlyList<LoadedSession> sessions,
        IReadOnlyList<CmTraceEntry> cmTrace)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(cmTrace);

        var results = new Dictionary<string, ProbeResult>(StringComparer.Ordinal);
        var forLlm = new List<ProbeForLlm>();
        foreach (var probe in catalog)
        {
            if (probe.Machine is null || probe.Kind is not ("assert-absent" or "assert-present"))
            {
                forLlm.Add(new ProbeForLlm(probe.Id, probe.Issue, probe.State, probe.Kind, probe.Sources, probe.Checks, probe.Notes));
                continue;
            }

            var (verdict, evidence) = probe.Machine.Kind switch
            {
                "signature" => EvaluateSignature(probe, sessions, cmTrace),
                "required-fields" => EvaluateRequiredFields(probe.Machine, sessions),
                "duplicate-within" => EvaluateDuplicateWithin(probe.Machine, sessions),
                "consecutive-duplicates" => EvaluateConsecutiveDuplicates(probe.Machine, sessions),
                _ => ("untested", (IReadOnlyList<string>)[$"unknown machine kind '{probe.Machine.Kind}'"]),
            };
            results[probe.Id] = new ProbeResult(probe.Kind, probe.Issue, probe.State, verdict, evidence);
        }

        return (results, forLlm);
    }

    private static (string, IReadOnlyList<string>) EvaluateSignature(
        ProbeDefinition probe, IReadOnlyList<LoadedSession> sessions, IReadOnlyList<CmTraceEntry> cmTrace)
    {
        var machine = probe.Machine!;
        if (machine.Signature is null)
        {
            return ("untested", ["machine block has no signature"]);
        }

        var hits = Find(machine.Signature, sessions, cmTrace);
        var present = hits.Count >= Math.Max(1, machine.Signature.MinCount);
        if (probe.Kind == "assert-absent")
        {
            return present
                ? ("fail", Quote(hits))
                : ("pass", [$"signature '{machine.Signature.Contains}' absent ({hits.Count} hit(s), threshold {machine.Signature.MinCount})"]);
        }

        // assert-present
        if (machine.Trigger is not null)
        {
            var triggers = Find(machine.Trigger, sessions, cmTrace);
            if (triggers.Count < Math.Max(1, machine.Trigger.MinCount))
            {
                return ("untested", [$"trigger '{machine.Trigger.Contains}' never occurred"]);
            }
        }

        return present
            ? ("pass", Quote(hits))
            : ("fail", [$"trigger occurred but behaviour '{machine.Signature.Contains}' was not observed"]);
    }

    /// <summary><c>fields</c> must be on every event of the type. <c>where</c> (path → text)
    /// keeps only the events whose payload reads that text there — an event type shared by
    /// several features (fo.answer: only mode "geo" carries factSource). <c>fieldsWhenPresent</c>
    /// (path → fields) asks for fields only on the events that carry the path — an event that
    /// fills in edge by edge (flight-times: takeoffFobKg only once takeoffUtc is stamped).
    /// Ticket t-20261004-2147: both probes failed on events they were never about.</summary>
    private static (string, IReadOnlyList<string>) EvaluateRequiredFields(ProbeMachine machine, IReadOnlyList<LoadedSession> sessions)
    {
        var always = machine.Fields ?? [];
        var conditional = machine.FieldsWhenPresent ?? new Dictionary<string, IReadOnlyList<string>>();
        if (machine.Type is null || (always.Count == 0 && conditional.Count == 0))
        {
            return ("untested", ["machine block needs type and fields (or fieldsWhenPresent)"]);
        }

        var filter = machine.Where is { Count: > 0 } conditions
            ? $" where {string.Join(", ", conditions.Select(pair => $"{pair.Key}={pair.Value}"))}"
            : "";
        var seen = 0;
        var evidence = new List<string>();
        foreach (var session in sessions)
        {
            foreach (var line in session.Lines.Where(l => l.Type == machine.Type))
            {
                if (machine.Where is not null
                    && machine.Where.Any(pair => !string.Equals(PathText(line.Payload, pair.Key), pair.Value, StringComparison.Ordinal)))
                {
                    continue;
                }

                seen++;
                var missing = always
                    .Concat(conditional.Where(pair => HasPath(line.Payload, pair.Key)).SelectMany(pair => pair.Value))
                    .Where(path => !HasPath(line.Payload, path))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (missing.Count > 0 && evidence.Count < MaxEvidence)
                {
                    evidence.Add($"{session.File} {Stamp(line.At)} {machine.Type} missing {string.Join(", ", missing)}");
                }
            }
        }

        if (seen == 0)
        {
            return ("untested", [$"no '{machine.Type}' event{filter} in the bundle"]);
        }

        return evidence.Count > 0 ? ("fail", evidence) : ("pass", [$"{seen} '{machine.Type}' event(s){filter} carry every required field"]);
    }

    private static (string, IReadOnlyList<string>) EvaluateDuplicateWithin(ProbeMachine machine, IReadOnlyList<LoadedSession> sessions)
    {
        if (machine.Type is null || machine.Key is null || machine.WindowSeconds is null)
        {
            return ("untested", ["machine block needs type, key and windowSeconds"]);
        }

        var evidence = new List<string>();
        var seen = 0;
        foreach (var session in sessions)
        {
            var lastByKey = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            foreach (var line in session.Timed.Where(l => l.Type == machine.Type))
            {
                seen++;
                var key = Str(line.Payload, machine.Key);
                if (key is null)
                {
                    continue;
                }

                if (lastByKey.TryGetValue(key, out var previous)
                    && (line.At!.Value - previous).TotalSeconds <= machine.WindowSeconds
                    && evidence.Count < MaxEvidence)
                {
                    evidence.Add($"{session.File} {Stamp(line.At)} {machine.Type} '{key}' repeated {(line.At.Value - previous).TotalSeconds:0.0} s after the previous one");
                }

                lastByKey[key] = line.At!.Value;
            }
        }

        if (seen == 0)
        {
            return ("untested", [$"no '{machine.Type}' event in the bundle"]);
        }

        return evidence.Count > 0 ? ("fail", evidence) : ("pass", [$"no '{machine.Type}' repeated within {machine.WindowSeconds} s"]);
    }

    private static (string, IReadOnlyList<string>) EvaluateConsecutiveDuplicates(ProbeMachine machine, IReadOnlyList<LoadedSession> sessions)
    {
        if (machine.Type is null)
        {
            return ("untested", ["machine block needs type"]);
        }

        var evidence = new List<string>();
        var seen = 0;
        foreach (var session in sessions)
        {
            string? previous = null;
            foreach (var line in session.Lines.Where(l => l.Type == machine.Type))
            {
                seen++;
                var payload = line.Payload.ValueKind == JsonValueKind.Undefined ? "" : line.Payload.GetRawText();
                if (previous is not null && string.Equals(previous, payload, StringComparison.Ordinal) && evidence.Count < MaxEvidence)
                {
                    evidence.Add($"{session.File} {Stamp(line.At)} {machine.Type} repeated the previous payload {payload}");
                }

                previous = payload;
            }
        }

        if (seen == 0)
        {
            return ("untested", [$"no '{machine.Type}' event in the bundle"]);
        }

        return evidence.Count > 0 ? ("fail", evidence) : ("pass", [$"{seen} '{machine.Type}' event(s), none identical to its predecessor"]);
    }

    /// <summary>All hits for a signature. Log signatures read the CMTrace entries when the
    /// bundle has any, else the mirrored log.* session events — never both, so counts are
    /// not doubled.</summary>
    private static List<string> Find(ProbeSignature signature, IReadOnlyList<LoadedSession> sessions, IReadOnlyList<CmTraceEntry> cmTrace)
    {
        var hits = new List<string>();
        if (signature.Source == "log")
        {
            if (cmTrace.Count > 0)
            {
                foreach (var entry in cmTrace)
                {
                    if (entry.Message.Contains(signature.Contains, StringComparison.Ordinal)
                        && (signature.Component is null || entry.Component.Equals(signature.Component, StringComparison.OrdinalIgnoreCase)))
                    {
                        hits.Add($"log {Stamp(entry.Timestamp)} [{entry.Component}] {FirstLine(entry.Message)}");
                    }
                }
            }
            else
            {
                foreach (var session in sessions)
                {
                    foreach (var line in session.Lines.Where(l => l.Type.StartsWith("log.", StringComparison.Ordinal)))
                    {
                        var message = Str(line.Payload, "message") ?? "";
                        var component = Str(line.Payload, "component") ?? "";
                        if (message.Contains(signature.Contains, StringComparison.Ordinal)
                            && (signature.Component is null || component.Equals(signature.Component, StringComparison.OrdinalIgnoreCase)))
                        {
                            hits.Add($"{session.File} {Stamp(line.At)} {line.Type} [{component}] {FirstLine(message)}");
                        }
                    }
                }
            }

            return hits;
        }

        foreach (var session in sessions)
        {
            foreach (var line in session.Lines)
            {
                if ((signature.Type is null || line.Type == signature.Type)
                    && line.Raw.Contains(signature.Contains, StringComparison.Ordinal))
                {
                    hits.Add($"{session.File} {Stamp(line.At)} {Truncate(line.Raw, 240)}");
                }
            }
        }

        return hits;
    }

    private static List<string> Quote(List<string> hits)
        => hits.Count <= MaxEvidence ? hits : [.. hits.Take(MaxEvidence), $"… {hits.Count - MaxEvidence} more"];

    private static bool HasPath(JsonElement payload, string path)
        => TryPath(payload, path, out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;

    /// <summary>The value at a dotted path as text (strings as they are, anything else as its
    /// JSON text); null when the path is absent.</summary>
    private static string? PathText(JsonElement payload, string path)
        => !TryPath(payload, path, out var value) ? null
            : value.ValueKind == JsonValueKind.String ? value.GetString()
            : value.GetRawText();

    private static bool TryPath(JsonElement payload, string path, out JsonElement value)
    {
        value = payload;
        foreach (var segment in path.Split('.'))
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
            {
                return false;
            }
        }

        return true;
    }

    private static Dictionary<string, string>? ReadWhere(JsonElement machine)
    {
        if (!machine.TryGetProperty("where", out var conditions) || conditions.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var filter = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in conditions.EnumerateObject())
        {
            filter[property.Name] = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()!
                : property.Value.GetRawText();
        }

        return filter;
    }

    private static Dictionary<string, IReadOnlyList<string>>? ReadFieldsWhenPresent(JsonElement machine)
    {
        if (!machine.TryGetProperty("fieldsWhenPresent", out var map) || map.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var fields = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var property in map.EnumerateObject())
        {
            fields[property.Name] = Strings(map, property.Name);
        }

        return fields;
    }

    private static string Stamp(DateTimeOffset? at)
        => at?.ToString("yyyy-MM-ddTHH:mm:ss.fffK", CultureInfo.InvariantCulture) ?? "(no timestamp)";

    private static string FirstLine(string text) => Truncate(text.Split('\n', 2)[0].TrimEnd('\r'), 240);

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private static string? Str(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static IReadOnlyList<string> Strings(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Array
            ? [.. el.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)]
            : [];
}
