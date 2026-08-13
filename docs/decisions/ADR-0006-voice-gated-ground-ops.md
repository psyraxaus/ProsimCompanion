# ADR-0006: Voice-gated ground services ride the existing command path; crew speech rides the arbiter roles

**Status:** Accepted (2026-08-14, project owner decision) · Design: `docs/voice-ground-ops-design.md`

## Context

The owner wants ground operations gated behind spoken crew calls ("Cockpit to ground" →
"Go ahead, captain" → "Request refueling") with audible, accent-localized crew responses,
and positive FO feedback for SayIntentions ATC requests. Several architectures were
possible: a parallel voice-driven service controller, a scripted dialogue engine with its
own GSX access, or extending the existing seams.

Relevant existing seams: the `CommandRegistry` single-writer path (every GSX trigger —
web, API, Stream Deck, voice — funnels through `IGsxTriggerDispatcher`, one in-flight
trigger at a time); the speech arbiter with `SpeechRole` voices (FO/Purser/Company); the
purser's interphone pattern (chime → wait for ACP CAB receive latch or grace → speak); the
`IMicOwnership` guided-dialogue seam; and the departure sequencer's per-step activation
model (`Skip/Manual/AfterCalled/…`).

## Decision

1. **Voice gating is expressed inside the existing automation models, not beside them.**
   A `Voice` value in `GsxServiceActivation` (sequencer holds, any trigger advances) and a
   `gsx.groundPrepActivation` mode (prep chain holds until departure services start). No
   voice-only write path exists: hails and phrases dispatch the same named commands as
   every other surface, so voice inherits every guard, and every other surface bypasses
   the voice ceremony (voice is an *additional* trigger, never the only one).
2. **Crew feedback is arbiter speech in new roles, gated by the real ACP receive latches.**
   `SpeechRole.GroundCrew` on INT mirrors the purser on CAB (latch-or-grace, intercom
   filter, chime/MECH call). No sample assets; TTS + programmatic chimes only.
3. **No fabricated ATC.** The FO acknowledges and (optionally) audibly voices the
   transmission; readbacks remain SayIntentions' job. Fabricating clearances from data we
   don't have was rejected (predecessor precedent, and it clashes with live-ATC users).
4. **Accent localization is a per-role voice resolution concern**, applied to GroundCrew
   from an ICAO-prefix→locale table at synthesis time, degrading to the configured voice
   when the serving provider lacks the locale (Kokoro = US/UK only).

## Consequences

- The GSX pillar's invariants survive untouched: single serialized trigger, mirror-confirmed
  dispatch, degrade-not-fail (mic-less and headless setups use `auto` mode or the web
  button — nothing blocks startup).
- The phrase→command catalog is extracted to one shared table used by both single-shot
  phrases and hail dialogues; adding a service phrase is one line.
- The on-demand service path gains the flight-plan and sim-session gates the automatic
  sequencer already had — closing a real hole (voice "request refueling" before OFP import
  used to go straight to GSX).
- Google TTS becomes the accent workhorse; `speech.localOnly` silently narrows accents to
  US/UK — documented, not fought.
- Existing users lose "cockpit to ground" = start-services (it becomes the hail);
  "commence ground services" / "start ground services" replace it. Pre-1.0, no migration.
