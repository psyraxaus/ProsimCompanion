# ProsimCompanion — User Manual

ProsimCompanion is a single Windows companion application for the **ProSim A320 (A322)** in
MSFS 2020/2024. It consolidates three predecessor apps into one:

- **GSX ground automation** (from Prosim2GSX) — departure services, refuel/boarding sync,
  doors, jetway/stairs, pushback, arrival handling, loadsheets, web EFB
- **Flight data & EFB** — SimBrief OFP, weight & balance, takeoff/landing performance,
  ECAM-style checklists
- **Voice First Officer** (from Prosim2FO) — spoken checklists, SOP callouts, briefings,
  ECAM abnormals, radio/FCU voice control, cabin crew and company immersion
- **Audio control** — ACP knobs/latches driving Windows per-app volumes or VoiceMeeter

Everything is configured in a **browser** (`http://localhost:5320` by default). The desktop
window is only a status panel plus the web-server settings (port, LAN access, QR onboarding) —
kept there so a broken web configuration can always be repaired.

## Manual contents

| Chapter | Covers |
|---|---|
| [Installation](01-installation.md) | Installer, prerequisites, first run, migrating from Prosim2GSX / Prosim2FO |
| [Using the app](02-using-the-app.md) | Desktop shell, tray, web UI tour, a typical flight |
| [Voice First Officer](03-voice-fo.md) | PTT, spoken checklists, callouts, briefings, persona |
| [Settings reference](04-settings-reference.md) | Every settings section, including the hand-editable ones |
| [Troubleshooting](05-troubleshooting.md) | Logs, degraded subsystems, common problems |

## Design principles (what to expect)

- **Degrade, not fail** — ProSim, MSFS, GSX, VoiceMeeter, the network: any of them can be
  absent. The affected feature disables itself with guidance in the log; the app keeps running.
- **Decision log** — every automation action (and every deliberate non-action) is recorded
  with its reason on the GSX status page. When something didn't happen, the reason is there.
- **Your files survive** — checklists, abnormals, phrases, themes and `settings.json` are
  plain JSON beside the app, editable and hot-reloaded where noted.
