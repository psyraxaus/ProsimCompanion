# ProsimCompanion installer

Inno Setup 6 script + payload. Build:

```powershell
.\installer\build-installer.ps1          # needs Inno Setup 6 (ISCC.exe) installed
```

The script publishes the app (framework-dependent win-x64), **fails the build if
`ProSimSDK.dll` is ever found in the payload** (the SDK is ProSim-AR's property and is loaded
at runtime from the user's ProSim installation — never redistributed), reads the version from
`Directory.Build.props`, and compiles `ProsimCompanion.iss` to
`installer/Output/ProsimCompanion-Setup-<version>.exe`.

## What the installer prompts for

| Prompt | Written to | Optional |
|---|---|---|
| `ProSimSDK.dll` location (auto-detected) | `config/settings.json` → `prosim.sdkPath` | yes — ProSim stays disabled with guidance until set on the web Settings page |
| `VoicemeeterRemote64.dll` (auto-detected) | `audio.voiceMeeterDllPath` | yes |
| Virtuali directory (default `%APPDATA%\Virtuali`) + install/overwrite GSX profiles | `<Virtuali>\Airplanes\{prosim-a322-cfm, prosim-a322-iae, Prosim-a322-neo}\gsx.cfg` | yes |

On update, an existing `settings.json` is only surgically edited for the two path keys —
everything else the user configured is untouched. Existing GSX profiles are kept unless the
overwrite box is ticked (your `gsx.cfg` edits survive updates).

## GSX payload notes

- `GSXProfiles/*/gsx.cfg` are the ProSim A322 aircraft profiles carried over from Prosim2GSX
  (data files: door positions/LVAR bindings `L:A320_door_N`, service points, `refueling = 0`
  so this app drives fuel). The directory name is GSX's match key — it must equal the
  SimObject folder name.
- `GSXProfiles/gsx_handler.py` is the in-sim handler (GSX Pro v4 tier-3, ported from
  Prosim2GSX): it pushes lifecycle events to the app's `/api/gsxmenu/events` endpoint (feeds
  the Flight Status "Last Handler Event" row + session log) and renders the flight number /
  route on the gate's VDGS from `/api/gsxmenu/flight-info`. One shared source, copied into
  every profile directory and **always refreshed on update** regardless of the
  overwrite-profiles choice (it is app-owned, not user sim config). The app rewrites the
  script's `PROSIMCOMPANION_PORT` line at startup so a non-default web port keeps working.

## Prerequisites & uninstall

- Requires the **.NET 10 Desktop Runtime** (x64); the app tells the user if it is missing.
- Per-user install (no elevation), default `%LOCALAPPDATA%\Programs\ProsimCompanion`.
- Uninstall removes the app directory only. `%LOCALAPPDATA%\ProsimCompanion` (logs, sessions,
  logbook, tech log) and the Virtuali profiles are left in place on purpose.
