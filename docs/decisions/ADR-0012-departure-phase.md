# ADR-0012: Departure is a real flight phase, entered on boarding evidence

Date: 2026-09-20
Status: accepted
Amends: ADR-0008 (the rule table gains one phase and one latch).

## Context

The 2026-09-19 EGLL session (`session-20260919-235732`) showed the phase text PREFLIGHT for
44 minutes: from power-on at 00:01 UTC, through catering and refuel, boarding (00:16–00:25),
and a 20-minute wait for the push at 00:45. The engine was right by its own rules — nothing
between Preflight and PushbackAndStart existed — but the pilot read it as the app having lost
its place.

The progress strip used to carry a DEPARTURE block lit from the GSX departure sequence. It
was removed on 2026-09-13 (issue #110, owner's Option 2) because it disagreed with the phase
text for 32 minutes on the 2026-09-13 leg: two state machines, one card. Bringing back a
display-only block would bring back the contradiction.

## Decision

1. **`FlightPhase.Departure`** sits between `Preflight` and `PushbackAndStart`: boarding
   underway or complete, still at the gate, engines off. The strip's DEPARTURE block and the
   phase text now come from the one state machine, so they cannot disagree.
2. **Entry evidence is boarding, not the service sequence.** Refuel and catering can run on a
   cold cockpit; boarding cannot. `GroundOpsSignals.BoardingStarted` (GSX Boarding went
   Active, relayed by `GsxGroundOpsSignalRelay`) is latched by the engine and stamped onto
   the sample as `BoardingStarted`; rule `boarding` takes Preflight → Departure on it. The
   latch is accepted while unclassified or before taxi-out (a boarding that starts during a
   cockpit power cycle is not lost) and cleared once the aircraft leaves the pre-taxi window.
3. **Departure never steps back.** Rule `departure-hold` keeps it through boarding
   completion, doors, and beacon/APU flickers with the brake set; it leaves only on the
   existing departure evidence (push, engine start, take-off thrust) or a power-off. A
   progress bar never regresses within one departure (the 2026-09-05 seven-minute PREFLIGHT
   step-back, #110).
4. **One helper per window.** `FlightPhase.IsAtGate()` (ColdAndDark/Preflight/Departure)
   and `IsBeforeTaxiOut()` (+ PushbackAndStart) replace the dozen hand-written
   "Preflight or ColdAndDark" checks in the GSX and Speech pillars. A phase added to the
   model is added to each window in one place.
5. **Replay keeps its evidence.** `flight-sample` carries `brd` (omitted while false, so
   older recordings parse unchanged), and `FlightReplay` re-derives the signal from the
   session's `gsx-service` Boarding/Active events, so every checked-in recording exercises
   the new edge. The two real-flight recordings' expected timelines gained
   `Preflight -> Departure` / `Departure -> PushbackAndStart`.

## Consequences

- The Flight Status sequence is seven blocks again (PREFLIGHT, DEPARTURE, PUSHBACK, TAXI OUT,
  FLIGHT, TAXI IN, ARRIVAL); the active DEPARTURE step says "Boarding" / "Boarding complete".
- GSX automation maps Departure to its Preparation window, the same as Preflight — the
  departure sequence is already running inside it.
- Features keyed on *entering* Preflight (day start, tech-log expiry, callout reset,
  session finalizer) are unchanged: Preflight still precedes Departure.
- A flight without GSX boarding (voice-only ground ops, GSX absent) stays Preflight until the
  push — the pre-2026-09-20 behaviour. A ProSim-side fallback (`efb.efb.boardingStatus` or a
  rising passenger count) is deferred until its values are verified live.
- Probe `departure-phase-on-boarding` in `docs/agents/verification-probes.json`.
