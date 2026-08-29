# ADR-0008: Flight phase is an ordered rule table, tuned by replaying recorded flights

Date: 2026-08-29
Status: accepted
Revises: the hand-written decision tree in `FlightPhaseEvaluator` and its two debounce maps in `FlightStateEngine`.

## Context

The flight-phase engine is the single state every feature consumes (GSX automation, the
voice First Officer, cabin, callouts, debrief, logbook). Between 2026-08-09 and 2026-08-23
eight issue-driven patches (#48, #59, #93, #99, #100, #101, #104, #105) each added a
constant and a debounce entry to a nested if-tree, and every one was reconstructed by hand
from `phase-changed` log lines because the session log kept no per-tick evidence. Each flight
found the next edge case.

A review of Prosim2GSX's simpler ground state machine (2026-08-29) found robustness it has
and we did not: a ground-contact agreement counter, composite parked detection, an explicit
turnaround, a pilot escape hatch, and a heartbeat log. Reading our tree also found two real
holes: Shutdown could only leave via cold-and-dark (an engine start after shutdown read as
TaxiIn, so leg 2 of a day never got ground prep), and the phase was never reset when the
pilot left the flight. Three go-around consumers keyed on an `Approach → InitialClimb` edge
the tree never produced.

## Decision

1. **The classifier is an ordered rule table** (`FlightPhaseRules`: a ground list and an
   airborne list of `PhaseRule` — from-set, target or *hold*, evidence test, settle time,
   reason). `FlightPhaseEvaluator.Decide` returns the first match; the engine debounces the
   decision it is handed. Every commit carries the rule id and reason (log, `phase-changed`
   payload, Status page). `Evaluate(snapshot, current)` stays as the pure facade so the
   existing evaluator tests remain the contract.
2. **Every threshold and settle time is a `flightState` option** (`FlightStateOptions`, hot
   reloadable, edited on the Flight Phase settings page). A field-found edge case is a
   settings change first and a rule change second.
3. **Turnaround is a rule, not a power-down**: `Shutdown → Preflight` once the ground-ops
   layer reports the arrival complete (deboarding done — `GroundOpsSignals.ArrivalCompleted`,
   stamped onto the sample as `ArrivalComplete`) and the aircraft has sat parked (engines off,
   brake set, beacon off, stopped) for the hold; `Shutdown → PushbackAndStart` on
   beacon-corroborated start evidence. The phase resets to Unknown when the sim session ends.
   *Amended 2026-08-29 (ESSA):* the first cut was time-only and fired 30 s after shutdown
   mid-deboarding — ground prep repositioned the aircraft under the passengers. A phase that
   opens ground automation must wait for ground evidence, not a clock. The prep chain also
   never repositions on a turnaround, and voice-activation mode never auto-starts departure.
4. **Ground contact is a committed state**: the raw on-ground flag must agree for N
   consecutive samples (default 2) before ground/air flips.
5. **Go-arounds land on InitialClimb** from Approach/LandingRollout, the edge the consumers
   key on.
6. **Every flight is replayable**: `FlightSampleRecorder` writes a compact `flight-sample`
   event about once a second while live; `FlightReplay` feeds a session file back through a
   fresh engine at the recorded times and diffs the result against the recorded commits.
   Recorded flights checked into `tests/.../Flight/Recordings` with an expected timeline are
   regression tests. A phase fix ships with a replayed recording, never a guessed constant.
7. **The pilot has an escape hatch**: `IFlightPhaseControl.ForcePhase/ResumeAutomatic` on the
   engine, exposed on the Flight Status page. A forced phase never opens the airborne
   write-safety latch.

## Consequences

- A "why did it think we were on approach?" question is answered by the reason on the Status
  page or the `phase-changed` event, not by reading code paths.
- Session files grow to a few MB per flight; the logbook/debrief readers ignore the sample
  event type and the telemetry API serves the file as before.
- The Approach→Climb tests changed to Approach→InitialClimb; everything else in the 54-test
  contract passed unchanged against the table.
- Not done here: replay of the *consumers* (callouts, cabin) — the harness drives the engine
  only. The sample record already carries V-speeds and N1 so that can follow.
