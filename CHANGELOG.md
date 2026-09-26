# Changelog

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
