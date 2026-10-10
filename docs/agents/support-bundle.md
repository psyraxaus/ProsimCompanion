# User diagnostics bundle and the support reducer

One button on the Logs page ("Export diagnostics") gives the user a zip that is complete,
consistent and safe to attach to a GitHub issue. One CLI (`reduce`) turns that zip into the
JSON the support pipeline feeds to an LLM step — using the **same** `DebriefFactExtractor`
the in-app debrief uses, so the two can never disagree.

## What the bundle contains

`ProsimCompanion-diagnostics-<utc yyyyMMdd-HHmmss>/` (one top-level folder):

| Path | Content |
|---|---|
| `manifest.json` | `{ bundleVersion: 1, createdUtc, appVersion, informationalVersion, commit, runtime, os, architecture, files: [{ path, bytes, sha256 }], truncated: [{ path, bytes, reason }] }`. The hash list is what `reduce` checks first — a mismatch means the file was edited or cut short. |
| `sessions/session-*.jsonl` | The newest N session event logs (`diagnostics.bundleSessionCount`, default 5, 1–20). Since 0.5.0 the first line of every file is `session-started` (or `session-rotated`) with `appVersion`, `commit`, `runtime`, `os`, `architecture`, `sessionFile`, `sampleIntervalSeconds`, `activeProfile`. Warning-and-above log events are mirrored in as `log.warning` / `log.error` / `log.fatal` (`logging.mirrorToSession`). |
| `logs/ProsimCompanion-<yyyyMMdd>.log` | The newest D daily CMTrace app logs (`diagnostics.bundleLogDays`, default 3, 1–14), every size-roll part of those days included. Each day's file carries the banner `ProsimCompanion <version> (<commit>) starting on <os> / <runtime>; session log <file>` at startup and on every session rotation. |
| `logs/ProsimCompanion-wire-<yyyyMMdd>.log` | Only when `diagnostics.includeWireTrace` (default off) — the GSX/gateway wire trace is large. |
| `config/settings.json` | A **redacted** copy: any string property whose name contains `token`, `apiKey`, `api_key`, `secret`, `password`, `credential` or `bearer`, and any string value that looks like a credential (bearer header, JWT, 32+ hex, DPAPI blob, long opaque token) is replaced with `***REDACTED***`. Structure, booleans and numbers stay, so support can see which features were on. |
| `versions.txt` | The version banner plus which optional dependencies were present (the subsystem status store: ProSim / SimConnect / GSX …). |

Never included: the web access token (it lives in settings.json and is redacted), the
user-editable content under `%LOCALAPPDATA%\ProsimCompanion\config\`, anything outside the
sessions/logs directories and the app settings file.

Size cap: 200 MB of source bytes. Over it, the oldest sessions are dropped first (the newest
is kept as long as possible), then the oldest logs; every drop is listed in
`manifest.truncated`.

The endpoint is `GET /api/diagnostics/bundle?sessions=5&days=3&wire=false`, gated exactly
like the telemetry API (`telemetryApi.enabled`; bearer token or the web-UI cookie), so the
page links to it directly with no token in the URL. The "Copy version" button beside it puts
the banner on the clipboard for the issue text.

## Running the reducer

```
dotnet run --project tools/ProsimCompanion.Reduce -- --bundle <zip|folder> [--out report.json] [--probes <catalog.json>] [--max-kb 60]
```

Publish for the n8n host with `dotnet publish tools/ProsimCompanion.Reduce -r linux-x64`
(Core is plain net10.0; the app itself stays Windows-only). Exit codes: `0` report written,
`1` usage error, `2` bundle refused with the reason on stderr — SHA-256 mismatch, an entry
escaping the extraction root, an extension other than `.jsonl/.log/.json/.txt`, a single
entry over 512 MB, or more than 500 entries. A bare folder (no manifest) is scanned instead
and `bundle.hashesVerified` is `false`.

Every path (`--bundle`, `--out`, `--probes`) must be below the working folder or the system
temp folder; any other path is a usage error (exit `1`, "path refused"). The command line is
outside input on the support host, so the tool does not follow it to an arbitrary file. Run
the tool from the folder that holds the bundle — with `dotnet run`, give `--project` the full
path of `tools/ProsimCompanion.Reduce`. The bundle, the probe catalog and the folder for
`--out` must exist: the tool finds each on disk from the allowed folder down and uses that
entry, never the typed text. Line breaks are removed from all text the tool echoes to the
console.

The probe catalog is embedded in the build; `--probes` points at a newer copy of
`docs/agents/verification-probes.json` when the pipeline is ahead of the release.

## Report JSON

Compact JSON, camelCase. Top level: `{ bundle, sessions, restarts, unattributedLogClusters, probes, forLlm }`.

- `bundle`: `{ source: "zip"|"folder", folder, createdUtc, appVersion, commit, fileCount, sessionFileCount, logFileCount, hashesVerified, truncated[], notes[] }`. `notes` records every trim step applied.
- `sessions[]` (oldest first):
  - `file`, `firstEvent`, `lastEvent`, `eventCount`, `endedCleanly` (a `session-ended` was seen).
  - `header`: `{ status: "session-started"|"session-rotated"|"unversioned", appVersion, commit, runtime, os, architecture, sampleIntervalSeconds, activeProfile }` — the build canary; `unversioned` means a pre-0.5.0 build.
  - `facts`: the `DebriefFacts` record exactly as the app extracts it (block/flight minutes, lift-off IAS, touchdown GS, gates, callouts, checklists, abnormals, route …).
  - `phaseTimeline[]`: `{ at, from, to, rule, reason }` per `phase-changed`.
  - `samples`: `{ rawSamples, points[{ t, ph, alt, ias, vs, ra, gs }], alt|ias|vs|ra: { min, minAt, max, maxAt } }`. One point per 30 s while airborne, per 5 s in TakeoffRoll / InitialClimb / Approach / LandingRollout, none otherwise. Raw samples are never emitted.
  - `eventsByType`: `{ "<type>": count }`; `eventsByTypePerPhase`: `{ "<phase>": { "<type>": count } }`.
  - `logEvents[]` (from the mirrored `log.*` events) and `cmTraceEvents[]` (from the CMTrace files, attributed by time range — the fallback, filled ONLY when the session carries no mirrored events: mirroring off or a build that predates it; never both, so a warning is listed once): `{ level, component, pattern, count, firstSeen, lastSeen, phase, example, exceptionType }`. `pattern` is the message with digits, GUIDs and opaque ids (Blazor circuit ids, Kestrel connection/request ids) replaced by `#`, so one fault is one row with a count. `logSource` says which stream filled them (`session`, `cmtrace`, `none`).
  - `gaps[]`: `{ from, to, seconds, beforeType, afterType }` — more than 30 s between consecutive events while the flight was live (between the first and last `flight-sample`).
  - `voiceUnmatched`: `{ total, suppressed, candidates[{ text, count, contexts[], lastHeard }] }` — the `voice.unmatched` events (issue #112) folded into command candidates: phrases heard 2+ times, most frequent first, at most 10; `suppressed` counts the sterile-phase absorptions, which are never listed. `total` 0 with the feature on means the FO understood everything (or `speech.trackUnmatched` is off).
- `restarts[]`: `{ previousFile, file, previousLastEvent, previousLastType, startedAt }` — a session that began while the previous one never recorded `session-ended`.
- `unattributedLogClusters[]`: CMTrace warnings/errors that fall in no session's time range.
- `probes`: `{ "<probe id>": { kind, issue, state, verdict: "pass"|"fail"|"untested", evidence[] } }` for every probe with a `machine` block (see the catalog's `machine` field for the four kinds: `signature`, `required-fields`, `duplicate-within`, `consecutive-duplicates`). Evidence quotes the matching lines (at most 5). `required-fields` takes two optional refinements: `where` (`{ "<path>": "<text>" }` — only events whose payload reads that text at the path are checked; none matching = `untested`) and `fieldsWhenPresent` (`{ "<path>": ["<field>", …] }` — the fields are required only on events that carry a non-null value at the path, for events that fill in edge by edge such as `flight-times`). Since 2026-10-10 `where` is honoured by every session-event kind — `duplicate-within`, `consecutive-duplicates`, and a `signature` with `source: session` (`contains` may then be `""`: the filter alone selects the events, e.g. a `phase-changed` with `previous`/`current`/`snapshot.pushback`). Booleans and numbers compare as their JSON text (`"true"`). Every evidence stamp is printed in **UTC** (`…Z`): session events are UTC already, CMTrace lines carry the sim PC's local offset (`+660`) and are converted, as are the `cmTraceEvents[]`/`unattributedLogClusters[]` timestamps — before this the two streams read eleven hours apart (ticket t-20261010-0726).
- `forLlm[]`: every other probe, text untouched: `{ id, issue, state, kind, sources, checks[], notes }`. The LLM step evaluates these.

### Size budget

`--max-kb` (default 60) trims in this order: `eventsByTypePerPhase` removed, `eventsByType`
cut to the 20 most frequent types, sample points thinned by half repeatedly. The probe text
under `forLlm` is never trimmed (by contract) and with the full catalog is about 80 KB on
its own — so a whole document under 60 KB needs `--probes` pointed at a reduced catalog
(e.g. only `open` probes), or a larger `--max-kb`; the final `bundle.notes` entry says how
much of the document is static probe text when that happens.
