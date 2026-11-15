# Installation

## Prerequisites

- Windows 10/11 x64
- **.NET 10 Desktop Runtime (x64)** — the app will tell you if it is missing
- ProSim A320 (A322) with the **ProSim SDK** (`ProSimSDK.dll` ships with ProSim; this app
  never bundles it)
- MSFS 2020 or 2024 with **GSX Pro** (for the ground-services pillar; optional)
- Optional: VoiceMeeter (audio routing), a Kokoro voice server and a speech recognition server on your network (local TTS/ASR),
  Navigraph DFD database (briefing procedures), SayIntentions (ATC integration),
  VoicemeeterBridge on a second PC's Voicemeeter ([chapter 9](09-remote-mixer.md))

## Running the installer

`ProsimCompanion-Setup-<version>.exe` installs per-user (no administrator rights — do **not**
run it elevated). The wizard asks for:

1. **ProSimSDK.dll location** — auto-detected from the usual ProSim install locations. Leave
   empty if unsure: the ProSim connection stays disabled with guidance until you set
   **ProSim SDK folder** (the folder that contains `ProSimSDK.dll`) on Settings → Setup, card
   **ProSim Connection**. *Fresh installs only — updates skip this page.*
2. **VoicemeeterRemote64.dll** — only if you want the VoiceMeeter audio backend. Optional.
   *Fresh installs only.*
3. **Virtuali directory** (default `%APPDATA%\Virtuali`) and whether to install the **GSX
   aircraft profiles** for the ProSim A322 (`prosim-a322-cfm`, `prosim-a322-iae`,
   `Prosim-a322-neo`). These profiles give GSX the door positions and the door-LVAR contract
   it needs. Profiles you never edited update automatically when a new version changes them;
   your own `gsx.cfg` edits are kept unless you tick *overwrite*.

The wizard also offers a desktop icon and, when the Elgato Stream Deck app is on this PC and
the plugin is missing or out of date, the ProsimCompanion Stream Deck plugin.

The two DLL paths land in `config\settings.json` beside the app; the Virtuali directory is
used for the profile copy only and is not stored. Updates never
modify an existing `settings.json` or re-ask for the paths already in it. API keys and the
LAN access token in that file are encrypted for your Windows account (DPAPI); a `settings.json`
copied to another PC or account keeps every path and preference, but the keys must be
re-entered on the settings pages (see the [settings reference](04-settings-reference.md)).

## First run

1. Start ProsimCompanion (Start menu / desktop icon). The status window opens and the tray
   icon appears.
2. Open the web UI (**Open Web UI** button, or `http://localhost:5320`).
3. On first run the app **imports your predecessor configuration automatically** if it finds:
   - Prosim2GSX: `%APPDATA%\Prosim2GSX\AppConfig.json` — SDK path, ProSim host name,
     VoiceMeeter, audio mappings and the audio device blacklist, saved fuel-on-board, and the
     GSX behaviour block (doors, jetway, pushback, refuel, departure services, operators and
     hubs) of the aircraft profile named "default", or of the first profile when none has
     that name
   - Prosim2FO: `%LOCALAPPDATA%\Programs\Prosim2FO\config\settings.json` or
     `C:\Prosim2FO\config\settings.json` (an install in another folder is not found) —
     ProSim connection and API key, audio input and output devices, TTS endpoints and voices,
     recognition endpoint, keyboard PTT and ATC-mute keys, LLM endpoint and API key, nav-data
     path, SayIntentions settings, persona
   The import runs once (it records a `predecessorImport` marker in `settings.json`; delete
   that section and restart to re-run it). Joystick PTT bindings cannot be carried over —
   re-bind them on Settings → Voice First Officer → Listening & PTT, with **Also listen for
   joystick buttons** switched on.
4. Check the connection dots in the footer of any page (**ProSim**, **SimConnect**, **GSX**;
   the footer is hidden on phones): they go green as each partner comes up.

## Phone / tablet access (LAN)

In the desktop window: enable LAN access, save, restart, then scan the QR code. Access from
other devices is protected by a bearer token (embedded in the QR link); localhost is always
exempt. Regenerating the token invalidates previously connected devices.

## Updating & uninstalling

- Run a newer installer over the existing installation — settings, checklists and GSX
  profile edits are preserved. The web UI shows a banner when a newer release is on GitHub.
- Uninstalling removes the application folder and the Stream Deck plugin.
  Logs/sessions/logbook/tech log and your edited content (checklists, phrases, themes) under
  `%LOCALAPPDATA%\ProsimCompanion` and the GSX profiles under `%APPDATA%\Virtuali` stay.

---

**Next:** [Using the app](02-using-the-app.md) · **See also:** [Tablet](07-tablet-install.md), [Troubleshooting](05-troubleshooting.md) · [Manual contents](README.md)
