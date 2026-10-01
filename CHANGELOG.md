# Changelog

## 0.5.0-rc.12

Replaces 0.5.0-rc.11 (same fixes) and adds two features.

- New: **Approach Gates** card on Settings → Voice First Officer → Callouts & Placards. Airline SOPs differ, so the stable-approach gates are no longer `settings.json` only: per gate the height, gear down required, minimum flap lever, speed band around VLS, maximum sink rate and whether a pass is spoken; add a gate (a 1500 ft gate, say), remove one, or reset to the 1000 / 500 ft defaults. The card also holds the master switch, the stable / unstable wording and the optional thrust check. Gear Limits gains the "gear still down" reminder switch and its height. The gear speed limits stay where they were; "gear up" on the ground is still always refused.
- New: **ElevenLabs voices** are in this build (the 0.5.0-rc.10 work, never published before) with a step-by-step setup guide: docs/manual/06-elevenlabs.md. Not yet run against a real key by the developer — reports welcome.
- Everything in 0.5.0-rc.11 below.

## 0.5.0-rc.11

Fixes from a tester's diagnostics bundle (0.5.0-rc.9, EDDN, ground only). 0.5.0-rc.10 is the ElevenLabs TTS build on its own branch; it was never published and is not part of this release.

- Fix: the Speech settings page (`/speech`) threw on every render (#142). Two Gear Limits hints were written with backslash-escaped quotes, which Razor does not support — the word "Gear" reached the number field as a parameter. A source-scan test now fails the build if the pattern returns. Probe speech-settings-render-crash.
- Fix: "confirm fuel" (voice, the CONFIRM FUEL button, the API) and a direct refuel request no longer order the GSX truck on a tankered aircraft (#143). The on-demand path now applies the same tankering rule as the departure sequence (`gsx.skipRefuelOnTankering`, FOB within 25 kg of the plan or above it): the request answers "Refueling is not needed", the figure is still confirmed, and the preliminary loadsheet is still produced. If GSX runs a refuel anyway (called from the GSX menu), the ground crew no longer announces "refueling complete" for fuel that never moved. Probe refuel-tankering-preskip extended.
- Fix (support reducer): a session that carries mirrored log events no longer lists every warning a second time from the CMTrace file, and Blazor circuit ids / Kestrel request ids collapse so one fault is one row with a count.
- Watch: catering at a jetway stand never opened door 1R in the bundle and one run stalled for 36 minutes (#144, needs a wire trace). New watch probe catering-1r-door-toggle. GSX's new `prompt` state key is recorded as known, not yet consumed.

## 0.5.0-rc.10

- New: ElevenLabs voice provider (design note docs/integrations/elevenlabs-tts-provider.md). Chain is now Kokoro → ElevenLabs → Google → Windows. Settings → Voice First Officer → ElevenLabs: paste your key (DPAPI-protected, never logged), **Fetch voices** to pick a premade voice, model (Flash v2.5 default, v3 optional), output format (MP3 works on every plan and is decoded locally; PCM 24 kHz needs a paid plan), stability / similarity / speed, a monthly character budget (9,000 default, plan-relative) and a per-request cap (2,500, the Free-plan limit) both checked before any call, text normalisation off by default, and a usage-this-month read-out. Usage is counted in `cache/tts/usage.elevenlabs.json` (Google's `usage.json` is untouched). A rejected key (401) parks the provider until settings are saved again. Crew role voices must be ElevenLabs voice ids while it is the active provider. Unverified live.

## 0.5.0-rc.9

- Fix: checklist confirm callouts accept `{fuel}` as an alias of `{fuelQuantity}` (#129) — on the 2026-09-19 flight the FO read "Fuel quantity, {fuel}, loaded" aloud because the owner's checklist used the short form. Any brace word the app does not know is now stripped (logged once) instead of being spoken.
- Housekeeping: #10, #43, #45, #54, #76, #110, #116, #131, #132 closed on flight evidence; their probes moved to regression watch.

## 0.5.0-rc.8

- Changed: the header voice trigger is now one pill with the mic inside it (owner pick "B2" from the canvas options sheet, #136): READY when the FO's ear is idle, LISTENING with a pulsing cyan dot while the recognizer captures, PAUSED on the ear-off latch. The rc.6/rc.7 "VOICE / OFF" block read as voice switched off. The drawer's head pill uses the same three words.

## 0.5.0-rc.7

- Fix: exact voice phrases now win over the value parsers (#137). "tune the ils", "say v speeds", "altitude star", "one hundred knots" and the commands.json FCU presses ("autopilot one", "arm approach") reached the radio/FCU parsers first and died with "Say again — couldn't read the …"; the router now sends text that IS a known phrase (global checklist command, an enabled feature's phrase, a drill trigger or a checklist start) straight to its owner. Free-form instructions ("set heading one two zero") still go to the parsers first. Probe exact-phrase-precedence.

## 0.5.0-rc.6

- New: Voice Reference drawer (#136) — a VOICE button in the header on every page opens a "what can I say?" panel: five tabs (FO · Ground · Cabin · ATC · Lists), every phrase the FO, ground crew, purser and SayIntentions ATC understand, alternatives joined by *or*, what happens and what comes back, badges for pilot-flying / INT / CAB / phase / value / dialogue, the live listening state and PTT binding, a search across all tabs and an "always available" box. The content is built live from the recognition grammar (registered voice features, checklist starts, drills, commands.json, atc-requests.json), so switched-off features show dimmed with the setting to flip and the list can never go stale. `?voice=1` opens it on load. Probe voice-reference-drawer.

## 0.5.0-rc.5

- New: NEXT SERVICE button in the EFB header (#133) — opt-in on Settings → Appearance; names the next departure service and its hold reason, calls it on a click (the INT/RAD smart button on every page). Off by default.
- New: the "cabin secure" report waits a random while after doors closed + beacon on (#134): `cabin.cabinSecureMinDelaySeconds` (45) plus up to `cabinSecureSecondsPerPax` (1.0) per passenger on board, drawn once per flight. Flight Status shows a "Cabin: securing/secure" pill; a "cockpit to cabin" hail meanwhile gets the new `cabinSecuringReplyText`. Session event `cabin.secure-armed`; probe cabin-secure-delay.
- New: live GSX menu card (#135) on Flight Status, the OFP pushback card and Ground Services — every menu GSX has open as buttons (an addon airport's named pushback directions, Customize, operator lists), with Open/Close. Picks go through the intent executor: the line is re-matched at send time, greyed lines stay disabled, and the card refuses while the automation answers the same menu. Decisions `web menu pick|open|close`; session event `gsx-menu-card`; probe gsx-menu-card-pick.

## 0.5.0-rc.4

- Fix: the app crashed at a ProSim reconnect when simulator.time arrived as a local-kind date (sim clock threw ArgumentException; the Monitor page's timer thread terminated the process). The sim clock now degrades to "not live" instead of throwing, and the Monitor tick and the loadsheet STD tick are guarded. Probe: sim-clock-local-kind-crash.

## 0.5.0-rc.3

- New: every session file and the CMTrace log are version-stamped (session-started/session-rotated header payload, startup banner).
- New: warnings and errors are mirrored into the session file as log.warning / log.error / log.fatal (logging.mirrorToSession, Logs page).
- New: "Export diagnostics" support bundle on the Logs page (/api/diagnostics/bundle) with redacted settings, hash manifest and a "Copy version" button.
- New: tools/ProsimCompanion.Reduce support reducer CLI + docs/agents/support-bundle.md; nine probes gained machine-checkable rules.
- Fix: the Advanced settings tab opened a 404; it now lands on Flight Phase Engine.

## 0.5.0-rc.2

- Fix: gradual ground-equipment removal pulled the GPU immediately after placement at cold and dark (external power not yet on). Ground prep now logs waiting/stalled stages.
