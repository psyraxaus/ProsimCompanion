# Installation

## Prerequisites

- Windows 10/11 x64
- **.NET 10 Desktop Runtime (x64)** — the app will tell you if it is missing
- ProSim A320 (A322) with the **ProSim SDK** (`ProSimSDK.dll` ships with ProSim; this app
  never bundles it)
- MSFS 2020 or 2024 with **GSX Pro** (for the ground-services pillar; optional)
- Optional: VoiceMeeter (audio routing), a Kokoro/faster-whisper LAN box (local TTS/ASR),
  Navigraph DFD database (briefing procedures), SayIntentions (ATC integration)

## Running the installer

`ProsimCompanion-Setup-<version>.exe` installs per-user (no administrator rights — do **not**
run it elevated). The wizard asks for:

1. **ProSimSDK.dll location** — auto-detected from the usual ProSim install locations. Leave
   empty if unsure: the ProSim connection stays disabled with guidance until you set it on
   the web Settings page. *Fresh installs only — updates skip this page.*
2. **VoicemeeterRemote64.dll** — only if you want the VoiceMeeter audio backend. Optional.
   *Fresh installs only.*
3. **Virtuali directory** (default `%APPDATA%\Virtuali`) and whether to install the **GSX
   aircraft profiles** for the ProSim A322 (`prosim-a322-cfm`, `prosim-a322-iae`,
   `Prosim-a322-neo`). These profiles give GSX the door positions and the door-LVAR contract
   it needs. Profiles you never edited update automatically when a new version changes them;
   your own `gsx.cfg` edits are kept unless you tick *overwrite*.

Everything the wizard collects lands in `config\settings.json` beside the app — updates never
modify an existing `settings.json` or re-ask for the paths already in it. API keys and the
LAN access token in that file are encrypted for your Windows account (DPAPI); a `settings.json`
copied to another PC or account keeps every path and preference, but the keys must be
re-entered on the settings pages (see the [settings reference](04-settings-reference.md)).

## First run

1. Start ProsimCompanion (Start menu / desktop icon). The status window opens and the tray
   icon appears.
2. Open the web UI ("Open browser" button, or `http://localhost:5320`).
3. On first run the app **imports your predecessor configuration automatically** if it finds:
   - Prosim2GSX: `%APPDATA%\Prosim2GSX\AppConfig.json` — SDK path, VoiceMeeter, audio
     mappings, saved fuel-on-board, and the whole GSX behaviour block (doors, jetway,
     pushback, refuel, departure services)
   - Prosim2FO: its `config\settings.json` — ProSim connection, TTS endpoints and voices,
     recognition endpoint, keyboard PTT binding, LLM endpoint, nav-data path, persona
   The import runs once (it records a `predecessorImport` marker in `settings.json`; delete
   that section and restart to re-run it). Joystick PTT bindings cannot be carried over —
   re-bind them on the Speech settings page.
4. Check the Flight Status page: ProSim / Sim / GSX connection dots go green as each partner
   comes up.

## Phone / tablet access (LAN)

In the desktop window: enable LAN access, save, restart, then scan the QR code. Access from
other devices is protected by a bearer token (embedded in the QR link); localhost is always
exempt. Regenerating the token invalidates previously connected devices.

## Updating & uninstalling

- Run a newer installer over the existing installation — settings, checklists and GSX
  profile edits are preserved. The web UI shows a banner when a newer release is on GitHub.
- Uninstalling removes the application folder only. Logs/sessions/logbook/tech log under
  `%LOCALAPPDATA%\ProsimCompanion` and the GSX profiles under `%APPDATA%\Virtuali` stay.
