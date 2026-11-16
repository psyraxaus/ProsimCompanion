# Settings reference

All configuration lives in **`config\settings.json`** beside the app, camelCase, one section
per feature. The web pages cover the everyday sections; everything is also hand-editable —
missing keys fall back to safe defaults, and the app adds newly available defaults to the
file on start. **Never share your `webUi.accessToken`.**

**Secrets are encrypted per Windows user.** The API keys and the access token (`prosim.apiKey`,
`sayIntentions.manualApiKey`, `briefing.llmApiKey`, `speech.elevenLabsApiKey`,
`webUi.accessToken`, and also `webUi.https.pfxPassword`, each notification target's `url` and
`token`, and the remote mixer tokens) are stored as
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

**Setup guides.** The step-by-step chapters for these sections: `gsx` → [chapter 20](20-gsx-settings.md); `speech` →
[Kokoro](18-kokoro.md), [ElevenLabs](06-elevenlabs.md), [speech recognition server](16-speech-recognition-server.md);
`briefing` → [language model](15-language-model.md) and [flight deck options](26-flight-deck-options.md);
`mcdu` → [chapter 19](19-mcdu-box-check.md); `sayIntentions` → [chapter 17](17-sayintentions.md);
`scenario` → [chapter 22](22-scenarios.md); `company`, `day`, `techLog`, `logbook` → [chapter 21](21-duty-day-logbook-company.md);
`skalarki` → [chapter 23](23-skalarki.md); `cockpitSwitches` → [chapter 24](24-cockpit-switches.md);
`notifications`, `flightState`, the aircraft profiles and `webUi` appearance → [chapter 25](25-app-pages.md);
`commandApi`, `telemetryApi` → [chapter 27](27-command-telemetry-api.md).

| Section | Page | Highlights |
|---|---|---|
| `prosim` | Settings → Setup | `sdkPath` (ProSimSDK.dll), `host`, optional `apiKey`; the **ProSim Setup Check** card (`setupCheckEnabled` on, `setupCheckIgnore[]` = the rows ticked "keep", by dataref name) — see [chapter 2](02-using-the-app.md#prosim-setup-check) |
| `webUi` | Settings → Setup / Display & Flight Data / Appearance, desktop window | `port` (default 5320), `bindToAllInterfaces`, `accessToken`, theme, units, Solari animation, `https` (`enabled` off, `port` 5321, `pfxPath`, `pfxPassword` encrypted — the optional secure address, see [chapter 7](07-tablet-install.md)), `showNextServiceInHeader` (off — the header NEXT SERVICE button), `language` (empty = follow each browser; else a pack code `en`, `de`, `es`, `fr`, `ja`, `ko`, `nl` — Settings → Appearance → Language; packs are the folders in `%LOCALAPPDATA%\ProsimCompanion\config\lang`, one per language with one JSON file per page, the `en` folder is the source) |
| `gsx` | Settings → Ground Services | the whole ground pillar — see below (master switch on Setup) |
| `audio` | Settings → Audio Control | backend (CoreAudio/VoiceMeeter), `requireAcpPower` (on: knobs apply only with the panel powered; off: always, like the remote mixer), app mappings, ACP side, device blacklist, device-filter flow/state (master switch on Setup); mapping channels are the eight ACP knobs plus `loudspeaker` (the cockpit LOUD SPEAKER dial, captain and first officer — see [chapter 8](08-loudspeaker-dial.md)) |
| `mixer` | Settings → Audio Control → Remote Mixer | Voicemeeters on other PCs through VoicemeeterBridge agents: `enabled` (off), `agents[]` (one block per PC: `name`, `enabled`, `host`, `port` (5088), `token` (encrypted), `panelStrips` / `panelBuses` (0-based, shown on the Status card)), reconnect delays, `setTimeoutMs`, `gainMinDb` / `gainMaxDb`, `mappings` (panel + channel → `agent` name + strip/bus, `useLatch`). A file that has `host` / `port` / `token` / `panelStrips` / `panelBuses` directly under `mixer` still loads: the page turns them into the first agent. Step by step in [chapter 9](09-remote-mixer.md), JSON format in [the integration note](../integrations/voicemeeter-bridge.md) |
| `speech` | Settings → Voice First Officer | the speech recognition server on Listening & PTT (`asrBaseUrl`, `asrApi` — see the [setup guide](16-speech-recognition-server.md)), TTS providers (one tab each on Voice Providers: Kokoro, ElevenLabs — see the [ElevenLabs setup guide](06-elevenlabs.md) — Google, Windows; each with a fallback voice), the First Officer voice over every provider (`foVoice` as `provider:id`, on Voices), output/input devices, recognition, PTT/ATC-mute bindings, track phrases the FO did not understand (`trackUnmatched` on), read through checklist lines already set (`checklistReadThroughSatisfied` off, on General), sterile cockpit (master switch on Setup); **Ask the First Officer** (`foQuestions`: `enabled` off, `leadIns` comma-separated, `minimumWords` 4, `smallTalk` off, `timeBudgetSeconds` 6, `standBySeconds` 2) on Briefings & LLM; **Region Facts** (`regionFacts`: `enabled` off, `minIntervalMinutes` 15, `maxIntervalMinutes` 30, `maxPerFlight` 4, `llmFacts` true — the curated file is `%LOCALAPPDATA%\ProsimCompanion\config\region-facts.json`) on the same page, **FO-side duties** on Settings → Flight Deck → MCDU (`foSide.baroFollow` off — the FO follows your STD / QNH and takes "QNH …" / "your side standard"; `foSide.efisCommands` off — "your range forty" / "ND to arc"; `foSide.adirsCheck` off — the read-only ADIRS / FMGC cross-check, once per departure and on "cross-check"), **step-climb advice** (`stepClimbAdvice` off, `stepClimbLeadNm` 20 — on the **In-flight monitoring** card of Callouts & Placards), **Human Pacing** (`humanize.enabled` on, `humanize.tempo` 1.0 — an advanced card on General) |
| `voices` | Settings → Voice First Officer → Voices | purser/company/ground voices as `provider:id`, each a dropdown over every connected provider grouped by provider then region (blank = the FO voice) + intercom filters |
| `accents` | Settings → Voice First Officer → Voices → **Accent Localization** | the ground crew's accent follows the airport's country: `enabled` on, `googlePersona` Charon; `overrides` is edited in the file only |
| `checklists` | Settings → Display & Flight Data → **Checklists** | `allowManualOverride` off — lets you tick the automatically checked lines by hand on the Checklists page. The checklist set in use is chosen on the Checklists page itself, not in this section |
| `sop` | Settings → Flight Deck → Callouts & Placards (rest: file) | on the page: callouts master switch, flap placards, gear limits and the "gear still down" height, the **Transition Reminders** card (`sop.transition`: `enabled` off, `delaySeconds` 30 — the DFD transition altitude/level per airport, the pilot's PFD altitude), the **Approach Gates** card (stable-approach check, per-gate height / gear / minimum flap / speed band / sink rate / spoken pass, add and remove gates, wording, thrust check), placard advisories, the **In-flight monitoring** card (`sop.monitoring`: `fuelCheck` enabled / `intervalMinutes` 30 / `shortfallMarginKg` 300 / `onPlanWithinKg` 100; `grossErrorCheck` enabled / `automatic` / `zfwToleranceKg` 500 / `blockFuelToleranceKg` 300 / `vSpeedToleranceKt` 3 / `flexToleranceC` 2; `destinationWeather` enabled / `visibilityThresholdM` 1500 / `ceilingThresholdFt` 500 / `tailwindThresholdKt` 5 / `minutesBetweenAnnouncements` 15 / `announceAtisChange`; `readbacks` enabled / `altimeterToleranceHpa` 0.5 / `vSpeedToleranceKt` 1 / `minimumsToleranceFt` 10; `windLimits` enabled / `approachAdvisory` — every `enabled` is off by default). File only: the individual callout texts and thresholds, the other flow-monitor reminders, weather advisories |
| `aircraftLimits` | Settings → Display & Flight Data → Aircraft limits | wind limits for the Takeoff / Landing pages, the briefings and the FO's wind check: `maxTailwindTakeoffKt` 15, `maxTailwindLandingKt` 15, `maxCrosswindTakeoffKt` 38, `maxCrosswindLandingKt` 38, `maxCrosswindWetKt` 33, `crosswindRwycc5Kt` … `crosswindRwycc1Kt` 25 / 20 / 15 / 10 / 5 — A320 published demonstrated values; operators restrict further |
| `briefing` | Settings → Flight Deck → Briefings & LLM (DFD path on Setup) | departure/arrival overrides, DFD path (`dfdPath`), minima capture, emergency brief and threats (`takeoffEmergencyBrief` off, `askForThreats` on, `arrivalGoAroundBrief` off, `accelerationAltitudeFt` / `thrustReductionAltitudeFt` 1500 AAL), LLM endpoint (`llm*`), streamed LLM speech (`streamLlm` true). To wake the language-model PC see `wakeOnLan`. Setup guide for the endpoint: [chapter 15](15-language-model.md) |
| `persona` | Settings → Voice First Officer → FO Persona | FO persona (the roster draws one FO per sim calendar day on the first plan of the day, pinned for the rest of an open duty day) — enabled, name, experience, formality, chattiness 0–3, confidence (nervous / steady / assured), what it styles (briefings / debrief / advisories), varied acknowledgements, advisory restyle budget; **Roster** (`persona.roster`: `mode` off / daily / pinned, `gateIntro` on) — a different FO per duty day from `fo-roster.json`, pin / release / draw again on the card |
| `smallTalk` | Settings → Flight Deck → Briefings & LLM → Small Talk | Cruise small talk — `enabled` off, `maxPerFlight` 3 (the ceiling; the persona's chattiness is the count), `minQuietMinutes` 8, `kinds.offer` / `kinds.view` / `kinds.layover` / `kinds.crew` on |
| `groundCrew` | Settings → Crew & Company → Ground Crew (the call switches also on Settings → Ground Services → Voices) | **Ground Crew** card: `enabled` on, `requireIntChannel` on (`intChannelGraceSeconds` 25), `mechCall` on (`hailListenTimeoutSeconds` 8); **Upcalls**: `callOnGroundPower`, `callOnChocks`, `callOnRefuelStarted`, `callOnRefuelComplete`, `callOnCateringComplete`, `callOnDeiceComplete` — all on; **Pushback Crew** (`pushbackCrew`: `enabled` on, `brakesCheck` on, `engineStartClearance` on, and its spoken lines); **Ground Crew Wording** (the `…Text` lines) |
| `groundCrew.walkaround` | Settings → Crew & Company → Ground Crew → Walkaround | The FO's exterior walkaround — `enabled` off, `minMinutes` 6, `maxMinutes` 10, `findingChance` 0.15, `allowNoGo` off, `onInterphone` on; the findings are `walkaround-findings.json` |
| `scenario` | Settings → Flight Deck → Abnormals & Drills → Scenarios | Scenario mode — `enabled` off, `perFlightChance` 0.10, `allowMajor` off, `takeoffScenarios` off, `minAltitudeFt` 1500, `showPlanned` off; the catalogue is `scenarios.json` (entries need `verified: true` with ProSim's exact failure name) |
| `sayIntentions` | Settings → SayIntentions | setup guide: [chapter 17](17-sayintentions.md); API key source, phraseology, auto-tune, departure gating, the wrong-frequency report (`frequencyMonitorEnabled` true, `frequencyMonitorWaitSeconds` 60, `offerToTune` true — the FO asks "Shall I tune it?"), **arrival gate from ATC** (`arrivalGateFromAtc` off — SayIntentions' assigned gate becomes the GSX arrival gate), **Comms hand-over** (`commsHandover.enabled` off — who has the radios in each phase, and the spoken hand-over lines), **FO works the radios** (`foRadios`: `enabled` off, `tuneOnHandoff` on, `checkIn` on, `pollSeconds` 4, `checkInDelaySeconds` 4), weather (`weatherEnabled` on) |
| `mcdu` | Settings → Flight Deck → MCDU | setup guide: [chapter 19](19-mcdu-box-check.md); `enabled` on, `allowActuation` off, `verifyPauseSeconds` 3, `boxCheckPageSeconds` 5, `boxCheckOfferFixes` on |
| `cabin` | Settings → Crew & Company → Cabin Crew | purser reports, the cabin-secure wait (`cabinSecureMinDelaySeconds` 45, `cabinSecureSecondsPerPax` 1.0, `cabinSecuringReplyText`), the FO's cabin-call auto-answer (`autoAnswerGround` off / `autoAnswerGroundDelayMs` 4000, `autoAnswerAir` off / `autoAnswerAirDelayMs` 2500), ambient events, the purser cruise query (`cruiseQuery` off, `cruiseQueryDelayMinutes` 10, `cruiseQueryWindowSeconds` 20, wording `cruiseQueryText` / `cruiseQueryEtaAckText` / `cruiseQueryRideAckText` / `cruiseQueryGenericAckText` / `cruiseQueryNoReplyText`), **turbulence** (`turbulenceDetection` off, `turbulenceUnit` auto, `turbulenceLightG` 0.2 / `turbulenceModerateG` 0.5 / `turbulenceSevereG` 1.0, `turbulenceSeatbeltAdvice` off, `turbulencePurserCall` off), **captain's PA** (`paPrompts` off, `paAfterTakeoffMinutes` 10, `purserPaAudible` off), the turbulence and PA wording (`turbulenceCallText` … `paPrepareLandingText`), dings, interphone chime |
| `company`, `debrief`, `day` | Settings → Crew & Company → Company & Debrief | company channel (auto loadsheet read, ACARS chime, cruise messages + probability, **movement messages** (`movementMessages` off, `movementMessagesSpoken` off — the MVT AD/AA telex in RCVD MSGS slots 06/07), **delay codes** (`delayCodes` off — the FO asks for the IATA code when off blocks is late; `delayToleranceMinutes` 15 is the on-time rule for the question and the Logbook; the day summary always uses 15 minutes; the list is `%LOCALAPPDATA%\ProsimCompanion\config\delay-codes.json`), save the spoken loadsheet), post-flight debrief (on, full / brief, LLM styling), company day mode (auto track, on by default — start at first preflight, the next leg starts on its OFP (never on the Preflight edge), turnaround summary, post-flight allowance, **auto-close idle** (`autoCloseIdleMinutes` 300 — counted from deboarding complete, else on-blocks; phase edges and restarts never reset it; 0 = never), **FO offers to end the day after** (`endOfDutyOfferMinutes` 10 — once cold and dark, parked, with no plan for this long the FO asks once "Captain, is that the end of our duty day?"; yes ends the day, no or silence leaves it open; 0 = never ask), rotations folder — empty = `%LOCALAPPDATA%\ProsimCompanion\config\rotations`, seeded with a `README.txt` that shows the file format — the **FDP warning** (`fdpWarning` off, `reportBeforeStdMinutes` 60; the EASA table is `%LOCALAPPDATA%\ProsimCompanion\config\ftl-easa.json`) and the state file) |
| `flightData` | Settings → Display & Flight Data | SimBrief fetch attempts, prelim/final loadsheet triggers and delays, last-minute changes (`lmcEnabled` off, `lmcMaxPax` 4, `lmcMaxWeightKg` 300, `lmcMaxMacShift` 0.5, `lmcAutoRevise` off) |
| `fuel` | Settings → Display & Flight Data → Fuel decision | `decisionAdvice` off (the FO's dispatch question), `holdingBurnKgPerHour` 2200, `thunderstormHoldingMinutes` 20, `marginalHoldingMinutes` 15, `belowMinimaHoldingMinutes` 30 |
| `flight` | Settings → Display & Flight Data → Diversion | `diversionEnabled` off — "divert to …" / `flight.divert` and the Diversion card |
| `flightStatus` | Settings → Display & Flight Data → Flight Status card | gate monitor final-call thresholds (`gateFinalCallPaxPercent` 90, `gateFinalCallMinutesBeforeStd` 10), weather-card refresh (`weatherRefreshMinutes` 10), top-of-descent notice lead (`todLeadMinutes` 10, 0 = off), the tile order of the Flight Status page (`tileOrder`, a list of `hero` / `sequence` / `sim` / `app` / `gsx` / `services`; empty = default; written by the page's Edit layout mode, a Reset button on the settings page); also drives the pop-out Flight Monitor |
| `flightState` | Settings → Advanced → Flight Phase Engine; Settings → Display & Flight Data → **After a restart in flight** | phase-engine thresholds and settle times (calibration data); on the restart card `snapshotEnabled` and `snapshotMaxAgeHours` |
| `techLog`, `logbook` | Settings → Crew & Company → Tech Log & Logbook | tech log on, offer entries after an abnormal, random wear, rectify on due date, open items in the briefings (`briefOpenItemsInBriefings` on), flag checklist lines (`flagChecklistItems` on), MEL repair days A–D, store / wear-pool file paths; logbook on + store path. The defects themselves are on the Tech Log page, the flights on the Logbook page |
| `abnormals` | Settings → Flight Deck → Abnormals & Drills | `enabled` (detect + announce), `interactiveDialogue` (work the ECAM line by line), `confirmTimeoutSeconds` 20, `maxSilentPrompts` 3 (0 = ask forever), `maxVerifyRetries` 1, **rejected takeoff** (`rtoEnabled` off — "stop" / "reject" or levers to idle on the roll, the PM calls and the ECAM offer), **engine failure after V1** (`efatoEnabled` off — the sequenced memory drill with the ENG FAIL hand-over at 400 ft). The procedures are JSON under `%LOCALAPPDATA%\ProsimCompanion\config\abnormals` |
| `wakeOnLan` | Settings → Setup → Wake-on-LAN | `targets[]` — one row per PC to wake at startup (name, on/off, MAC, broadcast, port) with a **Send now** button; a `briefing.llmWakeOnLan` block found in the file is moved into the list when you open the page |
| `weather` | Settings → Setup → Weather Sources | ActiveSky snapshot path (empty = auto-detect), local API on/off, host, port, timeout |
| `notifications` | Settings → Notifications | `enabled` (off), `targets[]` (name, kind ntfy / discord / webhook, `url` + `token` stored encrypted, ten per-event switches), `failureCooldownSeconds` 60, `sendTimeoutSeconds` 5, `holdoverExpiringMinutes` 5 — payloads in [docs/integrations/notifications.md](../integrations/notifications.md) |
| `updateCheck` | Settings → Setup → Updates | update banner on/off + check interval (hours) |
| `logging` | Settings → Advanced → Logs | the capture level (`defaultLevel`, Information; applies at once), `wireTrace`, `keepVerbose` (off: a Debug level or the wire trace switches itself back to Information after 24 h), `mirrorToSession`; per-subsystem levels (`overrides`) are edited in the file only |
| `telemetryApi` | Settings → Advanced → Logs | `enabled` on — the read-only telemetry endpoints for post-flight verification; the same switch also allows the diagnostics export. `requireTokenOnLoopback` off |
| `diagnostics` | Settings → Advanced → Logs (the export); Settings → Advanced → Diagnostics | the diagnostics export: `bundleSessionCount` 5 (1–20), `bundleLogDays` 3 (1–14), `includeWireTrace` off; the Diagnostics pages: `ecamTickMs` 1000 (250–10000, the **Refresh** card), `ecamHostLabels` (your names for the speech and language-model PCs, the **Host labels** card) — see [chapter 13](13-diagnostics.md) |
| `commandApi` | Settings → Setup | HTTP command API for the Stream Deck plugin (`enabled` checkbox, applies live; off by default; token-gated even on localhost — `requireTokenOnLoopback` is file-only) |
| `inputRelay` | Settings → Setup | vJoy relay ([chapter 12](12-remote-streamdeck.md)): `enabled` (off), `vJoyInterfacePath` (empty = the driver's folder), `vJoyDevice` 1, advanced `holdMs` 150, `views[]` rows (name → vJoy button) for `input.pressView` |
| `cockpitSwitches` | Settings → Cockpit Switches | Stream Deck keys for ProSim switches ([chapter 24](24-cockpit-switches.md)): `enabled` (on), `rows[]`, one grid line each with a fold-out for the rare fields — `id`, `name`, `dataRef` (picked from ProSim's switch list, `system.switches.S_*` only), `look` korry2 / korry1 / toggle / knob / guarded, `pressMode` toggle / momentary / cycle, legends, optional `upperIndicator` / `lowerIndicator` lamps, `lowerLitAtZero` (false; true = the lower lamp lights in position 0 — the leak valves' OFF light), `showUnlitLegend` (true = dim legend while dark, false = extinguished), `upperColor` / `lowerColor` / `bezelColor` hex |
| `display` | Settings → Screen & FOV | Your physical screens and the field of view they imply ([chapter 14](14-screen-fov.md)): `realism` (1.0 = life-size on the Main screen, 0 = the sim default; a 0–100 % slider), `displays[]` (`name`, `role` main / left / right / overhead / other — exactly one main, `diagonalInches`, `aspectWidth` / `aspectHeight`, `distanceMetres`, `yawDegrees`, `centreAboveEyeMetres`; default one Main, 32", 16:9, 0.8 m), `applyPitch` (off; also write the camera pitch). The Apply section rewrites `InitialZoom` in the aircraft's saved pilot view (MSFS 2024 LocalCache `cameras.cfg`, backed up first) — never a file under Official or Community |
| `skalarki` | Settings → Hardware → Skalarki Panels | setup guide: [chapter 23](23-skalarki.md); **Profiler link**: `enabled` off, `sdkPath` (the folder with the Skalarki SDK files), `profilerHost` 127.0.0.1, `profilerPort` 53000, `catalogue` A32X; **Boards**: `enabledDevices[]` (empty = every connected board), `deviceLabels` (your name per board); **Lamp test and reconnect**: `lampTestMs` 2000, `reconnectBackoffMs` 5000 |
| `aircraftProfiles` | Settings → Aircraft Profiles | `profiles[]` — one block per aircraft: how it is matched (aircraft title contains / equals, airline, or default; `matchString`) and an optional `gsxSettings` block, see the end of the next section and [chapter 25](25-app-pages.md) |

## The `gsx` section

Ground behaviour, in the order of the Settings → Ground Services menu. Under each menu item
the bold names are the cards on that page. What each setting does, step by step, is in
[chapter 20](20-gsx-settings.md); this list gives the option names and defaults.

- **Status** — no settings: the live panel with the **Departure Status Board**, the open
  questions and **Automation Decisions**. Its *Reload SimBrief in GSX* button is described
  under Departure Services below
- **Doors & Jetway**
  - **Doors** — `doorAutomationEnabled` on (the rules below need it); pax doors follow stairs
    (`doorStairHandling` on); service doors follow catering (`doorCateringHandling` on;
    `cateringDoor` `auto` — learned from GSX — or `forward` / `aft`); cargo doors follow
    loading (`doorCargoHandling` on, `doorOpenOnBoardingActive` on, `cargoDoorOpenDelaySec`
    2, `cargoDoorCloseDelaySec` 16, `keepCargoDoorsOpenAfterLoad` off,
    `keepCargoDoorsOpenAfterUnload` off); `closeDoorsOnFinal` on; advanced:
    `suppressGsxDoorMessages` on
  - **Jetway / Stairs** — connect at session start (`autoConnectJetwayOrStairs` on), when
    departure services start (`callJetwayStairsDuringDeparture` off), on arrival
    (`callJetwayStairsOnArrival` on); stairs removal `removeStairsAfterDeparture` never /
    always / `onlyJetway` (default); `removeJetwayStairsOnFinal` on
- **Ground Equipment** — one card: `autoGroundEquipment` on (GPU and chocks),
  `connectGpuWithApuRunning` on, PCA (`pcaMode` never / always / only jetway stands;
  advanced `pcaOverride` on), `gradualGroundEquipRemoval` off, arrival chock delay
  (`chockDelayMinSec` 10 to `chockDelayMaxSec` 20), `autoReposition` on (reposition at
  startup); advanced: `disableProsimNativeGsx` on
- **Departure Services**
  - **Automation** — master switch (`automationEnabled`, on), *Hardware next-service button*
    (`smartButtonEnabled`, on; advanced: *Trigger switch* — a list grouped by panel, from
    ProSim when connected else from the shipped catalogue — writes `smartButtonDataRef`,
    empty = the INT/RAD rockers on both audio panels; *Trigger value* — that switch's
    positions, or a number — writes `smartButtonTriggerValue`, 0 = INT; turn it off or
    re-bind it when your panel or audio bridge keys the interphone mic on INT),
    `autoStartDepartureServices` off, *Ground preparation start* (`groundPrepActivation`:
    `auto` | `voice` | `voiceThenAuto` — the last keeps the "commence ground services" wait
    for the first leg and starts a turnaround by itself once the next flight plan is loaded
    at the stand with the engines off), *Turnaround auto-start delay*
    (`turnaroundAutoStartDelaySeconds`, 30), *Ask before a turnaround starts*
    (`turnaroundAskFirst`, off), *Wait for OFP before departure services*
    (`requireOfpBeforeDeparture`, on)
  - **SimBrief / VDGS** — *Reload SimBrief in GSX on a new OFP* (`reloadSimbriefOnNewOfp`,
    off): clicks GSX's own "SimBrief" gate-menu line once per new flight plan so the VDGS
    and marshaller boards show the right flight number, destination and ETD — only while
    parked at the gate with engines off and GSX ready, never in flight. The Status section's
    *Reload SimBrief in GSX* button and the `gsx.reloadSimbrief` command do the same on
    demand whatever this is set to. *Additional Services pages to search*
    (`simbriefReloadMenuPages`, 4): how many further pages of GSX's Additional Services menu
    the reload turns to find the row; when no page has it, the Status section says so once
    and the plan is not retried — GSX only offers the row with a SimBrief username set in
    its own settings
  - **Cold & Dark Check** — `aircraftStateCheckEnabled` on (verify the aircraft state at
    session start), `aircraftStateCheckAnnounce` on (the First Officer reads out mismatches)
  - **Departure Service Order** — `departureServices[]`, the ordered steps, with the preset
    buttons Classic / Parallel / Manual. Each step has an activation rule (Previous called /
    Previous requested / Previous active / Previous completed, All earlier completed, Manual
    by user, Voice request, Skip / ignore), a leg constraint (every leg / first leg only /
    turnarounds only / company hubs only / non-hub airports only) and a minimum flight time
    (`minimumFlightMinutes`; short hops skip the service)
- **Refuel & Boarding**
  - **Refuel** — *Refuel call* (`refuelCall`, `automatic`; the other choice waits until the
    crew confirms the fuel), `refuelSyncEnabled` on, `refuelMethod` `fixedRate`
    (`refuelRateKgPerSec` 25) or `dynamicRate` (`refuelTimeTargetSeconds` 300),
    `refuelFinishOnHose` off, `allowDefuel` off. *Skip refuel when FOB exceeds plan*
    (`skipRefuelOnTankering`, on): when the fuel on board already meets the plan (INIT
    override, else OFP block fuel, else the EFB planned fuel; 25 kg tolerance) the truck is
    not ordered — by the departure sequence, by "confirm fuel" (voice or the INIT button) or
    by a direct refuel request. The preliminary loadsheet is still produced, and the ground
    crew does not call "refueling complete". Switch it off to always order the truck
  - **Boarding** — `boardingSyncEnabled` on
  - **Passengers** — `randomizePaxNoShows` off, `noShowChancePerSeat` 0.03,
    `weightPerBagKg` 15
- **Pushback**
  - **Pushback Direction** — default direction (`pushbackPreference`: **auto** toward the
    departure runway / **ask** the pilot / fixed tail left, tail right, straight — your own
    voice or Korry-button choice for the flight always wins), *Ask when unsure*
    (`pushbackAskWhenUnsure`, on)
  - **Pushback** — `beaconPushbackSequenceEnabled` on, `confirmEngineStart` on,
    `releaseSteeringAfterPushback` on
  - **Beacon Sequence Steps** — one switch per step, all on: `seqCloseDoors`,
    `seqRetractJetway`, `seqRemoveEquipment`, `requestPushClearanceOnBeacon` (the FO asks
    ATC for push and start; `pushClearanceWaitSec` 120), `callPushbackOnBeacon`
  - **Beacon Sequence Delays** — the random wait before each step, in seconds: doors
    (`seqDoorsCloseDelayMinSec` 10 to `…MaxSec` 20), jetway (`seqJetwayRetractDelayMinSec` 10
    to `…MaxSec` 25), equipment (`seqGpuDisconnectDelayMinSec` 10 to `…MaxSec` 20)
- **Arrival**
  - **Arrival & Deboarding** — `deboardingSyncEnabled` on, `autoCallDeboardOnArrival` on,
    `arrivalStableSeconds` 10, `arrivalGate` empty, `answerPositionMenuWithArrivalGate` off,
    *Airport list pages to search* (`airportPickMenuPages`, 4), *Ask which stand when GSX
    splits the gate* (`askPilotOnAmbiguousGate`, on). How the gate reaches GSX in flight and
    what happens when the scenery splits a gate: [chapter 20](20-gsx-settings.md), "Arrival"
  - **Stand Knowledge** — read GSX profiles and scenery for stands
    (`resolveGatesFromProfiles`, on), the GSX profile folder (`gsxProfileFolder`, empty =
    `%APPDATA%\Virtuali\GSX\MSFS`), scenery package folders (`sceneryPackageFolders`,
    `;`-separated on the page; empty = auto-detected Store/Steam locations + `UserCfg.opt`)
  - **Fuel Memory** — `fuelSaveLoadFob` on (FOB saved and restored per aircraft),
    `fuelResetDefaultKg` 3000
- **Operators & Hubs**
  - **Operator Selection** — `autoSelectOperator` on, `operatorPreferences[]` (your
    preferred operators, best first)
  - **Company Hubs** — `companyHubs[]` (airport codes, used by the hub / non-hub leg
    constraints)
- **GSX Questions**
  - **Skip Questions / Pop-ups** — `skipFollowMe` on, `answerCrewQuestions` on
    (`crewBoardingAnswer` Both), `skipCrewBoardingQuestion` off, `confirmPushbackRequest`
    on, the tug question during boarding (`tugQuestionAnswer`, no),
    `callPushbackWhenTugAttached` (`afterFinalLoadsheet`)
  - **De-Icing** — *Auto-accept de-ice offer* (`autoDeIce`, off — GSX's own "Ice warning"
    question), the fluid and concentration (`deIceFluidType` Type IV, `deIceConcentration`
    75; shared by GSX's offer and the app's own request), *Request de-icing from the
    weather* (`deice.autoRequest`: **off** default / **ask** / **auto**), `deice.oatThresholdC`
    3 °C (above 10 °C the card warns it will ask on nearly every flight),
    `deice.requirePrecipitation` on, *When to ask* (`deice.askWhen`: `whenStepIsNext`
    default, or `afterBoardingComplete`), *Answer timeout* (`deice.questionTimeoutMinutes`,
    5), *Packs procedure* (`deice.packsProcedure`, off). When the question comes and how to
    answer it: [chapter 20](20-gsx-settings.md), "GSX Questions". Only stated here: the
    policy reads ProSim's OAT (else the METAR temperature) and the departure METAR, and acts
    at or below the threshold with snow, freezing precipitation, ice pellets, rain, freezing
    fog or a dew-point spread of 3 °C or less; with no METAR it makes no request. On **auto**
    it puts the GSX `DeIce` service on the departure sequence as its last step without
    asking. *Auto-accept de-ice offer* obeys your decision: a declined question answers
    GSX's own offer with No, an open one is left for you, an accepted one with Yes. With
    *Packs procedure* the FO calls *packs off, ditching on* when the crew arrives and *packs
    on, ditching off* when it leaves, checking the overhead lights twenty seconds later — it
    never moves the switches. Every verdict is on the Status section's De-Icing row and in
    the decision log (the card **Automation Decisions** on Settings → Ground Services →
    Status)
- **Voices**
  - **Ground Service Voices** — `groundVoicesEnabled` on (one switch for every voice that
    speaks by itself about a ground service), `voiceControlEnabled` on (ask for services by
    voice)
  - **Ground Crew** and **Cabin Crew** — the same switches as in the `groundCrew` and
    `cabin` sections (the ground crew's calls, the pushback crew, the purser's
    boarding-complete and cabin-empty reports), shown here too
  - **First Officer Advisories** — `aircraftStateCheckAnnounce` on, `announceParkingConflict`
    on, `announceDroppedCalls` on
  - **Questions** — the three questions that need your answer, shown here too:
    `pushbackAskWhenUnsure`, `askPilotOnAmbiguousGate`, `turnaroundAskFirst`
- **Connection & Timeouts** — two advanced cards. **Connection**: `reconnectIntervalMs` 5000,
  `reconnectMaxIntervalMs` 60000. **Timeouts**: `commandTimeoutMs` 10000, `menuOpenTimeoutMs`
  5000, `intentVerifyTimeoutMs` 5000, `triggerConfirmTimeoutMs` 10000

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
| `checklists\*.json` + `sets\` | the checklists and the checklist sets | yes |
| `abnormals\*.json` | ECAM abnormal definitions | on start |
| `phrases.json` | acknowledgement phrase pools | yes |
| `region-facts.json` | curated cruise region facts, keyed by country code or `sea:<name>` | yes |
| `fo-roster.json` | the First Officer roster: id, name, experience, formality, chattiness, confidence, hours on type, base, optional `provider:id` voice | yes |
| `walkaround-findings.json` | what the FO may bring back from the walkaround: id, spoken, severity cosmetic / deferrable / noGo, tech-log title + MEL category, conditions (icing, precipitation, night, afterLanding), weight, `deice` | yes |
| `scenarios.json` | the scenario catalogue: id, title, abnormalId (a shipped abnormal), failureName (ProSim's own name), `verified`, phases, weight, severity, takeoff, afterEvent (`takeoff` or `v1`, for a takeoff entry), clearAfterSeconds, techLogAfter, decision, wearTitle | yes |
| `commands.json` | file-driven voice commands | yes |
| `atc-requests.json` | SayIntentions request phrasebook | on start |
| `themes\*.json` | your own airline themes; none is shipped here, the built-in themes are inside the app | on switch |
| `delay-codes.json` | the IATA delay codes the FO offers when off blocks is late | |
| `ftl-easa.json` | the maximum duty period table behind the FDP warning | |
| `airline-callsigns.json` | airline radio callsigns for spoken flight numbers | |
| `airport-names.json` | airport names spoken in place of the ICAO code | |
| `pronunciations.json` | abbreviations said as a word or as letters | |
| `aircraft-states\*.json` | the cockpit states checked at session start (shipped: cold and dark) | |
| `lang\<code>\*.json` | the language packs, one folder per language and one file per page | |
| `rotations\README.txt` | the format note in the planned-rotations folder | |

---

**Next:** [Command API and Telemetry API](27-command-telemetry-api.md) · **See also:** [Troubleshooting](05-troubleshooting.md), [Diagnostics](13-diagnostics.md) · [Manual contents](README.md)
