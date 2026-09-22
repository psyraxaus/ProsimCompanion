# Settings reference

All configuration lives in **`config\settings.json`** beside the app, camelCase, one section
per feature. The web pages cover the everyday sections; everything is also hand-editable —
missing keys fall back to safe defaults, and the app adds newly available defaults to the
file on start. **Never share your `webUi.accessToken`.**

**Secrets are encrypted per Windows user.** The API keys and the access token (`prosim.apiKey`,
`sayIntentions.manualApiKey`, `briefing.llmApiKey`, `webUi.accessToken`) are stored as
`dpapi:…` values, protected with Windows DPAPI for the account that runs the app. They only
decrypt on that PC and that Windows account. If you copy `settings.json` to another PC or
account, those fields bind as empty, the log warns once, and the settings page shows a
"re-enter it and save" hint under each affected field — type the key again and save. A key
typed into the file by hand is accepted and encrypted on the next start. The access token
is regenerated automatically (pair tablets again via the QR code).

| Section | Page | Highlights |
|---|---|---|
| `prosim` | Settings → Setup | `sdkPath` (ProSimSDK.dll), `host`, optional `apiKey` |
| `webUi` | Settings → Setup / Display, desktop window | `port` (default 5320), `bindToAllInterfaces`, `accessToken`, theme, units, Solari animation |
| `gsx` | Settings → Ground Services | the whole ground pillar — see below (master switch on Setup) |
| `audio` | Settings → Audio Control | backend (CoreAudio/VoiceMeeter), app mappings, ACP side, device blacklist, device-filter flow/state (master switch on Setup) |
| `speech` | Settings → Voice First Officer | TTS providers/voices, output/input devices, recognition, PTT/ATC-mute bindings, sterile cockpit (master switch on Setup) |
| `voices` | Settings → Voice First Officer | purser/company voices + chimes |
| `checklists` | Checklists | checklist sets, manual-override allowance |
| `sop` | file | callout texts/thresholds, flow monitor, stabilized gates, weather advisories |
| `briefing` | Settings → Voice First Officer → Briefings & LLM (DFD path on Setup) | departure/arrival overrides, DFD path (`dfdPath`), minima capture, LLM endpoint (`llm*`) |
| `persona` | file | FO persona — enabled, name, experience, formality, chattiness, style toggles |
| `sayIntentions` | Settings → Voice First Officer → SayIntentions | API key source, phraseology, auto-tune, departure gating, weather |
| `cabin`, `company` | Settings → Voice First Officer → Cabin Crew (`company` file-only) | purser reports, ambient events, company loadsheet/cruise messages, chimes |
| `flightData` | Settings → Display & Flight Data | SimBrief fetch attempts, prelim/final loadsheet triggers and delays |
| `flightState` | Settings → Advanced → Flight Phase Engine | phase-engine thresholds and settle times (calibration data) |
| `techLog`, `logbook`, `debrief`, `day` | Tech Log / Duty Day | MEL categories, random wear, debrief verbosity, day mode |
| `updateCheck` | file | update banner enable + interval |
| `logging` | Settings → Advanced → Logs | per-subsystem levels (hot-reload), `wireTrace` |
| `telemetryApi` | Settings → Advanced → Logs | read-only telemetry endpoints for post-flight verification |
| `commandApi` | Settings → Setup | HTTP command API for the Stream Deck plugin (`enabled` checkbox, applies live; off by default; token-gated even on localhost — `requireTokenOnLoopback` is file-only) |

## The `gsx` section

Ground behaviour, in the Ground Services rail order:

- **Automation** — master switch, auto-start departure services, wait-for-OFP gate
- **Departure Service Order** — ordered steps, each with an activation rule (previous
  called/requested/active/completed, all completed, manual, skip), a leg constraint
  (always / first leg / turnaround / company hub / non-hub) and a minimum flight time
  (short hops skip the service)
- **Refuel** — fixed or dynamic rate (fixed fill time), tankering skip, finish-on-hose,
  defuel guard
- **Gate & Doors** — per-door-class rules (pax doors follow stairs, service doors follow
  catering, cargo doors follow loading with keep-open choices, close-on-final) and the
  jetway/stairs lifecycle (connect at start / at departure / on arrival; stairs removal
  never/always/only-jetway; remove-on-final)
- **Ground Equipment** — GPU (with-APU option), PCA tri-state (never/always/only jetway) +
  override, arrival chock delays, gradual removal, reposition-at-start
- **Pushback** — beacon-orchestrated sequence with randomized step delays, pushback
  direction preference, tug question answer, pushback-when-tug-attached
- **Questions** — FollowMe, crew boarding, pushback confirm, de-ice fluid, operator
  preferences, company hubs
- **Arrival / Fuel** — auto-deboard, stable-parked hold time, FOB save/restore per aircraft
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
| `commands.json` | file-driven voice commands | yes |
| `atc-requests.json` | SayIntentions request phrasebook | on start |
| `themes\*.json` | airline themes | on switch |
