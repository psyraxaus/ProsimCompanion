# ADR-0018: Scheduled non-normals through the ProSim SDK failure API behind its own gate

Date: 2026-11-01
Status: accepted (owner decisions 2026-11-01 on issue #197: the SDK failure API is the write
path, clear-all also clears IOS-set failures, no AP-disconnect scenario until an abnormal
exists for it, the first build only lists ProSim's failures until the catalogue names are
verified live)
Amends: the write-safety rule in docs/ARCHITECTURE.md (a second, separate allow-list for a
different kind of write)

## Context

The voice First Officer's response side is complete: `FailureMonitor` detects ECAM text and
runs the ECAM dialogue, the memory drills fire, RTO and EFATO exist, the tech log offers a
post-abnormal shutdown entry, the debrief records it. What is missing is a cause other than
the pilot clicking the ProSim IOS. Scenario mode (issue #197) lets the app arm one minor
failure per flight, rarely and opt-in, so the response side gets used.

That is the first write into ProSim's failure system, and `ProsimWriteGate` deliberately
keeps the `system.config.` family off its allow-list: the comment on the setup-check names
"engine type, failures and the cockpit-setup loader" as the reasons. So this needed a
decision, not an implementation.

The reading changed the question. The A322 dataref catalogue (`Prosim_A322_Dataref.csv`)
holds **no per-failure dataref at all**. Its only failure-related `system.config.` rows are
ProSim's own random-failure generator — `Config.autoFailure` (None | Minor | All),
`Config.failureRate` (Realistic | High), `Config.engineAutoRestart` — which this app must
never switch on. Individual failures live behind an SDK object API (`ProSimSDK.xml`):
`ProSimConnect.getFailures()` lists the supported `Failure` objects (name, description,
domain, group, class); `ArmedFailure.Create(Failure)` activates one now and
`ArmedFailure.Create(FailureCondition, Failure)` arms it on a condition (altitude, speed,
time, or after Takeoff / V1 / Landing); `ArmedFailure.Reset()` clears it; `getArmedFailures()`
lists everything armed, the IOS's included. The EFB gateway lists the same failures read-only
(`GET /efb/failures`).

## Decision

1. **The write path is the SDK failure API, not a dataref.** `ProsimWriteGate` is not touched;
   a test pins that `system.config.Config.autoFailure` stays refused. The failures are armed
   through `ArmedFailure.Create`, cleared through `ArmedFailure.Reset`, verified through
   `getArmedFailures()` — the same calls the ProSim IOS makes.
2. **A separate gate on failure names.** `ProsimFailureGate` (Prosim pillar, beside
   `ProsimWriteGate`, sharing nothing with it) passes a name only when it is in the scenario
   catalogue's *verified* entries, installed once by the injector, AND in ProSim's live list
   at the moment of the write. It has no dataref path: nothing in the type takes a dataref,
   and a test says so by reflection. One implementer (`SdkFailureInjector`), enforced with
   the same `internal` discipline as ADR-0015's hook.
3. **One Core seam, one serialised path.** `IFailureInjector` (Core, implemented in Prosim —
   the `IAcarsUplink` shape) is reached only through `ScenarioService` (Speech), which is the
   `IScenarioControl` behind the commands `scenario.arm`, `scenario.clear`,
   `scenario.clearAll` and `scenario.listFailures`. A command names a catalogue id, never a
   failure name; nothing arriving over the network is a failure name. Every write is
   announce (session event) → a 3 s cancel window → write → verify, under one lock —
   the FCU / MCDU gate shape.
4. **Safety floor.** Never in flight below `scenario.minAltitudeFt` AGL (1,500); the two
   takeoff scenarios are their own switch and arm on the ground on the SDK's own after-V1 /
   takeoff condition, only with the RTO / EFATO features on; never while an abnormal is open,
   a drill or ECAM dialogue runs, the tech log holds a category-A (no-go) item, the flight is
   not live, or the cockpit is sterile. `scenario.clearAll` is unconditional and resets every
   armed failure ProSim reports — **the IOS's included**; the pilot who says "clear all
   failures" means all. Every failure the app armed is cleared and the clear verified at the
   flight-cycle reset and at app exit; ProSim unreachable is said on the STATUS page and in the
   log.
5. **The catalogue is a subset of the abnormals.** `scenarios.json` entries name an
   `abnormalId` that must be loaded (refused and logged otherwise — a scenario the FO cannot
   respond to is worse than none) and a `failureName` marked `verified` only once confirmed
   against the live list on a rig. Unverified entries are never armed. Major entries (engine
   fire / failure, APU fire) are present but excluded without `scenario.allowMajor`. No
   AP-disconnect entry until an abnormal definition exists for it.
6. **Only `Planned` survives a restart.** The plan's state (`Planned → Armed → Fired →
   Cleared`) is persisted; a load finds Planned (re-judged against the floor before it can
   arm) or, for Armed / Fired left by a crash, runs one clear-all. The phase replay drives the
   flight engine only and never constructs the scheduler; the startup resync seeds GSX, not
   this.
7. **Detection stays the real path.** The armed failure reaches `FailureMonitor` through the
   E/WD text and the system datarefs exactly as an IOS failure — no shortcut, no side
   channel. `scenario.fired` is recorded when the monitor detects the matching abnormal; the
   probe pairs the two.

## Consequences

- The write surface gains one new kind of write, bounded three ways: the catalogue (user
  file, verified names only), ProSim's own live list, and the one command path. Audit trail:
  `scenario.arming` / `armed` / `armFailed` / `fired` / `cleared` / `clearFailed` /
  `clearAllRequested` session events with the catalogue id, the failure name and the source.
- The exact failure names are a rig fact: the first build lists them (`scenario.listFailures`,
  the log line, the `scenario.failuresListed` event) and the seed ships every entry
  `verified: false`. The owner flips entries to `true` as the list confirms them.
- `ProsimConnectionService` is registered as itself as well as a hosted service so the
  injector can run SDK work on the live session; a rebuilt session invalidates every SDK
  object, so the injector re-reads the armed list instead of caching handles.
- The SDK types bind at compile time against the build SDK; an older `ProSimSDK.dll` without
  the failure members reads as "scenario mode off" through the existing mismatch check, never
  a crash.
