using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ProsimCompanion.Core.Debrief;

/// <summary>Extracts <see cref="DebriefFacts"/> from a recorded session's JSONL event log.</summary>
public interface IDebriefFactExtractor
{
    DebriefFacts Extract(string sessionPath);
}

/// <summary>
/// Single-pass deterministic reader over the session JSONL (envelope fields: <c>timestamp</c>,
/// <c>type</c>, <c>payload</c>). Opens with FileShare.ReadWrite so it can read while the
/// event-log writer still holds the file; unparseable lines are skipped; it never throws —
/// any failure yields <see cref="DebriefFacts.Empty"/> with a warning.
///
/// <para>Event-name map (this codebase's actual <c>JsonlEventLog.Record</c> call sites — NOT
/// Prosim2FO's names):</para>
/// <list type="bullet">
/// <item><c>phase-changed</c> { previous, current, snapshot { iasKt, groundSpeedKt } } —
/// PascalCase phase names. Off-block = first entry into PushbackAndStart/TaxiOut; lift-off =
/// first TakeoffRoll→InitialClimb (snapshot iasKt); touchdown = first entry into
/// LandingRollout (snapshot groundSpeedKt); on-block = FIRST entry into Shutdown (a null
/// guard — Prosim2FO's last-wins bug inflated block time when Shutdown re-entered).</item>
/// <item><c>approach.gate</c> { gate, aglFt, result, criteria[{name, result}] } — one
/// <see cref="GateFact"/> each, with the first criterion whose result is "Fail".</item>
/// <item><c>checklist.voice</c> { name, phase } — a completion when phase == "end"
/// (distinct by name).</item>
/// <item><c>callout.fired</c> / <c>speech.suppressed</c> / <c>callout.degraded</c> — counts
/// (the arbiter records <c>speech.*</c> from its lifecycle events).</item>
/// <item><c>flow.advisory</c> { id, spoken } — distinct ids where spoken != false.</item>
/// <item><c>cabin.report</c> — count.</item>
/// <item><c>radio.set</c> / <c>radio.swapped</c> — radio tunes handled.</item>
/// <item><c>drill.completed</c> { id } — memory-item drills spoken (voice rehearsals are
/// indistinguishable from real detections in the current payload, so all count).</item>
/// <item><c>techlog.raised</c> / <c>techlog.rectified</c> — counts;
/// <c>techlog.carried</c> / <c>techlog.briefed</c> { open } — defects carried (max).</item>
/// <item><c>failure.detected</c> { id, title } / <c>failure.cleared</c> { id } — abnormals
/// (first title per id wins; cleared when a later cleared event names the id).</item>
/// <item><c>flight.route</c> { role, airport, runway } — origin/destination + runways
/// (emitted by the briefing service when a briefing resolves).</item>
/// <item><c>fuel.check</c> { fobKg } — first/last/used. No producer exists in this codebase
/// yet; the mapping is kept so the fuel lines light up when one arrives.</item>
/// </list>
/// </summary>
public sealed class DebriefFactExtractor : IDebriefFactExtractor
{
    private readonly ILogger<DebriefFactExtractor> _logger;

    public DebriefFactExtractor(ILogger<DebriefFactExtractor> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public DebriefFacts Extract(string sessionPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sessionPath) || !File.Exists(sessionPath))
            {
                return DebriefFacts.Empty;
            }

            DateTimeOffset? offBlock = null, onBlock = null, liftoff = null, touchdown = null;
            double? liftoffIas = null, touchdownGs = null;
            var gates = new List<GateFact>();
            int callouts = 0, suppressed = 0, degradations = 0, cabinReports = 0;
            int defectsRaised = 0, defectsRectified = 0, defectsCarried = 0;
            int radioTunes = 0, memoryDrills = 0;
            var checklists = new List<string>();
            var fobs = new List<double>();
            var advisories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? origin = null, destination = null, depRunway = null, arrRunway = null;

            // failure id → (title, cleared); insertion-ordered so the debrief reads them in
            // the order they happened.
            var abnormals = new Dictionary<string, (string Title, bool Cleared)>(StringComparer.OrdinalIgnoreCase);
            var abnormalOrder = new List<string>();

            // ReadWrite share: the app's event-log writer still has the file open for append.
            using var stream = new FileStream(sessionPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    continue; // a torn/partial trailing line is expected while the writer is live
                }

                using (doc)
                {
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("type", out var typeEl))
                    {
                        continue;
                    }

                    var type = typeEl.GetString();
                    var payload = root.TryGetProperty("payload", out var p) ? p : default;
                    DateTimeOffset? ts = root.TryGetProperty("timestamp", out var tsEl)
                        && tsEl.ValueKind == JsonValueKind.String
                        && DateTimeOffset.TryParse(tsEl.GetString(), out var parsed)
                            ? parsed
                            : null;

                    switch (type)
                    {
                        case "phase-changed":
                        {
                            var to = Str(payload, "current");
                            var from = Str(payload, "previous");
                            if (ts is { } tv)
                            {
                                if (offBlock is null && (Is(to, "PushbackAndStart") || Is(to, "TaxiOut")))
                                {
                                    offBlock = tv;
                                }

                                if (liftoff is null && Is(from, "TakeoffRoll") && Is(to, "InitialClimb"))
                                {
                                    liftoff = tv;
                                    liftoffIas = Snap(payload, "iasKt");
                                }

                                if (touchdown is null && Is(to, "LandingRollout"))
                                {
                                    touchdown = tv;
                                    touchdownGs = Snap(payload, "groundSpeedKt");
                                }

                                if (onBlock is null && Is(to, "Shutdown"))
                                {
                                    onBlock = tv; // first-wins: re-entering Shutdown must not stretch block time
                                }
                            }

                            break;
                        }

                        case "approach.gate":
                        {
                            var name = Str(payload, "gate") ?? "?";
                            var agl = Num(payload, "aglFt") ?? 0;
                            var result = Str(payload, "result") ?? "?";
                            gates.Add(new GateFact(name, agl, result, FirstFailingCriterion(payload)));
                            break;
                        }

                        case "checklist.voice":
                        {
                            if (Is(Str(payload, "phase"), "end"))
                            {
                                var name = Str(payload, "name");
                                if (!string.IsNullOrWhiteSpace(name)
                                    && !checklists.Contains(name, StringComparer.OrdinalIgnoreCase))
                                {
                                    checklists.Add(name);
                                }
                            }

                            break;
                        }

                        case "callout.fired":
                            callouts++;
                            break;

                        case "speech.suppressed":
                            suppressed++;
                            break;

                        case "callout.degraded":
                            degradations++;
                            break;

                        case "flow.advisory":
                        {
                            var spoken = !(payload.ValueKind == JsonValueKind.Object
                                && payload.TryGetProperty("spoken", out var sp)
                                && sp.ValueKind == JsonValueKind.False);
                            var id = Str(payload, "id");
                            if (spoken && !string.IsNullOrWhiteSpace(id))
                            {
                                advisories.Add(id);
                            }

                            break;
                        }

                        case "cabin.report":
                            cabinReports++;
                            break;

                        case "radio.set":
                        case "radio.swapped":
                            radioTunes++;
                            break;

                        case "drill.completed":
                            memoryDrills++;
                            break;

                        case "techlog.raised":
                            defectsRaised++;
                            break;

                        case "techlog.rectified":
                            defectsRectified++;
                            break;

                        case "techlog.carried":
                        case "techlog.briefed":
                        {
                            if (Num(payload, "open") is { } open)
                            {
                                defectsCarried = Math.Max(defectsCarried, (int)open);
                            }

                            break;
                        }

                        case "failure.detected":
                        {
                            var id = Str(payload, "id");
                            if (!string.IsNullOrWhiteSpace(id) && !abnormals.ContainsKey(id))
                            {
                                abnormals[id] = (Str(payload, "title") ?? id, false);
                                abnormalOrder.Add(id);
                            }

                            break;
                        }

                        case "failure.cleared":
                        {
                            var id = Str(payload, "id");
                            if (!string.IsNullOrWhiteSpace(id) && abnormals.TryGetValue(id, out var known))
                            {
                                abnormals[id] = (known.Title, true);
                            }

                            break;
                        }

                        case "flight.route":
                        {
                            var airport = Str(payload, "airport");
                            var runway = Str(payload, "runway");
                            if (Is(Str(payload, "role"), "arrival"))
                            {
                                if (!string.IsNullOrWhiteSpace(airport))
                                {
                                    destination = airport;
                                }

                                if (!string.IsNullOrWhiteSpace(runway))
                                {
                                    arrRunway = runway;
                                }
                            }
                            else
                            {
                                if (!string.IsNullOrWhiteSpace(airport))
                                {
                                    origin = airport;
                                }

                                if (!string.IsNullOrWhiteSpace(runway))
                                {
                                    depRunway = runway;
                                }
                            }

                            break;
                        }

                        case "fuel.check":
                        {
                            if (Num(payload, "fobKg") is { } fob)
                            {
                                fobs.Add(fob);
                            }

                            break;
                        }
                    }
                }
            }

            int? blockMin = offBlock is { } ob && onBlock is { } on && on > ob
                ? (int)Math.Round((on - ob).TotalMinutes)
                : null;
            int? flightMin = liftoff is { } lo && touchdown is { } td && td > lo
                ? (int)Math.Round((td - lo).TotalMinutes)
                : null;
            double? startFob = fobs.Count > 0 ? fobs[0] : null;
            double? finalFob = fobs.Count > 0 ? fobs[^1] : null;
            double? used = fobs.Count >= 2 && fobs[0] > fobs[^1] ? fobs[0] - fobs[^1] : null;

            return new DebriefFacts(
                blockMin, flightMin, liftoffIas, touchdownGs, gates,
                callouts, suppressed, checklists.Count, checklists,
                startFob, finalFob, used, advisories.ToList(), degradations,
                abnormalOrder.Select(id => new AbnormalFact(abnormals[id].Title, abnormals[id].Cleared)).ToList(),
                origin, destination, depRunway, arrRunway, cabinReports,
                defectsRaised, defectsRectified, defectsCarried, radioTunes, memoryDrills);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Debrief fact extraction failed for {Path}", sessionPath);
            return DebriefFacts.Empty;
        }
    }

    private static bool Is(string? value, string expected)
        => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    private static string? Str(JsonElement payload, string field)
        => payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty(field, out var v)
            && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

    private static double? Num(JsonElement payload, string field)
        => payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty(field, out var v)
            && v.ValueKind == JsonValueKind.Number
            && v.TryGetDouble(out var d)
                ? d
                : null;

    private static double? Snap(JsonElement payload, string field)
        => payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("snapshot", out var s)
            && s.ValueKind == JsonValueKind.Object
            && s.TryGetProperty(field, out var v)
            && v.ValueKind == JsonValueKind.Number
            && v.TryGetDouble(out var d)
                ? d
                : null;

    private static string? FirstFailingCriterion(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("criteria", out var criteria)
            || criteria.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var criterion in criteria.EnumerateArray())
        {
            if (criterion.ValueKind == JsonValueKind.Object
                && criterion.TryGetProperty("result", out var result)
                && Is(result.GetString(), "fail"))
            {
                return criterion.TryGetProperty("name", out var name) ? name.GetString() : null;
            }
        }

        return null;
    }
}
