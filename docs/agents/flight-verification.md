# Flight verification workflow

Post-flight (or mid-flight) evaluation of GitHub issues against the app's own telemetry, so
sim time goes into flying, not log spelunking. The probe catalog is
[`verification-probes.json`](verification-probes.json); this file is the operating manual.

## Sources

| Source | Where | Notes |
|---|---|---|
| Session event log | `%LOCALAPPDATA%\ProsimCompanion\sessions\session-*.jsonl` | One JSON object per line: `timestamp`, `type`, `payload`. The primary evidence stream. |
| App log (CMTrace) | `%LOCALAPPDATA%\ProsimCompanion\logs\ProsimCompanion-<date>.log` | `type="1"` info, `"2"` warning, `"3"` error. |
| Wire trace | `...\logs\ProsimCompanion-wire-<date>.log` | GSX frames only; often disabled and can be huge — consult only when a probe names it. |
| Telemetry API | `http://<host>:5320/api/telemetry/*` (issue #94) | Same files over HTTP, token-gated — lets the agent pull evidence from the sim PC without file copying, including mid-flight. |
| User diagnostics bundle | Logs page → "Export diagnostics" (`/api/diagnostics/bundle`) | The user-sent zip: newest sessions + logs + redacted settings + version banner with a hash manifest. `tools/ProsimCompanion.Reduce` turns it into the support report and pre-evaluates the machine-checkable probes. See [`support-bundle.md`](support-bundle.md). |

When the sim PC is remote and the telemetry API is unavailable, the fallback is what we did
on 2026-08-17: copy `logs/` + `sessions/` into the repo's `scratchpad/` and point the agent
at them.

## Evaluating a flight

1. Identify the session files covering the flight (there may be several if the app was
   restarted — restarts are themselves evidence; see probe `crash-without-trace`).
2. Read the build from the first line of each session file **first**: since 0.5.0 the
   `session-started` (and `session-rotated`) payload carries `appVersion`, `commit`,
   `runtime`, `os` and the sample interval, and the CMTrace log opens with the matching
   `ProsimCompanion <version> (<commit>) starting on ...` banner. A first line with no
   payload means an older build ("unversioned") — fall back to the `phase-commit-evidence`
   probe as the canary. If the `phase-changed` payloads lack the full field set, the sim PC
   is running an old build and most other probes are meaningless — say so instead of
   reporting false failures.
3. Evaluate every probe whose `state` is `open` or `closed-regression-watch`, honouring the
   `kind` semantics defined at the top of the probe file. Three verdicts per probe:
   **pass** (behaviour observed / signature absent while the trigger occurred),
   **fail** (quote the exact events/log lines), and
   **untested** (the trigger never occurred this flight — never report as a pass).
4. `watch` probes produce counts and context, not verdicts.
5. List the **voice command candidates** (below) — the phrases the pilot keeps saying that
   the FO has no action for.
6. End the report with the `requires-human-checklist` entry so the pilot knows what still
   needs eyeballs.

## Voice command candidates (issue #112)

Every utterance that nothing acted on — no command, feature, checklist answer or question
handler — is a `voice.unmatched` session event (`text`, `normalized`, `score`, `context`,
`phase`, `reason`, `suppressed`). Read them after every flight:

- Group by `normalized`, drop `suppressed = true` (sterile-phase absorptions: callouts, not
  commands), and list every phrase heard **3+ times** as a command candidate, with its
  `context`s. `idle` means a new command (`commands.json` / `atc-requests.json` under
  `%LOCALAPPDATA%\ProsimCompanion\config`); `checklist: <line>` with reason `not-an-answer`
  means that line's `acceptedPhrases` is missing the pilot's wording; `confirm-declined`
  means the snapper guessed wrong — a phrase close to an existing one that still needs its own.
- The support reducer pre-computes the same list per session as
  `sessions[].voiceUnmatched.candidates` (see [`support-bundle.md`](support-bundle.md)).
- The Voice status page (`/speech`, "Heard but not understood") shows the live list during
  the flight; `speech.trackUnmatched` (default on) gates both the event and the page.
- Candidates are a proposal to the owner, never an automatic edit of the command files.

## Replaying a flight through the phase engine

Since 2026-08-29 the session log carries a compact `flight-sample` event about once a second
while the flight is live (`FlightSampleRecorder`; keys documented on `FlightSample`).
`FlightReplay.RunFile(path)` feeds those samples through a fresh `FlightStateEngine` at their
recorded times, re-creating the 250 ms tick cadence, and reports this build's phase timeline
next to the live engine's recorded `phase-changed` edges (`Describe()`).

- **Diagnosing a bad phase**: replay the session, read the rule id + reason on the offending
  edge, adjust the rule or a `flightState` threshold, replay again — no sim needed.
- **Turning a flight into a regression test**: copy the session file into
  `tests/ProsimCompanion.Core.Tests/Flight/Recordings/` with a sidecar
  `<name>.expected.txt` (one `From -> To` per line; `FlightReplayTests` prints the timeline
  to paste when the sidecar is missing). Every rule change is then judged against that flight.
- A replay that **diverges** from the recording means the running build and the repo disagree
  (old build on the sim PC, or a rule change) — report the first differing edge.

## Reporting rules

- Comment evidence on the matching GitHub issue when a probe **fails** (bug reproduced /
  regression) or when a probe for an **open** issue passes in a way that advances closure.
  Quote timestamps and exact events; the next reader should not need the raw logs.
- A passing probe means "observed working this flight" — closure is proposed to the owner,
  never automatic. Regressions on `closed-regression-watch` probes reopen the issue.
- New failure signatures with no matching issue get filed as new issues and, when
  observable from telemetry, added to the probe file in the same change.

## Maintaining the probe file

- Every behavioural fix ships with a probe (or an update to one) in the same change, the
  way options ship with a web-UI control. Purely visual/installer changes go on the
  `requires-human-checklist` instead.
- When an issue closes, flip its probe to `closed-regression-watch` (keep it — regressions
  are what the watch list is for). Retire a probe only when the code path it guards is gone.
- Probes reference event `type`s and payload fields — if an event shape changes, update the
  probes in the same commit (the `phase-commit-evidence` canary is the model).
