# Changelog

## 0.6.0-rc.16

From the EFHK→LKPR flight of 2026-10-05.

- New: **wrong-frequency report** for SayIntentions. SayIntentions never objects when COM1 is on the wrong station — it lets you (or its own copilot) check in with whoever answers. The FO now says once *"Captain, we should be on Tallinn Control, one three four decimal three two five"* when COM1 has stayed off the frequency ATC last gave you (its *"Contact … on …"* instruction) for 60 seconds. It runs from pushback to taxi-in with the SayIntentions copilot on or off, is quiet at the gate, and says nothing when it cannot read the instruction. Settings → Voice First Officer → SayIntentions: **Wrong-frequency report** (on) and **Wait before the report** (60 s). Session events `sayintentions.frequency-assigned` and `fo.wrong-frequency-advisory`; probe `si-wrong-frequency-report`. Not yet verified live: hand-off wording on arrival and in FAA phraseology.
- Fix: **Fuel Log times follow the simulated clock.** Every fix after the first row was stamped with the PC clock while the takeoff and the planned times were on the sim clock — on a flight flown at 03:54Z sim / 19:44Z real each row read 948 minutes late. The fuel figures were always right.
- Fix: the FO fuel check uses the same clock — the time shown in the Fuel Log's *FO checks* strip, and the fallback estimate (flow × time to the ETA) used when there is no route position.
- Changed: the plane on the Flight Monitor route strip **stays on the line**. It was drawn at its distance from the direct route on the route's scale, so an airway a few miles off the great circle put it a few pixels below the line and it looked misdrawn.
- Everything in 0.6.0-rc.15 below.

## 0.6.0-rc.15

Code-scanner clean-up (Snyk Code, 2026-10-04). Nothing changes in how the app flies.

- Security: the support reducer (`tools/ProsimCompanion.Reduce`) takes `--bundle`, `--out` and `--probes` only from below the working folder or the system temp folder. It finds each one on disk from that folder down and uses that entry, never the typed text, and it removes line breaks from everything it echoes. A path anywhere else is a usage error (exit `1`, *path refused*); the bundle, the probe catalog and the folder for `--out` must exist. Run the tool from the folder that holds the bundle.
- Security: `tools/build-atlas.js` writes the atlas to its one fixed place and takes no output argument.
- Housekeeping: lookup names the scanner read as credentials (`key` for an ini entry, a compass point, a flow-check id, a phrase pool) are renamed — no behaviour change. The test suite makes its stand-in API keys at run time.
- Everything in 0.6.0-rc.14 below.

## 0.6.0-rc.14

Stand knowledge and pushback direction, from the EGLL→EFHK flights of 2026-10-03/04 (owner Options A + B and pushback Option 1).

- Fix: **the arrival gate is found by its scenery identity** (#156). "W40" was refused four times at EFHK because the GSX profile prints that stand as *"Gate 40"* — a profile's name template expands the number and suffix but never the gate letter. The app now reads the destination's **GSX profile** (`.ini` stand data and `.py` names, from `%APPDATA%\Virtuali\GSX\MSFS` and from inside the scenery package) and the **simulator's own parking list** (SimConnect facility data, default airports included), resolves the typed gate to the stand (GATE_W 40) and sends its **number** first, then GSX's name, then your text. Session event `gsx-gate-resolved`.
- New: as you type the arrival gate on the OFP page the line below says what GSX calls it — *GSX knows it as Gate 40 (Apron 1W (Gates W34-W48)) · jetway · max span 65 m · VDGS*.
- New: **Stand Knowledge** (Settings → GSX → Arrival): on/off, the GSX profile folder, the scenery package folders (auto-detected from the Store/Steam installs and `UserCfg.opt`).
- Changed: **pushback direction is decided per flight.** Tell the FO what ATC said — *"push back facing north"*, *"tail left"*, *"tail right"*, *"straight back"* — or press a Korry button on the OFP page (now for this flight only, with an **Auto** button). Ask *"which way is the pushback"* any time.
- New: **Auto** mode (the new default of `gsx.pushbackPreference`): the app works out which push faces the departure runway — from the stand's GSX profile routes, the stand heading and the runway threshold (ProSim gateway, then Navigraph DFD; runway from the FMS, then the OFP) — shows *Suggested: TAIL RIGHT … for runway 22L* on the OFP page and applies it when confident. When unsure the FO asks *"Pushback — tail left or tail right?"* (the stand's own labels when it has them) and GSX's menu waits for your answer. **Ask** mode always asks; the three fixed answers still work and an existing `straight`/`tailLeft`/`tailRight` setting keeps the old behaviour.
- Changed: custom-labelled stands (*"Facing SW on Taxi AV"*) are matched by the profile's label, not by a fixed menu slot.
- Docs: `docs/integrations/gsx-profiles.md` (profile formats, locations, the `#`/`§` rule, facility data, advisor rules); probes `gate-resolved-by-identity` and `pushback-direction-advisor`. Not yet verified live: the facility-data field list and the BIAS_X/BIAS_Z axis convention (the log says *Parking positions at … median offset*).
- Everything in 0.6.0-rc.13 below.

## 0.6.0-rc.13

Fixes from the owner's EGLL→EFHK flight on 0.6.0-rc.8 (2026-10-03).

- Fix: **the debrief's fuel figures** (#155). "We burned 1.7 tonnes, landing with 5.1 tonnes" on a five-tonne flight: the debrief took its fuel from the first and last cruise fuel check. The app now records the fuel on board at off-blocks, takeoff, landing and on-blocks (in the `flight-times` session event), and the debrief says *"Fuel on blocks … About … used from takeoff."* Flights recorded before this build have no stamps and the debrief says nothing about fuel for them.
- Changed: **arrival gate diagnostics** (#156). Every refused `gate.select` now records what GSX's parking list held — airport, how many parkings, the nearest names and a sample — in the log and the session event `gsx-gate-not-found`, so the next refusal explains itself.
- New: **Answer GSX's position menu with the arrival gate** (#156, Settings → GSX → Arrival, off by default). When GSX itself asks *Select Position at …* after landing while your arrival gate is still unselected, the app picks the facility group whose range covers the gate (*Apron 1W (Gates W34-W48)*) and then the row that names it. Text match only, two tries, the menu is left for you on any miss. Not yet verified live — every attempt is in the session log with the rows GSX showed.
- Manual: the in-flight airport pick, the GSX "no menu in flight" workaround (open the GSX toolbar menu once after takeoff, #141) and the new switch.
- Everything in 0.6.0-rc.12 below.

## 0.6.0-rc.12

- New: **Fuel Log** tab (#154, owner request in flight) — the paper OFP's fuel column. **Plan vs actual**: fuel on board against the OFP's fuel at your exact point on the navlog, the delta with the last three fixes, the estimated landing fuel (planned landing fuel moved by the delta — the First Officer's own rule) and a holding / gaining / losing pill; **Fuel check now** makes the FO speak a check, **Open OFP** jumps to the plan. A **burn chart** (plan line, actual dots, planned landing line, where you land at the present delta). The **waypoint table** fills in as each fix is passed — planned and actual time, minutes early or late, planned and actual fuel, the difference; nothing is stamped before takeoff, the first row is the takeoff fuel at the takeoff time, a new OFP restarts the log. The **FO fuel checks** strip lists every check spoken this session with the exact words. Needs an OFP with a navlog; session only. Each stamped fix is a `fuel.log.fix` session event.
- Changed: the First Officer's fuel check and the Fuel Log page share one "where are we on the navlog" rule, so their figures agree.
- Everything in 0.6.0-rc.11 below.

## 0.6.0-rc.11

- New: **"What are we flying over?"** for Ask the First Officer (#153, owner request in flight). A third switch on the card, off by default. Ask *"what are we flying over"*, *"where are we"*, *"what is that city on the left"*, *"what country is this"* — no lead-in word, no minimum length, works with small talk off. The FO answers at once from an **atlas built into the app** (no internet): the country and which part of it, the mountains, desert or sea below, and the nearest notable towns with distance and side — *"We're over the Alps in Switzerland, about 30 miles north of Turin. Nearest town is Aosta, 12 miles out on the left."* The position line never comes from the language model. Then the model adds a fact or two about those places (small-talk rules: numbers unverified, nothing about this flight).
- New: **Wikipedia facts** (a fourth switch, only with the one above): the FO takes those facts from a short Wikipedia summary of the nearest town instead of the model's memory — one small request per place, three-second limit, cached; on failure the model's own facts. Only the place name is sent.
- Atlas data: Natural Earth 1:110m countries and 1:50m seas and regions (public domain), GeoNames towns of 25 000 and up plus capitals and first-order seats (CC BY 4.0). "Left" and "right" are relative to the track. Very broad regions (the North European Plain) are not named on purpose.
- Everything in 0.6.0-rc.10 below.

## 0.6.0-rc.10

- Changed: the Flight Monitor's route-strip plane **always points at the destination** for the whole journey (owner request 2026-10-04). It used to turn with your track, so at the gate it pointed wherever the stand faced and read as flying away from the route. The track no longer turns it.
- Everything in 0.6.0-rc.9 below.

## 0.6.0-rc.9

Fixes from the owner's gate report on 0.6.0-rc.8.

- Fix: **"Sync to FMS (FIN)" wrote the wrong block fuel.** The MCDU got the ordered figure (8.4 t) while the final loadsheet carried the fuel actually aboard (9576 kg → 9.6 t). The block now comes from the loadsheet the ZFW comes from (final or prelim); the ordered figure stays the fallback for the live source. Test added.
- Changed: the W&B page's big MACZFW / MACGW figures are labelled **LIVE** (ProSim's live datarefs) and show the loadsheet's figure beside them ("loadsheet FIN 27.6%") — the loadsheet's MACZFW is ProSim's own loadsheet relation, matches ProSim's EFB, and is what goes to the MCDU; the live dataref can read a little different.
- Everything in 0.6.0-rc.8 below.

## 0.6.0-rc.8

Flight Monitor board, round three (owner requests in flight on 2026-10-03).

- New: the board's clock is a **split-flap display** like the header's — amber on black in every theme, flipping on the minute when Solari animation is on.
- Changed: the route-strip marker is a **plain top-down airliner outline** — one closed line traced from the owner's reference drawing — instead of an arrow; it still turns with your track.
- Fix: **text on the page colour is readable on every theme** — Finnair showed the card's dark text on its blue page (clock, route, GATE CLOSED, EET/FOB/ETA unreadable). Themes now carry a "text on page" colour family; the white cards keep their own.
- Fix: **the ATIS letter now moves with the flight.** SayIntentions weather (the source of the ATIS letter) was fetched once at session start and never again; the weather refresh now asks it to update first, every cycle (its own 10 min cache applies). The METAR itself was already re-read every ten minutes.
- New: each board weather tile shows **"updated n min ago"** beside the METAR time, so you can see the cards are live.
- Everything in 0.6.0-rc.7 below.

## 0.6.0-rc.7

- New: **small talk and fun facts** for Ask the First Officer (#152, owner request in flight). A second switch on the same card, off by default. With it on, ask anything — "who is better, Chelsea or Arsenal", "tell me a fun fact", "what is the capital of Peru" — with no lead-in word: any sentence of four words or more that matched nothing else goes to the FO. A question that mentions the flight (fuel, weights, speeds, altitude, runway, weather, times, passengers…) still takes the strict path: fact sheet only, every number verified. On the small-talk path the FO answers from general knowledge, in character, one or two light sentences; numbers there are trivia and not verified, but a sentence about this flight with a figure in it is refused and the fixed line is spoken. "Let me check." hands a question back to the strict path. Still speech only, still silent in the sterile cockpit. Session events carry `mode: flight | chat`.
- Everything in 0.6.0-rc.6 below.

## 0.6.0-rc.6

Fixes from the owner's gate test on 0.6.0-rc.5.

- Fix: **Ask the First Officer answers were cut off after 6 seconds** (#149). The time budget is meant to cover the wait for the first spoken word; it kept running after speech began and cancelled every answer mid-sentence (three answers, all dropped at 6.0 s). The budget now stands down at the first word and the answer plays out. Test added.
- Fix: the **Flight Monitor and Flight Status gate strip now show the stand GSX reports** ("Gate 311") when no gate request was confirmed — the departure anchor's `gate.select` fails by design, so the board read "GATE —" through the whole turnaround.
- Fix: the **Finnair theme hid the BOARDING word** on the Flight Monitor (its accent is the same blue as its content background). Every theme now gets a contrast-guarded `--accent-readable` colour for the boarding word and bar: the accent when it reads at 3:1, else the accent pushed away from the background, else the gold accent.
- Changed: the Flight Monitor weather tiles read **CEILING 2,500 FT / NO CEILING** instead of the METAR shorthand "CIG".
- Everything in 0.6.0-rc.5 below.

## 0.6.0-rc.5

- Fix: the black band under the footer in the iPad home-screen app (rc.1–rc.4). The see-through status bar made iPadOS hand the app a web view one status-bar short (screen 820, window 788, safe-top 32 on the owner's iPad); the status bar is now opaque black, so iOS paints it and the view is the right height. **Delete the home-screen icon and add it again** — iOS freezes these settings when the icon is added.
- Everything in 0.6.0-rc.4 below.

## 0.6.0-rc.4

- New: a **Viewport** line on Appearance → Screen & Install (window size, screen size, safe-area insets, display mode, shell height) — the facts for the iPad home-screen band report. Note: iOS freezes a home-screen app's settings when the icon is added; after an upgrade, delete the icon and add it again.
- Everything in 0.6.0-rc.3 below.

## 0.6.0-rc.3

- Fix: in the home-screen (installed) app on the iPad the footer floated above a black band at the bottom of the screen (0.6.0-rc.1 screenshot). iPadOS under-reports the dynamic viewport height in standalone mode; the shell now fills the window there and the page canvas wears the theme colour.
- Everything in 0.6.0-rc.2 below.

## 0.6.0-rc.2

Keep-awake diagnostics after the first iPad test (the screen still slept over HTTPS).

- Changed: the screen wake lock is **re-requested on your first tap** on any page (iPadOS Safari can refuse a request made at load, before any touch), and on focus / page show.
- New: a **Lock diagnostics** line under Keep the screen awake — requests made, when the lock was granted, when the browser let it go, and the text of the last refusal — plus a **Request again** button. Send that line with a report.
- Docs: the switch is stored per address (`http://…:5320` and `https://…:5321` are different), and iPadOS ignores every wake lock in Low Power Mode — said on the card and in manual chapter 7.
- Everything in 0.6.0-rc.1 below.

## 0.6.0-rc.1

The October 2026 batch (#145–#151): seven features, one issue each, every one off by default
except the Logbook page and the flight-progress figures. Every item below is **unverified
live** — the test steps for the next flights are on each issue. Also includes everything in
0.5.0-rc.12 (ElevenLabs, Approach Gates, the EDDN fixes).

- New: **aircraft position and distance-based flight progress** (#145). The app now reads the aircraft's latitude and longitude from ProSim and locates the flight plan's airports (ProSim gateway first, your Navigraph database as the fallback). The pop-out Flight Monitor replaces its time-based bar in flight with a **route strip** — the origin → destination great circle with the aircraft marker on it, turned by its track, and a tick at the estimated top of descent — plus **distance to go**, a **ground-speed ETA** and **minutes to top of descent**. Flight Status gains the same figures on a small row under the telemetry. Distances are great-circle direct, not along the route, and are labelled that way; the top of descent is a 3:1 estimate and says so. With no position or no airport coordinates every figure falls back to the old time-based value and the caption tells you. A once-per-flight "top of descent approaching" moment is recorded 10 minutes ahead (Settings → Display & Flight Data → Flight Status card → Top of descent notice; 0 = off) — nothing is spoken yet. Flight recordings carry the position. Unverified live. Probes flight-progress-position, tod-approaching-once.
- Fix: the Flight Status card appeared twice on Settings → Display & Flight Data.
- New: **Logbook page** (#146) — a Logbook entry in the sidebar. Totals (flights, block hours, landings, stable-approach rate, average touchdown rate), a touchdown-rate trend chart of your last landings, a table of every flight you can sort by any column, and a detail panel per flight: off-blocks / takeoff / landing / on-blocks times, runways, lift-off speed, touchdown rate, IAS, ground speed, pitch and bounces. **Delete flight** takes two clicks and a deleted flight does not come back. **Export CSV** downloads the whole logbook (UTC times, decimal points whatever your Windows region is). **Read saved sessions** adds flights that are still in the session files but not in the logbook.
- New: **landing analysis** (#146). The app now measures each landing: the touchdown rate is the lowest vertical speed in the second before the wheels touch (not the already-flared reading at contact), with IAS, ground speed, pitch, bank and a bounce count (wheels off and on again within five seconds). A touch-and-go or a go-around after touchdown is recorded separately and does not count as your landing. The debrief gains one line — "Touchdown at minus 180 feet per minute" — and the LLM-styled debrief is held to the same figure by the number verifier. Flights flown before this build show a dash. Unverified live. Probes touchdown-recorded, touchdown-once-per-landing.
- Watch: `aircraft.acceleration.Y` is recorded raw on the touchdown event for a future vertical-G figure. Its unit is undocumented, so nothing converts or shows it yet.
- New: **the FO speaks LLM briefings and the debrief while the model writes them** (#147). The first words come after the model's first sentence instead of after the whole reply. Every sentence is checked against the facts before it is spoken — and numbers the model writes as words ("heading one six three") are now checked too; before, they slipped past the check. If a sentence has a wrong number, or the model stalls or fails, the plain template finishes the briefing from where the model left off — nothing is said twice from the top. A safety callout (minimums, V1, master warning) cuts a briefing for good; "brief the arrival" again gives a fresh one. "Positive climb"-type callouts no longer wait behind a whole briefing: they are spoken between two of its sentences. Switch: Settings → Voice First Officer → LLM Styling → **Speak while the model writes** (`briefing.streamLlm`, on). Model sentences are not written to the TTS cache. Unverified live. Probes llm-stream-takeover, llm-stream-never-restarted.
- Changed: with streaming on, the one strict re-ask of the whole-reply path does not happen (it would cost the latency streaming removes); the template takes over instead. The whole-reply path (switch off) is unchanged.
- New: **in-flight First Officer monitoring** (#148) — four independent modules, each with its own switch on Settings → Voice First Officer → Callouts & Placards → **In-flight monitoring**, all **off by default**, all armed by "Flight live". **Fuel check**: in the cruise, every N minutes (default 30) and on "fuel check", the FO names the last flight-plan fix passed, the fuel on board, the difference from the SimBrief plan at that point and the estimated landing fuel against the planned figure; a shortfall beyond the margin is spoken with priority. Without a navlog or position it falls back to burn × time to the ETA. **Gross error check**: once the final loadsheet is sent and the FMS PERF TO page is filled, the FO compares the aircraft's zero fuel weight and fuel against the loadsheet and the FMS flaps / flex / V1 / VR / V2 against the last Takeoff performance result — "checked", or each mismatch by name; once per loadsheet edition, also on "gross error check". **Destination weather watch**: from the cruise, when the destination METAR changes — visibility or ceiling crossing your thresholds (either way), a new ATIS letter, a tailwind appearing on the planned runway — the FO says so, with the alternate's weather when the destination has dropped below a limit; rate-limited per trigger. **Read-backs**: say "altimeter one zero one three", "V speeds one four one, one four four, one four seven", "runway two seven right" or "minimums four one zero" and the FO checks the figure against the aircraft (EFIS baro, FMS speeds, briefed runway, briefed minima) and answers "checked" or the value it reads. The SimBrief import now keeps the navlog (fix positions, planned fuel and time). Phrases appear in the VOICE drawer. Unverified live. Probes fo-fuel-check, fo-gross-error-check, fo-destination-weather-watch, fo-readback.
- New: **ask the First Officer** (#149) — free-form questions, answered by the language model from what the app knows right now and nothing else. Say "what is our fuel on board", "how long to top of descent", "tell me the destination weather", "are we above the minimum takeoff fuel" — any utterance that starts with one of the question lead-ins, is at least four words long, and matched no exact phrase. The FO answers in one or two sentences from a live fact sheet (phase, fuel and plan figures, weights, distance / ETA / top of descent, block and flight times, the weather cards, the briefed minima, the OFP basics, open tech-log items); every number is checked against that sheet, a failed check earns one strict re-ask, and after that the FO says "I don't have a verified answer for that". "Stand by" after 2 seconds of silence; 6 seconds to the first spoken word, then the fixed line. The answer is only ever spoken — it is never read as a command. In the sterile cockpit a question is heard but not answered. Needs the LLM and the LAN speech server (the offline Windows engine hears exact phrases only). **Off by default**: Settings → Voice First Officer → Briefings & LLM → Ask the First Officer; example questions in the VOICE drawer. Unverified live. Probe fo-question-answer.
- New: **the app on a tablet — installable, and a screen that stays awake** (#150, ADR-0013). A web manifest and home-screen icons: on the iPad, Safari → Share → Add to Home Screen gives a full-screen Companion app (no browser bars); Android / Chrome offers "Install app". No service worker and no offline mode — the app is a live link to the sim PC. **Keep the screen awake**: Settings → Appearance → Screen & Install, a per-device switch (stored in that browser) with a live pill — ACTIVE, OFF, NEEDS HTTPS, NOT SUPPORTED. Both need a secure page, so the web server gains an **optional HTTPS address** beside the normal one: Settings → Setup → Web Interface → HTTPS, your own certificate file (PFX) and its password (stored encrypted), port 5321, off by default. The app never makes or installs certificates; chapter 7 of the manual walks through mkcert and trusting the CA on the iPad. A missing, unreadable or wrong-password certificate means HTTP keeps working and a banner says why; an expired one still answers with a warning. The desktop window's QR code carries the HTTPS address when it is up. The manifest and icons are reachable without the sign-in token (they carry no data); everything else keeps it. Unverified live. Probe web-https-listener + the keep-awake steps on the human checklist.
- New: **notifications on your phone** (#151). Settings → Notifications: switch it on, add a target — an **ntfy** topic, a **Discord** channel webhook, or any **webhook** that takes a JSON POST — tick the milestones it should get, and press **Send test**. Milestones: refuel complete, boarding complete, final loadsheet sent (again for a revised edition), ready for pushback, cabin secure, deice holdover expiring, top of descent approaching, landed, on blocks, deboarding complete — each once per flight, with the flight number, the route and the UTC time. URLs and tokens are stored encrypted and never logged (the log and the diagnostics bundle name the target and the HTTP status only). Sends run in the background with a five-second limit; a failing target is left alone for a minute; a slow phone service never delays the ground automation or the First Officer. Off by default. Payload schema and an ntfy walk-through in docs/integrations/notifications.md. Unverified live. Probe notify-delivery.

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
