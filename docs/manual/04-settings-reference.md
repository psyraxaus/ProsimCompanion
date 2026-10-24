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

**The pages check what you type.** A host field refuses `http://` and ports, a URL field
wants `http://` or `https://`, and MAC, IP, ICAO and runway fields want their usual shape: a
wrong value turns red with the expected shape under it, and Save stays grey until it is
fixed. The bar names the field, so a red value in a section or fold you have switched away
from still holds Save. A path that is not on this PC turns amber but still saves (the file
may come later).
Empty is always allowed. Path fields show a **Browse…** button when you open the page in a
browser on the sim PC itself (localhost or that PC's own address): it opens the Windows file
or folder dialog on that PC's screen. A tablet or another PC gets no button, because the
dialog would open where you cannot see it; type the path instead. The dialog opens in the
folder of the current value, else where the app expects that file (for example
`%APPDATA%\Virtuali\GSX\MSFS` for the GSX profile folder, `%ProgramData%\ProSim-AR\Navdata`
for the DFD database), else the system drive.

| Section | Page | Highlights |
|---|---|---|
| `prosim` | Settings → Setup | `sdkPath` (ProSimSDK.dll), `host`, optional `apiKey`; the **ProSim Setup Check** card (`setupCheckEnabled` on, `setupCheckIgnore[]` = the rows ticked "keep", by dataref name) — see [chapter 2](02-using-the-app.md#prosim-setup-check) |
| `webUi` | Settings → Setup / Display / Appearance, desktop window | `port` (default 5320), `bindToAllInterfaces`, `accessToken`, theme, units, Solari animation, `https` (`enabled` off, `port` 5321, `pfxPath`, `pfxPassword` encrypted — the optional secure address, see [chapter 7](07-tablet-install.md)), `showNextServiceInHeader` (off — the header NEXT SERVICE button), `language` (empty = follow each browser; else a pack code `en`, `de`, `es`, `fr`, `ja`, `ko`, `nl` — Settings → Appearance → Language; packs are the folders in `%LOCALAPPDATA%\ProsimCompanion\config\lang`, one per language with one JSON file per page, the `en` folder is the source) |
| `gsx` | Settings → Ground Services | the whole ground pillar — see below (master switch on Setup) |
| `audio` | Settings → Audio Control | backend (CoreAudio/VoiceMeeter), `requireAcpPower` (on: knobs apply only with the panel powered; off: always, like the remote mixer), app mappings, ACP side, device blacklist, device-filter flow/state (master switch on Setup); mapping channels are the eight ACP knobs plus `loudspeaker` (the cockpit LOUD SPEAKER dial, captain and first officer — see [chapter 8](08-loudspeaker-dial.md)) |
| `mixer` | Settings → Audio Control → Remote Mixer | Voicemeeters on other PCs through VoicemeeterBridge agents: `enabled` (off), `agents[]` (one block per PC: `name`, `enabled`, `host`, `port` (5088), `token` (encrypted), `panelStrips` / `panelBuses` (0-based, shown on the Status card)), reconnect delays, `setTimeoutMs`, `gainMinDb` / `gainMaxDb`, `mappings` (panel + channel → `agent` name + strip/bus, `useLatch`). The pre-0.8 flat `host` / `port` / `token` / `panelStrips` / `panelBuses` still load and become `agents[0]` on the page's first visit — step by step in [chapter 9](09-remote-mixer.md), JSON format in [the integration note](../integrations/voicemeeter-bridge.md) |
| `speech` | Settings → Voice First Officer | TTS providers (one tab each on Voice Providers: Kokoro, ElevenLabs — see the [ElevenLabs setup guide](06-elevenlabs.md) — Google, Windows; each with a fallback voice), the First Officer voice over every provider (`foVoice` as `provider:id`, on Voices), output/input devices, recognition, PTT/ATC-mute bindings, track phrases the FO did not understand (`trackUnmatched` on), read through checklist lines already set (`checklistReadThroughSatisfied` off, on General), sterile cockpit (master switch on Setup); **Ask the First Officer** (`foQuestions`: `enabled` off, `leadIns` comma-separated, `minimumWords` 4, `smallTalk` off, `timeBudgetSeconds` 6, `standBySeconds` 2) on Briefings & LLM; **Region Facts** (`regionFacts`: `enabled` off, `minIntervalMinutes` 15, `maxIntervalMinutes` 30, `maxPerFlight` 4, `llmFacts` true — the curated file is `%LOCALAPPDATA%\ProsimCompanion\config\region-facts.json`) on the same page, **FO-side duties** (`foSide.baroFollow` off — the FO follows your STD / QNH and takes "QNH …" / "your side standard"; `foSide.efisCommands` off — "your range forty" / "ND to arc"; `foSide.adirsCheck` off — the read-only ADIRS / FMGC cross-check, once per departure and on "cross-check") |
| `voices` | Settings → Voice First Officer → Voices | purser/company/ground voices as `provider:id`, each a dropdown over every connected provider grouped by provider then region (blank = the FO voice) + intercom filters |
| `checklists` | Checklists | checklist sets, manual-override allowance |
| `sop` | Settings → Voice First Officer → Callouts & Placards (rest: file) | on the page: callouts master switch, flap placards, gear limits and the "gear still down" height, the **Approach Gates** card (stable-approach check, per-gate height / gear / minimum flap / speed band / sink rate / spoken pass, add and remove gates, wording, thrust check), placard advisories, the **In-flight monitoring** card (`sop.monitoring`: `fuelCheck` enabled / `intervalMinutes` 30 / `shortfallMarginKg` 300 / `onPlanWithinKg` 100; `grossErrorCheck` enabled / `automatic` / `zfwToleranceKg` 500 / `blockFuelToleranceKg` 300 / `vSpeedToleranceKt` 3 / `flexToleranceC` 2; `destinationWeather` enabled / `visibilityThresholdM` 1500 / `ceilingThresholdFt` 500 / `tailwindThresholdKt` 5 / `minutesBetweenAnnouncements` 15 / `announceAtisChange`; `readbacks` enabled / `altimeterToleranceHpa` 0.5 / `vSpeedToleranceKt` 1 / `minimumsToleranceFt` 10; `windLimits` enabled / `approachAdvisory` — every `enabled` is off by default). File only: the individual callout texts and thresholds, the other flow-monitor reminders, weather advisories |
| `aircraftLimits` | Settings → Display & Flight Data → Aircraft limits | wind limits for the Takeoff / Landing pages, the briefings and the FO's wind check: `maxTailwindTakeoffKt` 15, `maxTailwindLandingKt` 15, `maxCrosswindTakeoffKt` 38, `maxCrosswindLandingKt` 38, `maxCrosswindWetKt` 33, `crosswindRwycc5Kt` … `crosswindRwycc1Kt` 25 / 20 / 15 / 10 / 5 — A320 published demonstrated values; operators restrict further |
| `briefing` | Settings → Voice First Officer → Briefings & LLM (DFD path on Setup) | departure/arrival overrides, DFD path (`dfdPath`), minima capture, emergency brief and threats (`takeoffEmergencyBrief` off, `askForThreats` on, `arrivalGoAroundBrief` off, `accelerationAltitudeFt` / `thrustReductionAltitudeFt` 1500 AAL), LLM endpoint (`llm*`), streamed LLM speech (`streamLlm` true); `llmWakeOnLan` is legacy — see `wakeOnLan` |
| `persona` | Settings → Voice First Officer → FO Persona | FO persona — enabled, name, experience, formality, chattiness 0–3, what it styles (briefings / debrief / advisories), varied acknowledgements, advisory restyle budget |
| `sayIntentions` | Settings → Voice First Officer → SayIntentions | API key source, phraseology, auto-tune, departure gating, the wrong-frequency report (`frequencyMonitorEnabled` true, `frequencyMonitorWaitSeconds` 60), **arrival gate from ATC** (`arrivalGateFromAtc` off — SayIntentions' assigned gate becomes the GSX arrival gate), weather |
| `cabin` | Settings → Voice First Officer → Cabin Crew | purser reports, the cabin-secure wait (`cabinSecureMinDelaySeconds` 45, `cabinSecureSecondsPerPax` 1.0, `cabinSecuringReplyText`), the FO's cabin-call auto-answer (`autoAnswerGround` off / `autoAnswerGroundDelayMs` 4000, `autoAnswerAir` off / `autoAnswerAirDelayMs` 2500), ambient events, the purser cruise query (`cruiseQuery` off, `cruiseQueryDelayMinutes` 10, `cruiseQueryWindowSeconds` 20, wording `cruiseQueryText` / `cruiseQueryEtaAckText` / `cruiseQueryRideAckText` / `cruiseQueryGenericAckText` / `cruiseQueryNoReplyText`), **turbulence** (`turbulenceDetection` off, `turbulenceUnit` auto, `turbulenceLightG` 0.2 / `turbulenceModerateG` 0.5 / `turbulenceSevereG` 1.0, `turbulenceSeatbeltAdvice` off, `turbulencePurserCall` off), **captain's PA** (`paPrompts` off, `paAfterTakeoffMinutes` 10, `purserPaAudible` off), the turbulence and PA wording (`turbulenceCallText` … `paPrepareLandingText`), dings, interphone chime |
| `company`, `debrief`, `day` | Settings → Voice First Officer → Company & Debrief | company channel (auto loadsheet read, ACARS chime, cruise messages + probability, **movement messages** (`movementMessages` off, `movementMessagesSpoken` off — the MVT AD/AA telex in RCVD MSGS slots 06/07), **delay codes** (`delayCodes` off — the FO asks for the IATA code when off blocks is late; `delayToleranceMinutes` 15 is the on-time rule for the question, the Logbook and the day summary; the list is `%LOCALAPPDATA%\ProsimCompanion\config\delay-codes.json`), save the spoken loadsheet), post-flight debrief (on, full / brief, LLM styling), company day mode (auto track, start at first preflight, turnaround summary, post-flight allowance, auto-close idle, rotations folder — empty = `%LOCALAPPDATA%\ProsimCompanion\config\rotations`, seeded with a `README.txt` that shows the file format — and the state file) |
| `flightData` | Settings → Display & Flight Data | SimBrief fetch attempts, prelim/final loadsheet triggers and delays, last-minute changes (`lmcEnabled` off, `lmcMaxPax` 4, `lmcMaxWeightKg` 300, `lmcMaxMacShift` 0.5, `lmcAutoRevise` off) |
| `fuel` | Settings → Display & Flight Data → Fuel decision | `decisionAdvice` off (the FO's dispatch question), `holdingBurnKgPerHour` 2200, `thunderstormHoldingMinutes` 20, `marginalHoldingMinutes` 15, `belowMinimaHoldingMinutes` 30 |
| `flight` | Settings → Display & Flight Data → Diversion | `diversionEnabled` off — "divert to …" / `flight.divert` and the Diversion card |
| `flightStatus` | Settings → Display & Flight Data → Flight Status card | gate monitor final-call thresholds (`gateFinalCallPaxPercent` 90, `gateFinalCallMinutesBeforeStd` 10), weather-card refresh (`weatherRefreshMinutes` 10), top-of-descent notice lead (`todLeadMinutes` 10, 0 = off), the tile order of the Flight Status page (`tileOrder`, a list of `hero` / `sequence` / `sim` / `app` / `gsx` / `services`; empty = default; written by the page's Edit layout mode, a Reset button on the settings page); also drives the pop-out Flight Monitor |
| `flightState` | Settings → Advanced → Flight Phase Engine | phase-engine thresholds and settle times (calibration data) |
| `techLog`, `logbook` | Settings → Voice First Officer → Tech Log & Logbook | tech log on, offer entries after an abnormal, random wear, rectify on due date, open items in the briefings (`briefOpenItemsInBriefings` on), flag checklist lines (`flagChecklistItems` on), MEL repair days A–D, store / wear-pool file paths; logbook on + store path. The defects themselves are on the Tech Log page, the flights on the Logbook page |
| `abnormals` | Settings → Voice First Officer → Abnormals & Drills | `enabled` (detect + announce), `interactiveDialogue` (work the ECAM line by line), `confirmTimeoutSeconds` 20, `maxSilentPrompts` 3 (0 = ask forever), `maxVerifyRetries` 1, **rejected takeoff** (`rtoEnabled` off — "stop" / "reject" or levers to idle on the roll, the PM calls and the ECAM offer), **engine failure after V1** (`efatoEnabled` off — the sequenced memory drill with the ENG FAIL hand-over at 400 ft). The procedures are JSON under `%LOCALAPPDATA%\ProsimCompanion\config\abnormals` |
| `wakeOnLan` | Settings → Setup → Wake-on-LAN | `targets[]` — one row per PC to wake at startup (name, on/off, MAC, broadcast, port) with a Send now button; the old `briefing.llmWakeOnLan` block is moved into the list on the page's first visit |
| `weather` | Settings → Setup → Weather Sources | ActiveSky snapshot path (empty = auto-detect), local API on/off, host, port, timeout |
| `notifications` | Settings → Notifications | `enabled` (off), `targets[]` (name, kind ntfy / discord / webhook, `url` + `token` stored encrypted, ten per-event switches), `failureCooldownSeconds` 60, `sendTimeoutSeconds` 5, `holdoverExpiringMinutes` 5 — payloads in [docs/integrations/notifications.md](../integrations/notifications.md) |
| `updateCheck` | Settings → Setup → Updates | update banner on/off + check interval (hours) |
| `logging` | Settings → Advanced → Logs | per-subsystem levels (hot-reload), `wireTrace`, `keepVerbose` (off: a Debug level or the wire trace switches itself back to Information after 24 h), `mirrorToSession` |
| `telemetryApi` | Settings → Advanced → Logs | read-only telemetry endpoints for post-flight verification |
| `commandApi` | Settings → Setup | HTTP command API for the Stream Deck plugin (`enabled` checkbox, applies live; off by default; token-gated even on localhost — `requireTokenOnLoopback` is file-only) |

## The `gsx` section

Ground behaviour, in the Ground Services rail order:

- **Automation** — master switch, *Hardware next-service button* (`smartButtonEnabled`, on;
  advanced: *Trigger switch* — a list grouped by panel, from ProSim when connected else from
  the shipped catalogue — writes `smartButtonDataRef`, empty = the INT/RAD rockers on both
  audio panels; *Trigger value* — that switch's positions, or a number — writes
  `smartButtonTriggerValue`, 0 = INT; turn it off or re-bind it when your panel or audio
  bridge keys the interphone mic on INT), auto-start departure services, wait-for-OFP gate,
  *Ground preparation start* (`groundPrepActivation`: `auto` | `voice` | `voiceThenAuto` —
  the last keeps the "commence ground services" wait for the first leg and starts a
  turnaround by itself once the next flight plan is loaded at the stand with the engines
  off), *Turnaround auto-start delay* (`turnaroundAutoStartDelaySeconds`, 30) and *Ask
  before a turnaround starts* (`turnaroundAskFirst`, off: the FO asks "Next plan loaded —
  commence ground services?" and starts on yes)
- **SimBrief / VDGS** — *Reload SimBrief in GSX on a new OFP* (`reloadSimbriefOnNewOfp`,
  off): clicks GSX's own "SimBrief" gate-menu line once per new flight plan so the VDGS
  and marshaller boards show the right flight number, destination and ETD — only while
  parked at the gate with engines off and GSX ready, never in flight. The Status section's
  *Reload SimBrief in GSX* button and the `gsx.reloadSimbrief` command do the same on demand
  whatever this is set to. *Additional Services pages to search* (`simbriefReloadMenuPages`,
  4): the row sits on neither the gate menu nor the first Additional Services page, so the
  reload walks that many further pages; when no page has it, the Status section says so once
  and the plan is not retried — GSX only offers the row with a SimBrief username set in its
  own settings
- **Arrival & Deboarding** — besides the gate and position-menu switches: *Airport list pages
  to search* (`airportPickMenuPages`, 4) for the in-flight "Select airport" walk (three walks
  per flight, ten minutes apart; a "no airport loaded" answer waits for the destination to
  load) and *Ask which stand when GSX splits the gate* (`askPilotOnAmbiguousGate`, on): when
  GSX answers the gate number with suffix variants (W36 → 36A / 36B) and neither your suffix
  nor a lone jetway decides, you are asked (OFP page / Status buttons, the FO) and the first
  variant goes at taxi-in without an answer; off takes the first variant at once
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
  `deice.oatThresholdC` 3 °C — above 10 °C the card warns it will ask on nearly every
  flight; `deice.requirePrecipitation` on). The policy runs **when the de-icing step's turn
  comes** on the departure sequence (*When to ask*, `deice.askWhen`: `whenStepIsNext`
  default — every earlier service done, the plan loaded, the weather known — or
  `afterBoardingComplete`, which asks on the boarding-completed edge and repeats the
  question once when the step is next if you have not answered). It reads ProSim's OAT (else
  the METAR temperature) and the departure METAR shown on the Flight Status weather card: at
  or below the threshold with snow, freezing precipitation, ice pellets, rain, freezing fog or
  a dew-point spread of 3 °C or less it either asks the captain (the FO: *"Captain, conditions
  call for de-icing — OAT −2, snow. Request it?"*; the Ground Services Status section shows
  the question with *Request de-icing* / *No de-icing* buttons) or, on **auto**, puts the GSX
  `DeIce` service on the departure sequence as its last step. Silence is not an answer: the
  FO hands the question back (*"Your call — the question stands…"*) and it stays open —
  say *request de-icing* or *no de-icing*, or use the buttons — until the *Answer timeout*
  (`deice.questionTimeoutMinutes`, 5) runs out, which counts as no. With no METAR for the
  departure the policy makes no request (no data, no call). Every verdict is on the Status
  section's De-Icing row and in the decision log. *Auto-accept de-ice offer* obeys your
  decision: a declined question answers GSX's own offer with No, an open one is left for
  you, an accepted one with Yes. *Packs procedure* (`deice.packsProcedure`, off): the FO calls
  *packs off, ditching on* when the crew arrives and *packs on, ditching off* when it leaves,
  checking the overhead lights twenty seconds later — it never moves the switches
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
