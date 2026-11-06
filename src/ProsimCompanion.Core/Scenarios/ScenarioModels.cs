using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Scenarios;

/// <summary>How serious a catalogue entry is (issue #197).</summary>
public enum ScenarioSeverity
{
    Minor,
    Significant,

    /// <summary>Present in the catalogue, drawn only with <c>scenario.allowMajor</c>.</summary>
    Major,
}

/// <summary>One entry of <c>scenarios.json</c>.</summary>
/// <param name="AbnormalId">The ECAM abnormal definition the FO responds with — must exist in <c>config/abnormals</c>.</param>
/// <param name="FailureName">The ProSim SDK failure name (<c>ProSimConnect.getFailures()</c>) the gate arms.</param>
/// <param name="Verified">The name was confirmed against the live list on a rig; unverified names are never armed.</param>
/// <param name="Phases">The flight phases the failure may fire in.</param>
/// <param name="Takeoff">A takeoff scenario (RTO cause / EFATO): armed on the ground on the SDK's own after-event condition.</param>
/// <param name="AfterEvent">The SDK condition a takeoff scenario arms on (<c>afterEvent</c>: <c>takeoff</c> = an RTO
/// cause on the roll, <c>v1</c> = an engine failure after V1, needs the EFATO feature); null for a non-takeoff entry.
/// The catalogue says which it is — never the spelling of the id (review 2026-11-05).</param>
/// <param name="ClearAfterSeconds">A transient the app resets itself after this long; null = stays until cleared.</param>
/// <param name="TechLogAfter">The title the post-abnormal tech-log offer uses; null = the abnormal's title.</param>
/// <param name="Decision">The FO's decision-turn template (<c>{alternate}</c>, <c>{destination}</c>); the floor under the model.</param>
/// <param name="WearTitle">A tech-log wear item this scenario may "age" from; null = none.</param>
public sealed record ScenarioEntry(
    string Id,
    string Title,
    string AbnormalId,
    string FailureName,
    bool Verified,
    IReadOnlyList<FlightPhase> Phases,
    double Weight,
    ScenarioSeverity Severity,
    bool Takeoff,
    FailureAfterEvent? AfterEvent,
    int? ClearAfterSeconds,
    string? TechLogAfter,
    string Decision,
    string? WearTitle);

/// <summary>Pure parsing of <c>scenarios.json</c>: an object with a <c>scenarios</c> array. An
/// entry without an id, an abnormal id or a failure name is skipped and named; phases are the
/// <see cref="FlightPhase"/> names (unknown ones dropped, none = the entry never fires).</summary>
public static class ScenarioCatalogueFile
{
    public sealed record ParseResult(IReadOnlyList<ScenarioEntry> Scenarios, IReadOnlyList<string> Problems);

    public static ParseResult Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("scenarios", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return new([], ["no \"scenarios\" array"]);
        }

        var entries = new List<ScenarioEntry>();
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            index++;
            if (item.ValueKind != JsonValueKind.Object)
            {
                problems.Add($"entry {index}: not an object");
                continue;
            }

            var id = Text(item, "id");
            var abnormal = Text(item, "abnormalId");
            var failure = Text(item, "failureName");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(abnormal) || string.IsNullOrWhiteSpace(failure))
            {
                problems.Add($"entry {index}: id, abnormalId and failureName are required");
                continue;
            }

            if (!seen.Add(id))
            {
                problems.Add($"entry {index}: duplicate id \"{id}\"");
                continue;
            }

            var phases = new List<FlightPhase>();
            if (item.TryGetProperty("phases", out var phaseArray) && phaseArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var phase in phaseArray.EnumerateArray())
                {
                    if (phase.ValueKind == JsonValueKind.String && Enum.TryParse<FlightPhase>(phase.GetString(), true, out var parsed))
                    {
                        phases.Add(parsed);
                    }
                    else
                    {
                        problems.Add($"entry {index} ({id}): unknown phase \"{phase}\"");
                    }
                }
            }

            var severity = (Text(item, "severity") ?? "minor").Trim().ToLowerInvariant() switch
            {
                "significant" => ScenarioSeverity.Significant,
                "major" => ScenarioSeverity.Major,
                _ => ScenarioSeverity.Minor,
            };
            var takeoff = item.TryGetProperty("takeoff", out var t) && t.ValueKind == JsonValueKind.True;
            FailureAfterEvent? afterEvent = null;
            if (takeoff)
            {
                // The field, not the id, decides the SDK condition: "efato" in an id used to be
                // the switch, and a renamed entry silently became an RTO cause.
                var afterText = (Text(item, "afterEvent") ?? "takeoff").Trim().ToLowerInvariant();
                afterEvent = afterText switch
                {
                    "takeoff" => FailureAfterEvent.Takeoff,
                    "v1" => FailureAfterEvent.V1,
                    "landing" => FailureAfterEvent.Landing,
                    _ => null,
                };
                if (afterEvent is null)
                {
                    problems.Add($"entry {index} ({id}): unknown afterEvent \"{afterText}\" (takeoff | v1 | landing) — takeoff assumed");
                    afterEvent = FailureAfterEvent.Takeoff;
                }
            }

            entries.Add(new ScenarioEntry(
                id.Trim(),
                Text(item, "title")?.Trim() ?? id.Trim(),
                abnormal.Trim(),
                failure.Trim(),
                item.TryGetProperty("verified", out var v) && v.ValueKind == JsonValueKind.True,
                phases,
                item.TryGetProperty("weight", out var w) && w.ValueKind == JsonValueKind.Number && w.TryGetDouble(out var wd) ? Math.Max(0, wd) : 1.0,
                severity,
                takeoff,
                afterEvent,
                item.TryGetProperty("clearAfterSeconds", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var cs) && cs > 0 ? cs : null,
                NullIfBlank(Text(item, "techLogAfter")),
                Text(item, "decision")?.Trim() ?? "",
                NullIfBlank(Text(item, "wearTitle"))));
        }

        return new(entries, problems);
    }

    private static string? Text(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Where a planned scenario stands. Only <see cref="Planned"/> survives a restart.</summary>
public enum ScenarioPlanState
{
    Planned,
    Armed,
    Fired,
    Cleared,

    /// <summary>ProSim refused the arm, or the armed failure never verified: the plan is spent
    /// for this leg. Without this state the tick re-queued the arm every second for the rest of
    /// the phase (review 2026-11-05 — the "re-arm storm").</summary>
    Failed,
}

/// <summary>The one scenario of the current flight cycle, persisted in <c>scenario-state.json</c>.</summary>
public sealed class ScenarioPlan
{
    public string ScenarioId { get; set; } = "";

    public string Title { get; set; } = "";

    public string AbnormalId { get; set; } = "";

    public string FailureName { get; set; } = "";

    /// <summary>The phase the failure fires in.</summary>
    public FlightPhase Phase { get; set; }

    /// <summary>Seconds into that phase before it fires.</summary>
    public int FireAfterSeconds { get; set; }

    public bool Takeoff { get; set; }

    /// <summary>The SDK after-event condition of a takeoff plan (from the catalogue entry); null otherwise.</summary>
    public FailureAfterEvent? AfterEvent { get; set; }

    public int? ClearAfterSeconds { get; set; }

    public string? TechLogAfter { get; set; }

    public string Decision { get; set; } = "";

    public DateTimeOffset PlannedAtUtc { get; set; }

    public ScenarioPlanState State { get; set; } = ScenarioPlanState.Planned;

    /// <summary>The injector's handle once armed; null before, and after a restart (the SDK
    /// forbids reusing a handle across sessions — the injector re-reads what is armed).</summary>
    public string? Handle { get; set; }

    public DateTimeOffset? ArmedAtUtc { get; set; }

    public DateTimeOffset? FiredAtUtc { get; set; }

    public DateTimeOffset? ClearedAtUtc { get; set; }

    /// <summary>The session id the plan belongs to (a plan from another session is stale).</summary>
    public string? SessionId { get; set; }
}

/// <summary>What the state file held: the plan if it may continue, and whether a plan was
/// Armed or Fired at the last exit (a crash) — the caller then clears all once.</summary>
public sealed record ScenarioStateLoad(ScenarioPlan? Plan, bool LeftArmed);

/// <summary>
/// Persistence for <c>scenario-state.json</c> (issue #197, the <c>fo-roster-state.json</c>
/// pattern): atomic writes, never a throw. On load only a <see cref="ScenarioPlanState.Planned"/>
/// plan survives — an Armed or Fired plan from a crashed run is reported so the caller runs a
/// clear-all, and the plan is dropped. A replay or a startup resync therefore never finds a
/// plan to re-arm: nothing but Planned is ever read back, and Planned is re-judged before it fires.
/// </summary>
public sealed class ScenarioStateFile
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _path;
    private readonly ILogger<ScenarioStateFile> _logger;
    private readonly object _io = new();

    public ScenarioStateFile(ILogger<ScenarioStateFile> logger, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _path = string.IsNullOrWhiteSpace(path) ? Path.Combine(UserDataPaths.Root, "scenario-state.json") : path;
    }

    public string FilePath => _path;

    public ScenarioStateLoad Load()
    {
        try
        {
            lock (_io)
            {
                if (!File.Exists(_path))
                {
                    return new(null, false);
                }

                var json = File.ReadAllText(_path);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return new(null, false);
                }

                var plan = JsonSerializer.Deserialize<ScenarioPlan>(json, JsonOptions);
                if (plan is null)
                {
                    return new(null, false);
                }

                return plan.State switch
                {
                    ScenarioPlanState.Planned => new(plan, false),
                    ScenarioPlanState.Armed or ScenarioPlanState.Fired => new(null, true),
                    _ => new(null, false),
                };
            }
        }
        catch (Exception ex)
        {
            var backup = _path + $".corrupt-{DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.bak";
            try
            {
                File.Move(_path, backup, overwrite: true);
            }
            catch (Exception moveEx)
            {
                _logger.LogDebug(moveEx, "Could not move the corrupt scenario state aside");
            }

            _logger.LogWarning(ex, "Scenario state corrupt — backed up to {Backup}; starting without a plan", backup);
            return new(null, false);
        }
    }

    public void Save(ScenarioPlan? plan)
    {
        var tmp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            lock (_io)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                if (plan is null)
                {
                    if (File.Exists(_path))
                    {
                        File.Delete(_path);
                    }

                    return;
                }

                File.WriteAllText(tmp, JsonSerializer.Serialize(plan, JsonOptions));
                File.Move(tmp, _path, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write the scenario state {Path}", _path);
            try
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
            catch (Exception deleteEx)
            {
                _logger.LogDebug(deleteEx, "Could not delete {Path}", tmp);
            }
        }
    }
}

/// <summary>What the STATUS page and the API read (issue #197).</summary>
/// <param name="State">"off" | "idle" | "planned" | "armed" | "fired" | "cleared" | "failed".</param>
/// <param name="PlannedTitle">The planned / armed scenario's title — null unless <c>scenario.showPlanned</c> or once fired.</param>
/// <param name="FailureName">The armed failure's SDK name, once armed and shown.</param>
/// <param name="LastClear">The last clear's outcome line ("cleared 1", "could not clear — check the IOS").</param>
/// <param name="LiveFailures">ProSim's own failure list as last read (for verifying catalogue names).</param>
/// <param name="InjectorAvailable">The ProSim SDK failure API is reachable.</param>
public sealed record ScenarioSnapshot(
    string State,
    string? PlannedTitle,
    string? FailureName,
    string? LastClear,
    IReadOnlyList<string> LiveFailures,
    bool InjectorAvailable)
{
    public static ScenarioSnapshot Empty { get; } = new("off", null, null, null, [], false);
}

/// <summary>Live scenario state for the STATUS page and the API; written by the Speech-side service.</summary>
public sealed class ScenarioStore : SnapshotStore<ScenarioSnapshot>
{
    public ScenarioStore()
        : base(ScenarioSnapshot.Empty)
    {
    }
}
