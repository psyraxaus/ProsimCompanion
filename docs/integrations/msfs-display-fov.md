# MSFS 2024 field of view from the pilot's screens (Settings → Screen & FOV)

One owner story (2026-10-28, design `claude/fov-tool-design.md`): a hardware A320 flight deck
with a 52" 16:9 TV as the forward window, eye to screen centre 1.5 m. A true 1:1 view through
that "window" is a vertical FOV of 24.4° — far tighter than the sim's ~49° default, and the
in-sim zoom slider cannot be set to it reliably (it is relative to the saved view's own zoom and
always reads 50 after a save). The page turns the measured screen into the number MSFS actually
persists, writes it into the saved pilot view, and tells the pilot how to verify it.

## The maths (`Core/Display/FovCalculator.cs`, pure, unit-tested)

| Fact | Source |
|---|---|
| MSFS derives a camera's **vertical** FOV from its zoom: `FOV = arctan(InitialZoom × 3.3125)`. Inverse: `InitialZoom = tan(vFOV) / 3.3125`. | MSFS 2024 SDK, `cameras.cfg` reference |
| The common default `InitialZoom = 0.35` gives 49.2°, matching the ~50° vertical the community measured on Asobo aircraft. This is the realism blend's anchor. | MSFS forums, "The same initial zoom in every airplane" |
| MSFS holds vFOV constant and derives hFOV from the window aspect — only the vertical value is written. `WideViewAspect` only extends the zoom-out range; ignored. | MSFS forums FOV guide |
| Dev-mode readout: Developer Mode → Debug → Aircraft → Camera Blend shows the live vertical `Fov` in **radians**. | AVSIM "How to easily set up a realistic FoV" |

For a flat screen with diagonal `d` (inches → metres), aspect `w:h`, eye-to-centre distance `D`
and centre height above the eye `c`:

```
height = d / sqrt(1 + (w/h)²)      width = height × (w/h)
vFOV   = 2 × atan(height / 2D)     hFOV  = 2 × atan(width / 2D)
InitialZoom     = tan(vFOV_applied) / 3.3125
vFOV_applied    = 49.2 + realism × (vFOV − 49.2)          realism 0–1, default 1
pitchOffset     = atan(c / D)      positive = screen above the eye → the camera looks UP,
                                   which is a NEGATIVE InitialPbh pitch (MSFS: positive = down)
```

Reference rig: panel 1.151 × 0.648 m, vFOV 24.4°, hFOV 42.0°, `InitialZoom` 0.1367, dev-mode
`Fov` 0.426 rad (`FovCalculatorTests`).

Multi-display: MSFS 2024 renders **one FOV for every render window on a PC**, so the applied
value is the Main display's. Every other row gets its ideal vFOV, the delta from the applied
value (a mismatched side TV is visible), and `lateralRotation = yaw`,
`verticalRotation = pitchOffset` for the Multi Window dialog. Phase 1 is read-out only; writing
MSFS's own window configuration is deferred until its persisted format has been diffed on the
owner's rig.

## The file (`Core/Display/CamerasCfgEditor.cs`)

What a saved pilot view persists is a `[CAMERADEFINITION.N]` block with its own `InitialZoom`
in a per-aircraft custom-camera file under the sim's **LocalCache**:

| Build | LocalCache root |
|---|---|
| Store / Xbox app | `%LOCALAPPDATA%\Packages\Microsoft.Limitless_8wekyb3d8bbwe\LocalCache` |
| Steam | `%APPDATA%\Microsoft Flight Simulator 2024\LocalCache` (from the design doc — **unverified** on a Steam install) |

The file is `<root>\SimObjects\Airplanes\<aircraft folder>\…\cameras.cfg`. On the owner's sim
PC (2026-10-29) it sits directly in the aircraft folder:
`LocalCache\SimObjects\Airplanes\Prosim-a322-cfm\cameras.CFG`, beside `state.CFG`; the
`…\presets\<creator>\<livery>\config\` nesting the forums describe for Asobo aircraft was not
seen, so the search is recursive and case-insensitive to cover both.
`MsfsPackageRoots.DetectLocalCache2024()` lists the roots that exist (a sibling of `Detect()`,
which keeps answering with the package roots for the GSX profile reader);
`CamerasCfgEditor.FindCandidateFiles(root, hint)` narrows to the aircraft folders whose name and
the ProSim aircraft title contain one another and lists every aircraft when none matches,
newest file first.

**What MSFS 2024 actually writes** (archaeology, owner's sim PC 2026-10-29 — the design doc's
"Ctrl+F10 saves a VFRPilot block" did nothing on that install): the file holds the **custom
cameras** saved with Ctrl+Alt+1 … 9, recalled with Alt+1 … 9. One block each:

```
[CAMERADEFINITION.0]
Title="1"                      ; the slot — Alt+1 recalls it
Guid="{…}"
Origin="Virtual Cockpit"
AllowZoom=1
InitialZoom=0.35               ; the sim's default, whatever the slider showed
Category="Cockpit"
SubCategory="Custom"
SubCategoryItem="None"
InitialXyz= -0.000218, 0.700349, 0.019997
InitialPbh= 0.999755, 0.055953, -0
```

CRLF, no BOM, `major=1` / `minor=0` in `[VERSION]`. The built-in pilot views (`DefaultPilot`
for the ProSim A322, `LandingPilot`, and Asobo's `VFRPilot` / `IFRPilot` / `ClosePilot`) only
ever live in the package's own `cameras.cfg` under `Community\prosim-a322\…`, which is
read-only to this feature. So the pilot saves a custom camera once, the page sets that camera's
zoom, and the pilot recalls it with Alt+N — the result card names the chord. Until a file
exists the page shows those steps instead of the button.

The edit is surgical: within every block whose `Category` is `Cockpit` and which is either a
custom camera (`SubCategory` = `Custom`) or a pilot view (`SubCategoryItem` in `DefaultPilot`,
`VFRPilot`, `IFRPilot`, `LandingPilot`, `ClosePilot`) — case ignored, quotes optional, spaces
around `=` tolerated, keys in any order; two passes, since the keys that qualify a block may
follow its zoom line — only the value of `InitialZoom=` changes (formatted with up to four
decimals), plus the first component of `InitialPbh=` when "Also tilt the camera" is on — bank
and heading keep their original text and separators. Every other byte survives: unknown keys,
comments, ordering, the line-ending style (CRLF or LF, detected per line) and a BOM (the file
is read as raw bytes so the BOM stays a character of the text and is written back as such). A
file with no qualifying block changes zero blocks; the page then lists every camera block
`CamerasCfgEditor.Describe` found (header, title, category, sub-category, item) so the pilot
sees what the sim wrote instead of a bare refusal.

**Write safety.** `CamerasCfgEditor.IsWritablePath` admits only a `cameras.cfg` whose full
path contains `\LocalCache\SimObjects\Airplanes\` and contains neither `\Official\`,
`\Community\` nor `p42-util-chaseplane`; `WriteWithBackup` throws for anything else. A
package's own file, and ChasePlane's folder, cannot be reached from this feature by
construction. The write copies the file to `cameras.cfg.bak-yyyyMMdd-HHmmss` beside itself,
prunes to the newest five backups, then writes a `.tmp` and `File.Move(overwrite)`s it over the
original. The page asks for confirmation (showing the zoom and the path) before writing.

## The sim nudge (`Sim/FovSimNudge.cs`, `IFovSimNudge`)

After the write, best effort and never a reason to refuse it: `COCKPIT CAMERA ZOOM` = 50
(`percent`) then `CAMERA ACTION COCKPIT VIEW RESET` = 1 (`Bool`). For a custom camera this is
only a zoom-slider reset: the sim reads `cameras.cfg` when the aircraft loads and the camera is
applied when the pilot recalls it (Alt+N), so the page's verification steps start with "press
Alt+N; if nothing changes, reload the aircraft". Both names are on `SimWriteGate.AllowedExact` (camera state only,
nothing on the aircraft). Each is subscribed through its typed descriptor
(`ProsimDataRefNames.SimVars.CockpitCameraZoom` / `CameraActionCockpitViewReset`) on the first
Apply — that fixes the SimConnect unit, where a bare write would register the name as a plain
number — and never at startup. Not connected, or a refused write, logs at Debug and the page says
"takes effect on the next aircraft load". **Both writes are unverified on MSFS 2024** (see the
list below).

## ChasePlane

Parallel 42's ChasePlane for MSFS 2024 keeps its presets under
`…\LocalState\WASM\MSFS2024\p42-util-chaseplane\work` (Store) or
`%APPDATA%\Microsoft Flight Simulator 2024\WASM\MSFS2024\p42-util-chaseplane\work` (Steam).
The format is undocumented and is **never written**; `MsfsPackageRoots.ChasePlaneDetected()` is
a folder-exists probe and nothing more. A recalled CP cockpit preset carries its own zoom and
overrides the native view, so the page's ChasePlane section shows the target dev-mode `Fov`
(radians) to match by hand and suggests leaving the cockpit view native and using CP for the
external cameras only.

## Settings (`display`, `DisplayOptions`)

- `realism` (1.0): the blend, shown as a 0–100 % slider.
- `displays[]`: `name`, `role` (`main` | `left` | `right` | `overhead` | `other`; exactly one
  `main`), `diagonalInches`, `aspectWidth` / `aspectHeight`, `distanceMetres`, `yawDegrees`
  (+ = right), `centreAboveEyeMetres`. Default: one `Main`, 32", 16:9, 0.8 m — a neutral
  screen, nobody's rig.
- `applyPitch` (false): also write the `InitialPbh` pitch.

Rail: Settings → Screen & FOV (`settings/display`, group key `display`, labelled "Screen" so it
does not read as a twin of "Display & Flight Data"), sections `displays` / `apply` /
`multiwindow` / `chaseplane`.

## Verification (dev-mode `Fov` before and after)

1. Load the aircraft, position the cockpit view, Ctrl+Alt+1. Options → General → Developers →
   Developer Mode on; Debug → Aircraft → Camera Blend: note `Fov` (≈ 0.86 rad for the 0.35
   default). The ProSim A322 CFM LocalCache file on the owner's PC held one such camera, "1".
2. Settings → Screen & FOV → Displays: enter the screen, Save. Apply FOV: check the readout,
   Refresh, pick the file, Apply, confirm. The result card says "Load the camera with Alt+1".
3. Alt+1 (reload the aircraft first if the view does not change). Camera Blend `Fov` should now
   read the page's "Expected dev-mode Fov" (0.449 rad for a 55" 16:9 at 1.5 m).

Still to be verified on the owner's rig before relying on it: Alt+1 applies the new zoom (and
whether an aircraft reload is needed first); the edited LocalCache file survives a sim restart;
a ~25° view is not clamped in single-window mode; the Steam-edition LocalCache path; whether
MSFS 2024 accepts the two nudge writes.

## Out of scope

VR, curved screens, asymmetric frustums, projector warping (Fly Elise territory), writing
ChasePlane data, writing MSFS's Multi Window configuration, P3D, MSFS 2020.
