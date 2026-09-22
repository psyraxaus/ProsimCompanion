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

### Speech

**Spoken text**:
The TTS-ready rendering of an aviation identifier — runway, airport, waypoint, frequency,
altitude — including the fallback when the friendly form is unknown (an unknown airport is
NATO-spelled, never letter-spelled).
_Avoid_: phrasing, pronunciation helpers

**Utterance router**:
The module that decides what a recognized utterance means right now — feature, checklist
answer, global command, confirmation, or idle miss — by a fixed precedence order over the
enabled voice features.
_Avoid_: recognition handler, command router
