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

When the sim PC is remote and the telemetry API is unavailable, the fallback is what we did
on 2026-08-17: copy `logs/` + `sessions/` into the repo's `scratchpad/` and point the agent
at them.

## Evaluating a flight

1. Identify the session files covering the flight (there may be several if the app was
   restarted — restarts are themselves evidence; see probe `crash-without-trace`).
2. Run the `phase-commit-evidence` probe **first**: it doubles as the build canary. If the
   `phase-changed` payloads lack the full field set, the sim PC is running an old build and
   most other probes are meaningless — say so instead of reporting false failures.
3. Evaluate every probe whose `state` is `open` or `closed-regression-watch`, honouring the
   `kind` semantics defined at the top of the probe file. Three verdicts per probe:
   **pass** (behaviour observed / signature absent while the trigger occurred),
   **fail** (quote the exact events/log lines), and
   **untested** (the trigger never occurred this flight — never report as a pass).
4. `watch` probes produce counts and context, not verdicts.
5. End the report with the `requires-human-checklist` entry so the pilot knows what still
   needs eyeballs.

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
