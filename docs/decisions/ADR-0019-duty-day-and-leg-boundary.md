# ADR-0019: The duty day is a sequence of legs; a leg boundary is three pilot-visible steps

Date: 2026-11-02
Status: accepted (owner decisions 2026-11-02, grilling session before the duty-day flow audit;
the code at f1a4653f does NOT yet implement this — the audit measures it against this ADR)
Amends: ADR-0012 (the phase engine keeps its rules; the leg state is a second, non-overlapping
machine shown beside the phase), the Duty Day feature (#164: legs start on the plan, not on
the Preflight edge; on by default).

## Context

The owner wants the whole duty day audited: cold and dark at the first airport, every leg,
every turnaround, app restarts anywhere, to the final shutdown. The audit needs a yardstick
that says what the app must do at each step, and the code at f1a4653f has no written one.

What the code does today (mapped 2026-11-02; file references at that commit):

- After landing the phase goes TaxiIn → Shutdown, then Shutdown → **Preflight** 30 s after
  deboarding completes (`turnaround-arrival-complete`, `FlightPhaseRules.cs:127`). No plan is
  needed. The Duty Day engine hears that Preflight edge and starts the next leg at once —
  the FO says "Leg 2. New sector" with no plan loaded (`CompanyDayService.cs:328`).
- A new plan is known by its SimBrief request id (route as the fallback; the flight number
  does not count). The reset it triggers (#207, `NewPlanCycleReset.cs`) clears the departure
  services, the pax target and the tracking LVARs — not the gate monitor, loadsheet, upcalls
  or notifications; only the arrival at Shutdown raises `FlightCycleReset` for those.
- Nothing detects an MCDU/FMS reset. The IOS button writes `control.resetFMS`, which is
  write-only; its effects are readable: `aircraft.fms.origin` and `aircraft.fms.destination`
  go empty and the FMGC's own phase `aircraft.fms.flightPhase` leaves DONE. ProSim's IOS
  option **Reset FMS after landing or failure** (`system.config.Config.RESETFMS`) clears the
  FMS by itself after landing when on.
- The Duty Day feature is off by default. When on, the day ends by voice, by the web button,
  or after 90 min idle in turnaround. Ending writes a summary file; nothing is spoken.
- The flight-state snapshot is cleared at Shutdown and writes stay suspended until a new
  plan; a restart during a turnaround relies on the startup resync's tracking LVARs alone.
- Deboarding complete drives the phase and a notification; no crew line is spoken.
- `gsx-cycle-reset` always records `cycleWasComplete: false` (read after the reset).

## Decision

1. **Duty day, leg, leg boundary, end of duty** are domain terms (CONTEXT.md, "Duty day").
   The duty day is one crew's working day; it holds the legs; it never ends on its own.
   A **leg** begins when its OFP is loaded — not when the aircraft is powered, not when the
   previous cabin empties.

2. **The leg boundary is three steps, each a state the pilot can see** on the Flight Status
   card (one pill under the phase, hidden outside a turnaround), on the arrival monitor of the
   pop-out board, and as one session event per state change:
   - **Turnaround** — deboarding complete (GSX Deboarding completed). Parked, powered, cabin
     empty, the old plan still in the FMS. The purser reports once, Low priority: "Cabin's
     empty, captain."
   - **Awaiting plan** — the FMS plan cleared: `aircraft.fms.origin` AND
     `aircraft.fms.destination` go from a value to empty while parked with the engines off.
     Event `fms.cleared`. The FMGC phase leaving DONE is logged as a witness, never required.
   - **Preflight of the new leg** — a *different* OFP loaded (request id, else route). The FO
     announces the new sector (and may say the route). Ground preparation may begin.
   The order is preferred, never required: any step may come first (RESETFMS on puts step 2
   before step 1; a pilot may load the plan before the reset; GSX deboarding may be skipped).
   A missed step is a warning in the log, not an error. **A new OFP always begins the leg**
   whatever else happened. The same OFP loaded twice is one leg: nothing resets.

3. **One reminder, not a nag.** New OFP loaded, cabin empty, FMS still holding the previous
   plan: the FO says once, "Captain, the FMS still holds the previous flight plan." Never when
   the FMS already cleared (RESETFMS on, or the pilot reset first).

4. **The phase engine is untouched.** Turnaround and Awaiting plan are NOT flight phases
   (ADR-0012's lesson about two machines on one card was about overlap; these do not overlap:
   the phase reads PREFLIGHT while the leg state reads AWAITING PLAN). `Shutdown → Preflight`
   on deboarding stays. `aircraft.fms.flightPhase` is a witness (log, Diagnostics, probes),
   never a phase-engine input (ADR-0008 stays sensor-based).

5. **The Duty Day engine starts the next leg on the new OFP**, not on the Preflight edge. The
   Preflight edge only closes the arrival. The leg's route is seeded from the plan that began
   it.

6. **A different plan at the same gate resets everything the arrival resets** — gate monitor,
   loadsheet, upcalls, notifications (`FlightCycleReset`) — when boarding has not started.
   A same-id re-import resets nothing.

7. **The leg-boundary machine runs always.** The Duty Day feature (legs by name, roster, end
   of duty, summary) sits on top and is **on by default** from 1.0 (auto-start on the first
   Preflight).

8. **End of duty is the pilot's act**, three ways, any one works: voice ("end of duty"), the
   web button (Flight Status and Duty Day cards), or accepting the FO's one-time offer —
   "Captain, is that the end of our duty day?" — asked after the aircraft has been cold and
   dark, parked, with no plan for 10 minutes. No or silence leaves the day open. The 90-minute
   idle auto-close stays as the backstop. On end of duty: a short spoken debrief (legs, block
   time, fuel, tech-log entries), the day record closed (logbook and fuel log final), the
   roster FO released for the next sim day, the flight-state snapshot and the departure cycle
   cleared so a restart revives nothing, ground preparation held off until a new OFP. A plan
   loaded after the end starts a **new duty day with the same FO** (the roster draws per sim
   calendar day).

9. **A restart is invisible.** During Turnaround or Awaiting plan the snapshot keeps a small
   turnaround part (deboarded, FMS cleared, last plan id) so a restart lands in the same leg
   state; the tracking-LVAR resync is the fallback. Never Preflight-with-nothing, never a
   revived old leg.

10. **Deficiencies are of two kinds**, silent misses (the app does not do what this ADR says)
    ranked above wrong displays and wrong lines. The audit report lives at
    `docs/testing/2026-11-02-duty-day-flow-audit.md`; each approved gap becomes one GitHub
    issue (`ready-for-agent`) linking this ADR and the report line.

Owner's own setting for **Reset FMS after landing or failure**: _to be recorded_ (the audit
replays that case first).

## Consequences

- A new small state machine (leg boundary) owned by the departure-cycle side, one new pill,
  one event family, one FMS-cleared watcher on two Infrequent datarefs already subscribed.
- Day engine: leg start moves from the Preflight edge to the OFP edge; `day.enabled` default
  flips to true — a settings-page default change and a manual change in the same commit.
- `NewPlanCycleReset` raises `FlightCycleReset` for a different plan with no boarding; the
  `cycleWasComplete` field is read before the reset.
- Snapshot: a `turnaround` part written after Shutdown; the "writes suspended after Shutdown"
  rule narrows to "after end of duty".
- Speech: one purser line, one FO reminder, one FO offer, one spoken debrief — all through the
  arbiter with the usual gates; nothing new writes to the aircraft.
- Probes: `leg-boundary-order`, `fms-cleared`, `day-end-offer`, `turnaround-restart` ship
  with the fixes; the EHAM turnaround of 2026-11-01 is the first replay.
- Rejected: Turnaround and Awaiting plan as `FlightPhase` values (every at-gate window, GSX
  prep gate and cabin re-arm would need teaching; the pilot gains nothing the pill does not
  give); the duty day ending on its own (a day that ends under the pilot is the #110 feeling
  again); a timer-only end of duty (the offer makes the button unnecessary; the backstop
  keeps the record tidy).
