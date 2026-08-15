# Settings reference

All configuration lives in **`config\settings.json`** beside the app, camelCase, one section
per feature. The web pages cover the everyday sections; everything is also hand-editable —
missing keys fall back to safe defaults, and the app adds newly available defaults to the
file on start. **Never share your `webUi.accessToken`.**

| Section | Page | Highlights |
|---|---|---|
| `prosim` | App Settings | `sdkPath` (ProSimSDK.dll), `host`, optional `apiKey` |
| `webUi` | desktop window | `port` (default 5320), `bindToAllInterfaces`, `accessToken`, theme, units, Solari animation |
| `gsx` | GSX tab | the whole ground pillar — see below |
| `audio` | Audio tab | backend (CoreAudio/VoiceMeeter), app mappings, ACP side, device blacklist, device-filter flow/state |
| `speech` | First Officer tab | TTS providers/voices, output/input devices, recognition, PTT/ATC-mute bindings, sterile cockpit |
| `voices` | First Officer tab | purser/company voices + chimes |
| `checklists` | Checklists | checklist sets, manual-override allowance |
| `sop` | file | callout texts/thresholds, flow monitor, stabilized gates, weather advisories |
| `briefing` | file | departure/arrival overrides, DFD path (`dfdPath`), minima capture, LLM endpoint (`llm*`) |
| `persona` | file | FO persona — enabled, name, experience, formality, chattiness, style toggles |
| `sayIntentions` | file | API key source, phraseology, auto-tune, departure gating, weather |
| `cabin`, `company` | file | purser reports, ambient events, company loadsheet/cruise messages, chimes |
| `flightData` | file | SimBrief fetch attempts, prelim/final loadsheet triggers and delays |
| `techLog`, `logbook`, `debrief`, `day` | Tech Log / Duty Day | MEL categories, random wear, debrief verbosity, day mode |
| `updateCheck` | file | update banner enable + interval |
| `logging` | App Settings | per-subsystem levels (hot-reload), `wireTrace` |
| `commandApi` | App Settings | HTTP command API for the Stream Deck plugin (`enabled` checkbox, applies live; off by default; token-gated even on localhost — `requireTokenOnLoopback` is file-only) |

## The `gsx` section

Ground behaviour, in the GSX tab's rail order:

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
