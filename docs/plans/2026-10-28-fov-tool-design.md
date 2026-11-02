# Display & FOV tool — design

## Goal

A settings page in ProsimCompanion that takes the pilot's physical display geometry (one or more TVs/monitors, each with size, distance, yaw and height offset) and (a) computes the true-to-life field of view, (b) applies it to MSFS 2024 with one button by writing the aircraft's custom `cameras.cfg`, and (c) reports what to type into the Multi Window dialog and ChasePlane where those are in play.

JC's own case: hardware A320 flight deck, 52" 16:9 TV, eye to screen centre 1.5 m. The TV is the forward window only, so a true 1:1 view is what's wanted even though it is much tighter than the sim default.

## Facts the design rests on

| Fact | Source |
|---|---|
| MSFS computes camera FOV from zoom as `FOV = arctan(InitialZoom × 3.3125)`. This is the **vertical** FOV. Inverse: `InitialZoom = tan(vFOV) / 3.3125`. | MSFS 2024 SDK, `cameras.cfg` reference |
| Sanity check: common default `InitialZoom=0.35` → 49.2°, matching the ~50° vertical the community measured on Asobo aircraft. | MSFS forums "The same initial zoom in every airplane" |
| The in-sim zoom slider is relative to `InitialZoom` (always shows 50 after a save) and is useless as a target. What a saved pilot view actually persists is a `[CAMERADEFINITION.N]` block with its own `InitialZoom`. | AVSIM "How to easily set up a realistic FoV" |
| Custom pilot views for MSFS 2024 are written per aircraft under `<LocalCache>\SimObjects\Airplanes\<aircraft folder>\…\cameras.cfg` (observed nested as `…\presets\<creator>\<livery>\config\cameras.CFG` for Asobo aircraft; may differ for third-party). Store LocalCache = `%LOCALAPPDATA%\Packages\Microsoft.Limitless_8wekyb3d8bbwe\LocalCache`; Steam = `%APPDATA%\Microsoft Flight Simulator 2024\LocalCache` (verify on a Steam install). `MsfsPackageRoots` already resolves these roots. | MSFS forums "Location of MSFS 2024 custom camera config files" |
| MSFS holds vFOV constant and derives hFOV from the window aspect ratio — only the vertical value needs computing/writing. `WideViewAspect` only extends the zoom-out range; ignore it. | MSFS forums FOV guide |
| Multi Window (MSFS 2024, no longer "experimental") supports **one FOV for all render windows on a PC**; each extra window gets lateral/vertical rotation offsets. Fly Elise-ng's export clamps multi-view vFOV to 26°–104°. | Fly Elise-ng MSFS2020/2024 Multi-View Integration |
| Dev-mode verification: Developer Mode → Debug → Aircraft → Camera Blend shows the live vertical `Fov` in **radians**. | AVSIM thread |
| ChasePlane for MSFS 2024 (Dec 2025) keeps its presets in `<LocalState>\WASM\MSFS2024\p42-util-chaseplane\work\` (Store) or `%APPDATA%\Microsoft Flight Simulator 2024\WASM\MSFS2024\p42-util-chaseplane\work\` (Steam). Undocumented format — **never write to it**. CP cockpit presets carry their own zoom and override the native view when recalled. | Parallel 42 wiki "Where are the files" |

## Maths (pure, unit-tested)

For a flat display with diagonal `d` (metres), aspect `w:h`, eye-to-centre distance `D`:

```
height  = d / sqrt(1 + (w/h)^2)
width   = height * (w/h)
vFOV    = 2 * atan(height / (2D))
hFOV    = 2 * atan(width  / (2D))
InitialZoom = tan(vFOV) / 3.3125
pitchOffsetDeg = atan(centreHeightAboveEye / D)   // positive = screen centre above eye → camera must look UP, i.e. InitialPbh pitch negative (MSFS: positive pitch looks down)
```

Realism blend `b` (0–1, default 1): `vFOV_applied = vFOV_default_49.2 + b * (vFOV_true − 49.2)`. Shown in the UI as "Realism: 100 %".

Reference values for JC: panel 1.151 × 0.648 m, vFOV 24.4°, hFOV 42.0°, InitialZoom 0.137, dev-mode `Fov` 0.426 rad.

Multi-display: compute per display. Because MSFS allows one FOV per PC, the applied value is the **main** display's; the page shows each other display's ideal vFOV and the delta so a mismatched TV is visible. Each secondary display also gets `lateralRotationDeg = yaw` and `verticalRotationDeg = pitchOffset` for the Multi Window dialog.

## Data model (`DisplayOptions`, section `display`)

```
Realism            double  1.0
Displays           List<DisplayEntry>
  Name             string   "Main"
  Role             enum     Main | Left | Right | Overhead | Other   (exactly one Main)
  DiagonalInches   double   52
  AspectWidth/AspectHeight int 16/9   (or derive from native px if the user prefers entering W×H)
  DistanceMetres   double   1.5
  YawDegrees       double   0     (+ = right of straight ahead)
  CentreAboveEyeMetres double 0
ApplyPitch         bool     false  (write InitialPbh pitch as well as InitialZoom — off by default, opt-in)
```

Persist via the existing `SettingsWriter` / `OptionsDraft` mechanism; no new file.

## Apply flow ("Apply realistic FOV" button)

1. **Precondition**: the pilot has saved the VFR pilot view once in-sim (Ctrl+F10) so MSFS creates the camera block with the correct GUID and eye-point XYZ for the loaded aircraft. The page checks whether a candidate `cameras.cfg` exists and, if not, shows the instruction instead of the button.
2. **Locate the file**: aircraft folder from the ProSim/sim aircraft title already surfaced as `AircraftTitle` (fall back to a dropdown of folders under `SimObjects\Airplanes` in the LocalCache root when matching fails). Search that folder recursively for `cameras.cfg` (case-insensitive); if several, prefer the most recently modified and show the chosen path.
3. **Edit**: back up to `cameras.cfg.bak-<yyyyMMdd-HHmmss>` in the same folder (keep the last 5). Rewrite **only** the `InitialZoom=` line (and, when `ApplyPitch`, only the pitch component of `InitialPbh=`) inside every `[CAMERADEFINITION.N]` whose `Category="Cockpit"` and whose `SubCategoryItem` is one of `VFRPilot`, `IFRPilot`, `LandingPilot`, `ClosePilot` (match case-insensitively, tolerate quotes/no quotes and spaces around `=`). Preserve every other byte: line endings, ordering, comments, unknown keys. Write atomically (temp file + replace).
4. **Nudge the sim** (best effort, when SimConnect is connected): write `COCKPIT CAMERA ZOOM` = 50 and set `CAMERA ACTION COCKPIT VIEW RESET` = 1 so the change is visible without reloading. These names go on `SimWriteGate`'s exact allow-list. If either is rejected by the sim, log at Debug and tell the user "takes effect on next aircraft load".
5. **Verify panel**: show the expected dev-mode `Fov` in radians with the three-click path to read it, plus the raw `InitialZoom` written.

Never touch files under `Official\`, `Community\` or any package folder — only the LocalCache custom-camera file.

## Multi Window & ChasePlane

- Multi Window: phase 1 is **read-out only** — a table of per-window lateral/vertical rotation and the single shared FOV, with a note that the main window's zoom is what all windows use. Phase 2 (writing MSFS's own window config) is deferred until the persisted format has been diffed on JC's rig.
- ChasePlane: detection only. If the CP work folder exists, show a one-line notice that CP cockpit presets override the native FOV and give the target `Fov` (rad) to match in CP via the dev-mode readout. Suggest leaving the cockpit view native and using CP for external cameras.

## Settings UI

New rail group `display` ("Display & FOV", `settings/display`) placed after Cockpit Switches, sections: `displays` (table + add/remove), `apply` (computed values, realism slider, Apply button, verification), `multiwindow`, `chaseplane`. Follows ADR-0010 hub conventions and the ADR-0014 language-pack ids `settings.nav.display.*`.

## Verification on JC's rig before relying on it

- Edited LocalCache `cameras.cfg` survives a sim restart for the ProSim A320 visual model.
- A 24.4° view is not clamped anywhere in single-window mode.
- Steam-edition LocalCache path.
- `COCKPIT CAMERA ZOOM` / `CAMERA ACTION COCKPIT VIEW RESET` are accepted as writes in MSFS 2024.

## Out of scope

VR, curved/cylindrical screens, asymmetric frustums, projector warping (Fly Elise territory), writing ChasePlane data, P3D.
