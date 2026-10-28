# Screen & FOV: a life-size view on your TV

If your forward "window" is a TV in front of a hardware cockpit, the sim's default view shows
far more of the world than the real window would — about 49° top to bottom, where a 52" TV at
1.5 m covers only 24°. The result is a shrunken, fish-eyed outside world and runway lights that
look twice as far away as they are. Settings → **Screen & FOV** measures your screen and writes
the matching field of view into the sim's own saved pilot view. Nothing is guessed in the sim's
zoom slider, and nothing is written outside your own saved-camera file.

## 1. Enter your screens

Settings → Screen & FOV → **Displays**: one line per physical screen.

- **Role** — the screen straight ahead is **Main**. Exactly one line must be Main: it decides
  the field of view. Side and overhead screens are only listed for the Multi Window section.
- **Diagonal** — the size on the box, in inches (tick *Show the diagonal in centimetres* if you
  prefer).
- **Aspect W : H** — 16 and 9 for a normal TV, 21 and 9 for an ultrawide.
- **Distance** — from your eye to the centre of the screen, in metres, measured straight at it.
  Sit as you fly and measure; this number matters most.
- **Yaw** — 0 for the forward screen; for a side screen, how many degrees left (negative) or
  right (positive) its centre sits.
- **Centre above eye** — 0 when the screen centre is level with your eyes; positive when it is
  higher, negative when lower.

Save.

## 2. Save a cockpit camera in the sim (once per aircraft)

MSFS only creates the file this page edits when you save a custom camera:

1. Load the aircraft in MSFS 2024.
2. Put the cockpit view where you want it (eye point, not zoom — the zoom is what the page sets).
3. Press **Ctrl+Alt+1** (save custom camera 1). You recall it later with **Alt+1**.

The sim's built-in pilot view lives in the aircraft package and is never edited; the saved
custom camera is the one that carries your field of view.

## 3. Apply

Settings → Screen & FOV → **Apply FOV**.

- **Realism** — 100 % is life-size: you see exactly what the real window would show. That is
  what a hardware cockpit wants, even though it is much tighter than the default. Lower values
  blend towards the sim default if you would rather keep more of the world in view.
- **Also tilt the camera** — only if your screen centre is above or below your eyes and you
  want the camera pitched to look straight at it. Off keeps the pitch of your saved view.
- **What will be written** shows the panel size, the true and applied vertical field of view,
  the `InitialZoom` that will go into the file and the `Fov` in radians you should see in
  Developer Mode afterwards.
- **The saved cockpit camera** lists the camera file(s) MSFS saved for the aircraft ProSim
  reports (newest first). Press **Refresh** after step 2 if it says there is none yet.
- Press **Apply realistic FOV**, check the zoom and the path in the confirmation line, and
  press **Yes, write it**. The page backs the file up beside itself first
  (`cameras.cfg.bak-<date>`, the last five are kept) and changes only the zoom (and the pitch,
  if ticked) of the saved cockpit cameras. The result card names the key that loads each
  camera (**Alt+1** for camera 1). If nothing in the file qualified, the card lists every
  camera block it found so you can see what the sim wrote.

## 4. Check it

Press **Alt+1** to load the camera. If the view does not change, reload the aircraft (the sim
reads the file when the aircraft loads) and press Alt+1 again. Then, in MSFS: Options → General
→ Developers → **Developer Mode** on. In the dev menu bar: **Debug → Aircraft → Camera
Blend**. The `Fov` line is the live vertical field of view in radians; it should read the
page's "Expected dev-mode Fov" (0.449 rad for a 55" 16:9 screen at 1.5 m). Reading it once
*before* you apply (about 0.86 rad for the default view) makes the difference obvious.

## Multi Window

Settings → Screen & FOV → **Multi Window** lists, for every screen that is not Main, the
lateral and vertical rotation to type into MSFS 2024's Multi Window dialog, the field of view
that screen would ideally have, and how far it is from the one the sim uses. MSFS renders every
window with one field of view — the Main screen's — so a side TV of a different size or
distance is always a compromise; the delta shows how big a one.

## ChasePlane

If ChasePlane for MSFS 2024 is installed the **ChasePlane** section says so. Its cockpit
presets carry their own zoom and override the native view when you recall one, so the file this
page writes is not what ChasePlane shows. Either leave the cockpit view native (do not recall a
ChasePlane cockpit preset) and use ChasePlane for the external cameras, or adjust the cockpit
preset's zoom in ChasePlane until the dev-mode `Fov` reads the target the section shows. The
app never writes to ChasePlane's files.

## What is never touched

Only the saved-camera `cameras.cfg` under your MSFS 2024 LocalCache folder is edited, and only
the `InitialZoom` (and optionally `InitialPbh` pitch) lines of the cockpit cameras in it.
Aircraft package files under `Official` and `Community`, and ChasePlane's folder, are refused
by the app itself. Every write makes a backup first.
