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
