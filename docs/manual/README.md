# ProsimCompanion — User Manual

ProsimCompanion is a single Windows companion application for the **ProSim A320 (A322)** in
MSFS 2020/2024. It consolidates three predecessor apps into one:

- **GSX ground automation** (from Prosim2GSX) — departure services, refuel/boarding sync,
  doors, jetway/stairs, pushback, arrival handling, loadsheets, web EFB
- **Flight data & EFB** — SimBrief OFP, weight & balance, takeoff/landing performance,
  ECAM-style checklists
- **Voice First Officer** (from Prosim2FO) — spoken checklists, SOP callouts, briefings,
  ECAM abnormals, radio/FCU voice control, cabin crew and company immersion
- **Audio control** — ACP knobs/latches and the loudspeaker dial driving Windows per-app
  volumes or VoiceMeeter, and a Voicemeeter on another PC through VoicemeeterBridge

Everything is configured in a **browser** (`http://localhost:5320` by default). The desktop
window is only a status panel plus the web-server settings (port, LAN access, QR onboarding) —
kept there so a broken web configuration can always be repaired.

## Manual contents

Read the groups from top to bottom. The chapter numbers in the file names are the order in which
the chapters were written, not the reading order; every chapter ends with a link to the next one.

### Getting started

| Chapter | Covers |
|---|---|
| [Installation](01-installation.md) | Installer, prerequisites, first run, migrating from Prosim2GSX / Prosim2FO |

### Flying with the app

| Chapter | Covers |
|---|---|
| [Using the app](02-using-the-app.md) | Desktop shell, tray, web UI tour, a typical flight |
| [Ground Services: the GSX settings, page by page](20-gsx-settings.md) | A guided tour of the GSX settings: what must be running, the first five minutes, then each menu item from Status to Connection & Timeouts with its main settings, defaults and a check, troubleshooting |
| [Duty Day, Logbook and Company: the airline-life features](21-duty-day-logbook-company.md) | The Duty Day page (legs, ending or discarding a day, the twelve-hour rule, the FDP warning), the Logbook and Tech Log pages with their settings, the company channel, movement messages, delay codes and the post-flight debrief |

### Voice First Officer and its servers

| Chapter | Covers |
|---|---|
| [Voice First Officer](03-voice-fo.md) | PTT, spoken checklists, callouts, briefings, persona |
| [Voices: setting up the First Officer and crew voices](10-voices.md) | The Voices page and the provider tabs: connect Kokoro, ElevenLabs, Google or the Windows voices, pick the First Officer and crew voices from live lists, fallback voices, troubleshooting |
| [ElevenLabs voices](06-elevenlabs.md) | Step-by-step setup: API key, voice, plan, crew voices, cost control |
| [Kokoro voice server: free neural voices on your own network](18-kokoro.md) | Connecting a self-hosted Kokoro voice server: what the server must offer, the Base URL exactly as it must be typed, a browser and curl check, setup on the Kokoro tab, testing on the Status page, the 60-second cooldown, troubleshooting |
| [Speech recognition server: connecting whisper.cpp or faster-whisper](16-speech-recognition-server.md) | Pointing the app at a whisper.cpp server or the faster-whisper example server: which server flavour, the address exactly as it must be typed, the wrong-flavour trap where everything stays green, a check before and inside the app, troubleshooting |
| [Language model (LLM): connecting your own server](15-language-model.md) | Pointing the app at Ollama, Open WebUI, LM Studio, llama-server or OpenAI: which API flavour, the Base URL exactly as it must be typed, the model name, thinking models, a check before and inside the app, troubleshooting |
| [SayIntentions: ATC requests, the radios and weather](17-sayintentions.md) | Switching SayIntentions on, the API key (Auto or Manual), what you can say to have the FO call ATC, the three ways to share the radios with the SayIntentions copilot, push-to-talk from a cockpit button, the arrival gate from ATC, weather, troubleshooting |
| [MCDU: "Check the box" and the FO's own side](19-mcdu-box-check.md) | The MCDU settings page: "Check the box" (the FO's D-I-F-S-R-I-P-P check, its report, the card and the corrections it offers), MCDU reading, key actuation, ILS and arrival-runway commands, the FO-side duties |
| [Flight deck options: weather, fuel, briefings, cabin crew, small talk, checklists and abnormals](26-flight-deck-options.md) | Where the switches are for the weather sources, the fuel decision and fuel check, briefings, cabin crew, cruise small talk, checklists and ECAM abnormals; their values after installation, what to change, what each needs, how to check it |
| [Scenarios: failures in flight for training](22-scenarios.md) | Scenario mode switches on a ProSim failure in flight: the one-time matching of failure names in scenarios.json, the settings, what the FO does, how to clear failures and how to switch it off |

### Audio

| Chapter | Covers |
|---|---|
| [Loudspeaker dial](08-loudspeaker-dial.md) | Headset sound on the cockpit speakers with the LOUD SPEAKER dial as their volume: the VoiceMeeter wiring (VBAN loop) and the mapping |
| [Remote mixer](09-remote-mixer.md) | Voicemeeters on other PCs (the vPilot box, or one PC per pilot) driven from the cockpit through VoicemeeterBridge agents: setup on every PC, one agent block each, knob and REC-button mappings, the status card, the two-pilot-PC example |
| [Two headsets](11-two-headsets.md) | The VoiceMeeter layout for two headsets with online ATC: one fader per strip or bus, a bus per headset, how to get INT on its own knob, why VHF2 cannot have its own bus |

### Hardware and displays

| Chapter | Covers |
|---|---|
| [Tablet: HTTPS, home screen, keep-awake](07-tablet-install.md) | Your own certificate (mkcert), trusting the CA on the iPad, Add to Home Screen, the keep-awake switch; no offline mode |
| [Stream Deck on another PC: cockpit switches and camera views](12-remote-streamdeck.md) | Running the plugin on a second PC; Cockpit Switch keys that press ProSim switches you have no hardware for (Korry buttons, toggles, knobs, drawn like the real overhead); Camera View keys that change ChasePlane views through the vJoy relay |
| [Cockpit Switches: the switches your Stream Deck keys can press](24-cockpit-switches.md) | The Cockpit Switches settings page: building the list of ProSim switches for Stream Deck keys, picking a switch, look and press mode, lamps and colours, which switches are allowed, testing a line, how saved lines reach the plugin |
| [Skalarki hardware: connecting the app to the SkalarkiIO Profiler](23-skalarki.md) | The link behind the Diagnostics PANELS page: your own SDK folder, the Profiler PC and port 53000, Test connection, choosing and naming boards, what is still unverified on hardware, troubleshooting |
| [Screen & FOV: a life-size view on your TV](14-screen-fov.md) | Enter your screen's size and distance, see the true field of view, press one button to write it into MSFS 2024's saved pilot view (with a backup), and check it in Developer Mode; Multi Window rotations and the ChasePlane caveat |

### Reference and troubleshooting

| Chapter | Covers |
|---|---|
| [General settings: notifications, appearance, flight phases, aircraft profiles, display & flight data](25-app-pages.md) | Five pages: phone notifications (ntfy, Discord, webhook), Appearance (language, themes, logos), the Advanced Flight Phase Engine page, Aircraft Profiles and how the active one is chosen, Display & Flight Data (units, loadsheets, limits) |
| [Diagnostics: the ECAM-style system pages](13-diagnostics.md) | The pages under Settings → Diagnostics (VOICE, SIM, REMOTE, GSX, STATUS, and PANELS for Skalarki hardware): how to read green / amber / red / `XX` / `- - -`, what the common cautions mean and what to do, the sidebar badge, the bundle |
| [Troubleshooting](05-troubleshooting.md) | Logs, degraded subsystems, common problems |
| [Settings reference](04-settings-reference.md) | Every settings section, including the hand-editable ones |
| [Command API and Telemetry API: driving the app from your own tools](27-command-telemetry-api.md) | The two HTTP interfaces for your own tools: switching them on, the access token, commands with curl, the answer and status codes, live state, the telemetry addresses and the session event log, a check, troubleshooting |

## Design principles (what to expect)

- **Degrade, not fail** — ProSim, MSFS, GSX, VoiceMeeter, the network: any of them can be
  absent. The affected feature disables itself with guidance in the log; the app keeps running.
- **Decision log** — every automation action (and every deliberate non-action) is recorded
  with its reason on the GSX status page. When something didn't happen, the reason is there.
- **Your files survive** — checklists, abnormals, phrases, themes and `settings.json` are
  plain JSON beside the app, editable and hot-reloaded where noted.

## Licence

ProsimCompanion is free software under the GNU GPL, version 3 or later, with a linking
exception for the ProSim SDK and the MSFS SimConnect libraries — see
[LICENSE](../../LICENSE) and [LICENSE-EXCEPTION](../../LICENSE-EXCEPTION). The ProSim SDK is
never shipped: the app loads `ProSimSDK.dll` from your own ProSim installation.
