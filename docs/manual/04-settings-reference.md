# Settings reference

All configuration lives in **`config\settings.json`** beside the app, camelCase, one section
per feature. The web pages cover the everyday sections; everything is also hand-editable —
missing keys fall back to safe defaults, and the app adds newly available defaults to the
file on start. **Never share your `webUi.accessToken`.**

**Secrets are encrypted per Windows user.** The API keys and the access token (`prosim.apiKey`,
`sayIntentions.manualApiKey`, `briefing.llmApiKey`, `speech.elevenLabsApiKey`,
`webUi.accessToken`) are stored as
`dpapi:…` values, protected with Windows DPAPI for the account that runs the app. They only
decrypt on that PC and that Windows account. If you copy `settings.json` to another PC or
account, those fields bind as empty, the log warns once, and the settings page shows a
"re-enter it and save" hint under each affected field — type the key again and save. A key
typed into the file by hand is accepted and encrypted on the next start. The access token
is regenerated automatically (pair tablets again via the QR code).

| Section | Page | Highlights |
|---|---|---|
| `prosim` | Settings → Setup | `sdkPath` (ProSimSDK.dll), `host`, optional `apiKey`; the **ProSim Setup Check** card (`setupCheckEnabled` on, `setupCheckIgnore[]` = the rows ticked "keep", by dataref name) — see [chapter 2](02-using-the-app.md#prosim-setup-check) |
| `webUi` | Settings → Setup / Display / Appearance, desktop window | `port` (default 5320), `bindToAllInterfaces`, `accessToken`, theme, units, Solari animation, `https` (`enabled` off, `port` 5321, `pfxPath`, `pfxPassword` encrypted — the optional secure address, see [chapter 7](07-tablet-install.md)), `showNextServiceInHeader` (off — the header NEXT SERVICE button) |
| `gsx` | Settings → Ground Services | the whole ground pillar — see below (master switch on Setup) |
| `audio` | Settings → Audio Control | backend (CoreAudio/VoiceMeeter), app mappings, ACP side, device blacklist, device-filter flow/state (master switch on Setup); mapping channels are the eight ACP knobs plus `loudspeaker` (the cockpit LOUD SPEAKER dial, captain and first officer — see [chapter 8](08-loudspeaker-dial.md)) |
| `speech` | Settings → Voice First Officer | TTS providers/voices (Kokoro, Google and Windows on Voice Providers; ElevenLabs on its own page — see the [ElevenLabs setup guide](06-elevenlabs.md)), output/input devices, recognition, PTT/ATC-mute bindings, track phrases the FO did not understand (`trackUnmatched` on), sterile cockpit (master switch on Setup); **Ask the First Officer** (`foQuestions`: `enabled` off, `leadIns` comma-separated, `minimumWords` 4, `smallTalk` off, `timeBudgetSeconds` 6, `standBySeconds` 2) on Briefings & LLM; **Region Facts** (`regionFacts`: `enabled` off, `minIntervalMinutes` 15, `maxIntervalMinutes` 30, `maxPerFlight` 4, `llmFacts` true — the curated file is `%LOCALAPPDATA%\ProsimCompanion\config\region-facts.json`) on the same page |
| `voices` | Settings → Voice First Officer | purser/company voices + chimes |
| `checklists` | Checklists | checklist sets, manual-override allowance |
| `sop` | Settings → Voice First Officer → Callouts & Placards (rest: file) | on the page: callouts master switch, flap placards, gear limits and the "gear still down" height, the **Approach Gates** card (stable-approach check, per-gate height / gear / minimum flap / speed band / sink rate / spoken pass, add and remove gates, wording, thrust check), placard advisories, the **In-flight monitoring** card (`sop.monitoring`: `fuelCheck` enabled / `intervalMinutes` 30 / `shortfallMarginKg` 300 / `onPlanWithinKg` 100; `grossErrorCheck` enabled / `automatic` / `zfwToleranceKg` 500 / `blockFuelToleranceKg` 300 / `vSpeedToleranceKt` 3 / `flexToleranceC` 2; `destinationWeather` enabled / `visibilityThresholdM` 1500 / `ceilingThresholdFt` 500 / `tailwindThresholdKt` 5 / `minutesBetweenAnnouncements` 15 / `announceAtisChange`; `readbacks` enabled / `altimeterToleranceHpa` 0.5 / `vSpeedToleranceKt` 1 / `minimumsToleranceFt` 10 — every `enabled` is off by default). File only: the individual callout texts and thresholds, the other flow-monitor reminders, weather advisories |
| `briefing` | Settings → Voice First Officer → Briefings & LLM (DFD path on Setup) | departure/arrival overrides, DFD path (`dfdPath`), minima capture, LLM endpoint (`llm*`), streamed LLM speech (`streamLlm` true); `llmWakeOnLan` is legacy — see `wakeOnLan` |
| `persona` | Settings → Voice First Officer → FO Persona | FO persona — enabled, name, experience, formality, chattiness 0–3, what it styles (briefings / debrief / advisories), varied acknowledgements, advisory restyle budget |
| `sayIntentions` | Settings → Voice First Officer → SayIntentions | API key source, phraseology, auto-tune, departure gating, the wrong-frequency report (`frequencyMonitorEnabled` true, `frequencyMonitorWaitSeconds` 60), **arrival gate from ATC** (`arrivalGateFromAtc` off — SayIntentions' assigned gate becomes the GSX arrival gate), weather |
| `cabin` | Settings → Voice First Officer → Cabin Crew | purser reports, the cabin-secure wait (`cabinSecureMinDelaySeconds` 45, `cabinSecureSecondsPerPax` 1.0, `cabinSecuringReplyText`), the FO's cabin-call auto-answer (`autoAnswerGround` off / `autoAnswerGroundDelayMs` 4000, `autoAnswerAir` off / `autoAnswerAirDelayMs` 2500), ambient events, the purser cruise query (`cruiseQuery` off, `cruiseQueryDelayMinutes` 10, `cruiseQueryWindowSeconds` 20, wording `cruiseQueryText` / `cruiseQueryEtaAckText` / `cruiseQueryRideAckText` / `cruiseQueryGenericAckText` / `cruiseQueryNoReplyText`), dings, interphone chime |
| `company`, `debrief`, `day` | Settings → Voice First Officer → Company & Debrief | company channel (auto loadsheet read, ACARS chime, cruise messages + probability, save the spoken loadsheet), post-flight debrief (on, full / brief, LLM styling), company day mode (auto track, start at first preflight, turnaround summary, post-flight allowance, auto-close idle, rotations folder, state file) |
| `flightData` | Settings → Display & Flight Data | SimBrief fetch attempts, prelim/final loadsheet triggers and delays |
| `flightStatus` | Settings → Display & Flight Data → Flight Status card | gate monitor final-call thresholds (`gateFinalCallPaxPercent` 90, `gateFinalCallMinutesBeforeStd` 10), weather-card refresh (`weatherRefreshMinutes` 10), top-of-descent notice lead (`todLeadMinutes` 10, 0 = off), the tile order of the Flight Status page (`tileOrder`, a list of `hero` / `sequence` / `sim` / `app` / `gsx` / `services`; empty = default; written by the page's Edit layout mode, a Reset button on the settings page); also drives the pop-out Flight Monitor |
| `flightState` | Settings → Advanced → Flight Phase Engine | phase-engine thresholds and settle times (calibration data) |
| `techLog`, `logbook` | Settings → Voice First Officer → Tech Log & Logbook | tech log on, offer entries after an abnormal, random wear, rectify on due date, open items in the briefings (`briefOpenItemsInBriefings` on), flag checklist lines (`flagChecklistItems` on), MEL repair days A–D, store / wear-pool file paths; logbook on + store path. The defects themselves are on the Tech Log page, the flights on the Logbook page |
| `abnormals` | Settings → Voice First Officer → Abnormals & Drills | `enabled` (detect + announce), `interactiveDialogue` (work the ECAM line by line), `confirmTimeoutSeconds` 20, `maxSilentPrompts` 3 (0 = ask forever), `maxVerifyRetries` 1. The procedures are JSON under `%LOCALAPPDATA%\ProsimCompanion\config\abnormals` |
| `wakeOnLan` | Settings → Setup → Wake-on-LAN | `targets[]` — one row per PC to wake at startup (name, on/off, MAC, broadcast, port) with a Send now button; the old `briefing.llmWakeOnLan` block is moved into the list on the page's first visit |
| `weather` | Settings → Setup → Weather Sources | ActiveSky snapshot path (empty = auto-detect), local API on/off, host, port, timeout |
| `notifications` | Settings → Notifications | `enabled` (off), `targets[]` (name, kind ntfy / discord / webhook, `url` + `token` stored encrypted, ten per-event switches), `failureCooldownSeconds` 60, `sendTimeoutSeconds` 5, `holdoverExpiringMinutes` 5 — payloads in [docs/integrations/notifications.md](../integrations/notifications.md) |
| `updateCheck` | Settings → Setup → Updates | update banner on/off + check interval (hours) |
| `logging` | Settings → Advanced → Logs | per-subsystem levels (hot-reload), `wireTrace` |
| `telemetryApi` | Settings → Advanced → Logs | read-only telemetry endpoints for post-flight verification |
| `commandApi` | Settings → Setup | HTTP command API for the Stream Deck plugin (`enabled` checkbox, applies live; off by default; token-gated even on localhost — `requireTokenOnLoopback` is file-only) |

## The `gsx` section

Ground behaviour, in the Ground Services rail order:

- **Automation** — master switch, auto-start departure services, wait-for-OFP gate
- **SimBrief / VDGS** — *Reload SimBrief in GSX on a new OFP* (`reloadSimbriefOnNewOfp`,
  off): clicks GSX's own "SimBrief" gate-menu line once per new flight plan so the VDGS
  and marshaller boards show the right flight number, destination and ETD — only while
  parked at the gate with engines off and GSX ready, never in flight. The Status section's
  *Reload SimBrief in GSX* button and the `gsx.reloadSimbrief` command do the same on demand
  whatever this is set to
- **Departure Service Order** — ordered steps, each with an activation rule (previous
  called/requested/active/completed, all completed, manual, skip), a leg constraint
  (always / first leg / turnaround / company hub / non-hub) and a minimum flight time
  (short hops skip the service)
- **Refuel** — fixed or dynamic rate (fixed fill time), tankering skip, finish-on-hose,
  defuel guard. *Skip refuel on tankering*: when the fuel on board already meets the plan
  (INIT override, else OFP block fuel, else the EFB planned fuel; 25 kg tolerance) the truck is
  not ordered — by the departure sequence, by "confirm fuel" (voice or the INIT button) or by
  a direct refuel request. The preliminary loadsheet is still produced, and the ground crew
  does not call "refueling complete". Switch it off to always order the truck
- **Gate & Doors** — per-door-class rules (pax doors follow stairs, service doors follow
  catering, cargo doors follow loading with keep-open choices, close-on-final) and the
  jetway/stairs lifecycle (connect at start / at departure / on arrival; stairs removal
  never/always/only-jetway; remove-on-final)
- **Ground Equipment** — GPU (with-APU option), PCA tri-state (never/always/only jetway) +
  override, arrival chock delays, gradual removal, reposition-at-start
- **Pushback** — default direction mode (`pushbackPreference`: **auto** toward the departure
  runway / **ask** the pilot / fixed tail left, tail right, straight — your own voice or
  Korry-button choice for the flight always wins), *ask when unsure*, the beacon-orchestrated
  sequence with randomized step delays, tug question answer, pushback-when-tug-attached
- **Questions** — FollowMe, crew boarding, pushback confirm, de-ice fluid, operator
  preferences, company hubs
- **De-Icing** — *Auto-accept de-ice offer* (GSX's own "Ice warning" question), the fluid
  and concentration (shared by GSX's offer and the app's own request), and the
  weather/OAT auto-request policy (`deice.autoRequest`: **off** default / **ask** / **auto**;
  `deice.oatThresholdC` 3 °C; `deice.requirePrecipitation` on). Once the departure services
  have started, the app reads ProSim's OAT (else the METAR temperature) and the departure
  METAR shown on the Flight Status weather card: at or below the threshold with snow,
  freezing precipitation, ice pellets, rain, freezing fog or a dew-point spread of 3 °C or
  less it either asks the captain (the FO: *"Captain, conditions call for de-icing — OAT −2,
  snow. Request it?"*; the Ground Services Status section shows the question with
  *Request de-icing* / *No de-icing* buttons; five minutes without an answer counts as no)
  or, on **auto**, puts the GSX `DeIce` service on the departure sequence as its last step.
  With no METAR for the departure the policy makes no request (no data, no call). Every
  verdict is on the Status section's De-Icing row and in the decision log
- **Arrival / Fuel** — auto-deboard, stable-parked hold time, FOB save/restore per aircraft
- **Stand Knowledge** — read GSX profiles and scenery for stands (`resolveGatesFromProfiles`,
  on), the GSX profile folder (empty = `%APPDATA%\Virtuali\GSX\MSFS`), scenery package
  folders (`;`-separated; empty = auto-detected Store/Steam locations + `UserCfg.opt`)
- **Pax** — no-show randomization, bag weight

Per-aircraft overrides: the Aircraft Profiles page stores a `gsxSettings` block per profile
and applies it when that aircraft loads.

## User-editable content files

These live in **`%LOCALAPPDATA%\ProsimCompanion\config`** — *not* in the install directory.
The copies beside the exe are only the shipped defaults: on every start the app seeds
missing files from them and refreshes any you have never edited, while your edited files are
always kept (so app updates cannot revert your work). The Checklists page shows the exact
folder and the time of the last reload — if you save an edit and that time does not move,
you edited the wrong copy.

| File(s) | Format | Hot reload |
|---|---|---|
| `checklists\*.json` + `sets\` | Prosim2FO / Prosim2GSX checklist formats | yes |
| `abnormals\*.json` | ECAM abnormal definitions | on start |
| `phrases.json` | acknowledgement phrase pools | yes |
| `region-facts.json` | curated cruise region facts, keyed by country code or `sea:<name>` | yes |
| `commands.json` | file-driven voice commands | yes |
| `atc-requests.json` | SayIntentions request phrasebook | on start |
| `themes\*.json` | airline themes | on switch |
