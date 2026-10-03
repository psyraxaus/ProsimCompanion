# ProsimCompanion

Companion application for the ProSim A320 in MSFS: GSX ground-services automation, a voice
First Officer, and the connective tissue between ProSim, the sim session, and the crew.
This glossary is the canonical language for those domains; architecture decisions live in
`docs/decisions/`.

## Language

### Ground operations

**Trigger slot**:
The single serialized path through which any GSX service trigger is sent. One trigger in
flight at a time; a request made while the slot is occupied is refused, never queued.
_Avoid_: dispatcher, trigger path

**Departure cycle**:
One aircraft turnaround's departure side, from ground-prep start to departure services
complete — including whether it is a turnaround continuation rather than a fresh origin.
_Avoid_: departure state, automation phase

**Gate monitor**:
The Flight Status hero strip that latches one of five forward-only states for the current
departure cycle — Gate Closed, Gate Open, Boarding, Final Call, Gate Closed — from the
latched GSX Boarding stage, the GSX pax counters, door 1L and the effective STD. It resets
only with the ground-ops cycle; it dims after taxi-out but never hides.
_Avoid_: boarding widget, gate status pill

**Flight monitor board**:
The pop-out window (`/monitor`) that shows the gate monitor at the stand, the flight monitor
from pushback to landing and the arrival monitor after block-in — one fixed 1920×1080 stage
scaled as a whole to the window. In flight mode its progress line is the route strip: the
origin → destination great circle drawn straight, the aircraft marker placed from flight
progress, with the distance to go and the ETA beside it. It shows position-based figures
while there are any and the time-based fallback otherwise, and says which.
_Avoid_: second screen, FIDS, dashboard

**Flight progress**:
How far along the leg the aircraft is: distance flown, distance to go, the fraction done, the
ETA and the time to top of descent. Position-based when the flight is live, the aircraft has a
position and the OFP's airports are located (ProSim gateway, else the Navigraph DFD);
otherwise each figure falls back on its own to the time-based rule (off-blocks against the OFP
enroute time) and carries that basis. Distances are great-circle direct, never along the
route. The top of descent is an estimate (3:1 rule) that errs early.
_Avoid_: route progress, distance remaining on the route, FMS distance

**Weather card**:
One of the two hero-card weather tiles: "local weather" (the origin until Descent, the
destination from Descent on) and "weather at destination" (the alternate once local has
moved to the destination). Fed by the composite weather chain; the sky graphic is the
classifier's coarse read of the METAR, the raw text stays on hover.
_Avoid_: METAR box, weather widget

**Ground-ops cycle**:
The logical cycle of ground-operations milestones (loadsheets, deice, reset) that features
observe; each milestone occurs at most once per cycle.
_Avoid_: ground signals

### Sim session

**Session window**:
A settle period anchored to sim-session entry, after which time-gated checks may conclude.
Resets when the session ends.
_Avoid_: settle timer, assessment window

**Session gate**:
The single authority on whether the sim session is live enough for a purpose. Two named
questions: *data is meaningful* (flight data may be trusted — includes the walkaround) and
*may drive ground services* (GSX automation may act — excludes the walkaround).
_Avoid_: in-session check, session predicate

**Flight live**:
The voice First Officer's single arming signal: the session gate says data is meaningful AND
every phase-critical dataref is registered and fresh AND the sample is physically plausible.
Published by the flight-state engine; every tick-driven FO module holds while it is false and
resets its per-flight latches on the false edge. ProSim connectivity alone is never enough —
ProSim pushes plausible cold-and-dark data, faults included, with no MSFS session.
_Avoid_: FO armed, ready flag, classification enabled

### Flight phase

**Phase rule**:
One edge of the flight-phase graph: the phases it may fire from, its target (or *hold*), the
evidence test, its settle time and a reason string. The ordered rule table (ground list, then
airborne list; first match wins) is the whole classifier — a phase fix is a rule or a
threshold, never a new branch in a tree.
_Avoid_: transition condition, evaluator branch

**Phase commit**:
A transition the engine actually published, after the matched rule's evidence persisted for
its settle time. Carries the rule id and reason; a forced phase and a session-end reset are
commits with the engine's own ids.
_Avoid_: phase change (ambiguous with the raw evaluator vote)

**Ground contact**:
The committed on-ground/airborne state: the raw weight-on-wheels flag must agree for a
configured number of consecutive samples before it flips, so a one-sample flicker never
commits a runway transition.
_Avoid_: on-ground flag (that is the raw dataref)

**Flight sample**:
The compact per-second record of what the phase engine saw, written to the session log while
the flight is live. The replay feeds these back through the engine; a recorded flight plus its
expected timeline is a regression test.
_Avoid_: telemetry tick, snapshot event

**Touchdown**:
One landing as the touchdown recorder measured it: the ground-contact edge after the aircraft
has been off the ground for at least five seconds, reported once the landing has settled. Its
rate is the most negative vertical speed in the second before the wheels first touched — not
the sample at contact. A touch followed by more than five seconds airborne is a go-around, a
separate touchdown from the landing that ends the flight.
_Avoid_: landing event, landing-rollout edge (that is the phase commit)

**Bounce**:
Ground contact lost and regained within five seconds of a touchdown. Counted on the committed
ground contact, so a one-sample flicker of the raw flag is not a bounce.
_Avoid_: skip, second touchdown

### Speech

**Spoken text**:
The TTS-ready rendering of an aviation identifier — runway, airport, waypoint, frequency,
altitude — including the fallback when the friendly form is unknown (an unknown airport is
NATO-spelled, never letter-spelled).
_Avoid_: phrasing, pronunciation helpers

**Streamed narration**:
An LLM-styled briefing or debrief spoken while the model is still writing it: each sentence
is verified against the fact set — spelled-out numbers included — and only then handed to
the arbiter as the next segment of ONE queue item, so nothing at the same or a lower priority
gets between its sentences, a High callout plays between two of them, and a Critical ends it
for good (never resumed, never restarted). Off by the `briefing.streamLlm` switch = the
whole-reply path.
_Avoid_: incremental speech, token streaming (that is the wire, not the speech)

**Template takeover**:
The deterministic template finishing a streamed narration that the model could not: the
first sentence that fails verification is dropped with everything after it (likewise a stall,
a broken stream or an error), and the template's sections the pilot has not heard yet follow
in template order. A section counts as heard when its numbers, one of its key words and its
runway appear in what was spoken; a miss repeats a short fact, never loses one. Nothing
spoken yet = the whole template as one ordinary utterance.
_Avoid_: fallback restart, re-ask (there is none on the streamed path)

**Utterance router**:
The module that decides what a recognized utterance means right now — feature, checklist
answer, global command, confirmation, or idle miss — by a fixed precedence order over the
enabled voice features.
_Avoid_: recognition handler, command router

**Fuel check**:
The FO's periodic (and on-request) comparison of fuel on board with the SimBrief plan at the
last fix passed, ending in an estimated landing fuel against the planned figure. "Last fix
passed" is the start of the leg the aircraft is on; off every leg, the nearest fix behind.
_Avoid_: fuel report, fuel monitor

**Gross error check**:
The one-shot pre-departure comparison of the aircraft's weights with the final loadsheet and
of the FMS PERF TO entries with the last Takeoff performance result — "checked" or a list of
mismatches. Once per loadsheet edition.
_Avoid_: performance cross-check, weight check

**Destination weather watch**:
The in-flight module that speaks a destination METAR change — a limit crossed (visibility,
ceiling), a new ATIS letter, a tailwind on the planned runway — as an edge, rate-limited per
trigger, never a repeat of a steady state.
_Avoid_: weather alerts, METAR monitor

**Read-back**:
A captain's statement of a figure ("altimeter one zero one three") that the FO checks against
the aircraft and answers "checked" or corrects. A lead-in with no figure ("minimums check") is
not a read-back; it belongs to the checklist or callout feature.
_Avoid_: confirmation, challenge

### Notifications

**Milestone**:
One of the ten flight moments the app can announce outward (refuel complete, boarding
complete, final loadsheet sent, ready for pushback, cabin secure, deice holdover expiring, top
of descent approaching, landed, on blocks, deboarding complete). Fires once per flight cycle.
_Avoid_: trigger, alert

**Target**:
One place a milestone is sent to — an ntfy topic, a Discord webhook or a generic JSON
webhook — with its own event switches. Its URL and token are secrets.
_Avoid_: channel, endpoint, subscriber
